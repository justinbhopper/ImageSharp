// Copyright (c) Six Labors and contributors.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

using SixLabors.ImageSharp.Memory;

namespace SixLabors.ImageSharp.Formats.WebP
{
    /// <summary>
    /// Implements decoding for lossy alpha chunks which may be compressed.
    /// </summary>
    internal class AlphaDecoder : IDisposable
    {
        private readonly MemoryAllocator memoryAllocator;

        /// <summary>
        /// Initializes a new instance of the <see cref="AlphaDecoder"/> class.
        /// </summary>
        /// <param name="width">The width of the image.</param>
        /// <param name="height">The height of the image.</param>
        /// <param name="data">The (maybe compressed) alpha data.</param>
        /// <param name="alphaChunkHeader">The first byte of the alpha image stream contains information on how to decode the stream.</param>
        /// <param name="memoryAllocator">Used for allocating memory during decoding.</param>
        /// <param name="configuration">The configuration.</param>
        public AlphaDecoder(int width, int height, IMemoryOwner<byte> data, byte alphaChunkHeader, MemoryAllocator memoryAllocator, Configuration configuration)
        {
            this.Width = width;
            this.Height = height;
            this.Data = data;
            this.memoryAllocator = memoryAllocator;
            this.LastRow = 0;
            int totalPixels = width * height;

            var compression = (AlphaCompressionMethod)(alphaChunkHeader & 0x03);
            if (compression != AlphaCompressionMethod.NoCompression && compression != AlphaCompressionMethod.WebPLosslessCompression)
            {
                WebPThrowHelper.ThrowImageFormatException($"unexpected alpha compression method {compression} found");
            }

            this.Compressed = compression is AlphaCompressionMethod.WebPLosslessCompression;

            // The filtering method used. Only values between 0 and 3 are valid.
            int filter = (alphaChunkHeader >> 2) & 0x03;
            if (filter < (int)WebPAlphaFilterType.None || filter > (int)WebPAlphaFilterType.Gradient)
            {
                WebPThrowHelper.ThrowImageFormatException($"unexpected alpha filter method {filter} found");
            }

            this.Alpha = memoryAllocator.Allocate<byte>(totalPixels);
            this.AlphaFilterType = (WebPAlphaFilterType)filter;
            this.Vp8LDec = new Vp8LDecoder(width, height, memoryAllocator);

            if (this.Compressed)
            {
                var bitReader = new Vp8LBitReader(data);
                this.LosslessDecoder = new WebPLosslessDecoder(bitReader, memoryAllocator, configuration);
                this.LosslessDecoder.DecodeImageStream(this.Vp8LDec, width, height, true);
                this.Use8BDecode = Is8BOptimizable(this.Vp8LDec.Metadata);
            }
        }

        /// <summary>
        /// Gets the the width of the image.
        /// </summary>
        public int Width { get; }

        /// <summary>
        /// Gets the height of the image.
        /// </summary>
        public int Height { get; }

        /// <summary>
        /// Gets the used filter type.
        /// </summary>
        public WebPAlphaFilterType AlphaFilterType { get; }

        /// <summary>
        /// Gets or sets the last decoded row.
        /// </summary>
        public int LastRow { get; set; }

        /// <summary>
        /// Gets or sets the row before the last decoded row.
        /// </summary>
        public int PrevRow { get; set; }

        /// <summary>
        /// Gets information for decoding Vp8L compressed alpha data.
        /// </summary>
        public Vp8LDecoder Vp8LDec { get; }

        /// <summary>
        /// Gets the decoded alpha data.
        /// </summary>
        public IMemoryOwner<byte> Alpha { get; }

        public int CropTop { get; }

        /// <summary>
        /// Gets a value indicating whether the alpha channel uses compression.
        /// </summary>
        private bool Compressed { get; }

        /// <summary>
        /// Gets the (maybe compressed) alpha data.
        /// </summary>
        private IMemoryOwner<byte> Data { get; }

        /// <summary>
        /// Gets the Vp8L decoder which is used to de compress the alpha channel, if needed.
        /// </summary>
        private WebPLosslessDecoder LosslessDecoder { get; }

        /// <summary>
        /// Gets a value indicating whether the decoding needs 1 byte per pixel for decoding.
        /// Although Alpha Channel requires only 1 byte per pixel, sometimes Vp8LDecoder may need to allocate
        /// 4 bytes per pixel internally during decode.
        /// </summary>
        public bool Use8BDecode { get; }

        /// <summary>
        /// Decodes and filters the maybe compressed alpha data.
        /// </summary>
        public void Decode()
        {
            if (this.Compressed is false)
            {
                Span<byte> dataSpan = this.Data.Memory.Span;
                var pixelCount = this.Width * this.Height;
                if (dataSpan.Length < pixelCount)
                {
                    WebPThrowHelper.ThrowImageFormatException("not enough data in the ALPH chunk");
                }

                Span<byte> alphaSpan = this.Alpha.Memory.Span;
                if (this.AlphaFilterType is WebPAlphaFilterType.None)
                {
                    dataSpan.Slice(0, pixelCount).CopyTo(alphaSpan);
                    return;
                }

                Span<byte> deltas = dataSpan;
                Span<byte> dst = alphaSpan;
                Span<byte> prev = null;
                for (int y = 0; y < this.Height; ++y)
                {
                    switch (this.AlphaFilterType)
                    {
                        case WebPAlphaFilterType.Horizontal:
                            HorizontalUnfilter(prev, deltas, dst, this.Width);
                            break;
                        case WebPAlphaFilterType.Vertical:
                            VerticalUnfilter(prev, deltas, dst, this.Width);
                            break;
                        case WebPAlphaFilterType.Gradient:
                            GradientUnfilter(prev, deltas, dst, this.Width);
                            break;
                    }

                    prev = dst;
                    deltas = deltas.Slice(this.Width);
                    dst = dst.Slice(this.Width);
                }
            }
            else
            {
                if (this.Use8BDecode)
                {
                    this.LosslessDecoder.DecodeAlphaData(this);
                }
                else
                {
                    this.LosslessDecoder.DecodeImageData(this.Vp8LDec, this.Vp8LDec.Pixels.Memory.Span);
                    this.ExtractAlphaRows(this.Vp8LDec);
                }
            }
        }

        /// <summary>
        /// Applies filtering to a set of rows.
        /// </summary>
        /// <param name="firstRow">The first row index to start filtering.</param>
        /// <param name="lastRow">The last row index for filtering.</param>
        /// <param name="dst">The destination to store the filtered data.</param>
        /// <param name="stride">The stride to use.</param>
        public void AlphaApplyFilter(int firstRow, int lastRow, Span<byte> dst, int stride)
        {
            if (this.AlphaFilterType is WebPAlphaFilterType.None)
            {
                return;
            }

            Span<byte> alphaSpan = this.Alpha.Memory.Span;
            Span<byte> prev = this.PrevRow is 0 ? null : alphaSpan.Slice(this.Width * this.PrevRow);
            for (int y = firstRow; y < lastRow; ++y)
            {
                switch (this.AlphaFilterType)
                {
                    case WebPAlphaFilterType.Horizontal:
                        HorizontalUnfilter(prev, dst, dst, this.Width);
                        break;
                    case WebPAlphaFilterType.Vertical:
                        VerticalUnfilter(prev, dst, dst, this.Width);
                        break;
                    case WebPAlphaFilterType.Gradient:
                        GradientUnfilter(prev, dst, dst, this.Width);
                        break;
                }

                prev = dst;
                dst = dst.Slice(stride);
            }

            this.PrevRow = lastRow - 1;
        }

        /// <summary>
        /// Once the image-stream is decoded into ARGB color values, the transparency information will be extracted from the green channel of the ARGB quadruplet.
        /// </summary>
        /// <param name="dec">The VP8L decoder.</param>
        private void ExtractAlphaRows(Vp8LDecoder dec)
        {
            int numRowsToProcess = dec.Height;
            int width = dec.Width;
            Span<uint> pixels = dec.Pixels.Memory.Span;
            Span<uint> input = pixels;
            Span<byte> output = this.Alpha.Memory.Span;

            // Extract alpha (which is stored in the green plane).
            int pixelCount = width * numRowsToProcess;
            WebPLosslessDecoder.ApplyInverseTransforms(dec, input, this.memoryAllocator);
            this.AlphaApplyFilter(0, numRowsToProcess, output, width);
            ExtractGreen(input, output, pixelCount);
        }

        private static void HorizontalUnfilter(Span<byte> prev, Span<byte> input, Span<byte> dst, int width)
        {
            byte pred = (byte)(prev == null ? 0 : prev[0]);

            for (int i = 0; i < width; ++i)
            {
                byte val = (byte)(pred + input[i]);
                pred = val;
                dst[i] = val;
            }
        }

        private static void VerticalUnfilter(Span<byte> prev, Span<byte> input, Span<byte> dst, int width)
        {
            if (prev == null)
            {
                HorizontalUnfilter(null, input, dst, width);
            }
            else
            {
                for (int i = 0; i < width; ++i)
                {
                    dst[i] = (byte)(prev[i] + input[i]);
                }
            }
        }

        private static void GradientUnfilter(Span<byte> prev, Span<byte> input, Span<byte> dst, int width)
        {
            if (prev == null)
            {
                HorizontalUnfilter(null, input, dst, width);
            }
            else
            {
                byte prev0 = prev[0];
                byte topLeft = prev0;
                byte left = prev0;
                for (int i = 0; i < width; ++i)
                {
                    byte top = prev[i];
                    left = (byte)(input[i] + GradientPredictor(left, top, topLeft));
                    topLeft = top;
                    dst[i] = left;
                }
            }
        }

        /// <summary>
        /// Row-processing for the special case when alpha data contains only one
        /// transform (color indexing), and trivial non-green literals.
        /// </summary>
        /// <param name="hdr">The VP8L meta data.</param>
        /// <returns>True, if alpha channel has one byte per pixel, otherwise 4.</returns>
        private static bool Is8BOptimizable(Vp8LMetadata hdr)
        {
            if (hdr.ColorCacheSize > 0)
            {
                return false;
            }

            // When the Huffman tree contains only one symbol, we can skip the
            // call to ReadSymbol() for red/blue/alpha channels.
            for (int i = 0; i < hdr.NumHTreeGroups; ++i)
            {
                List<HuffmanCode[]> htrees = hdr.HTreeGroups[i].HTrees;
                if (htrees[HuffIndex.Red][0].BitsUsed > 0)
                {
                    return false;
                }

                if (htrees[HuffIndex.Blue][0].BitsUsed > 0)
                {
                    return false;
                }

                if (htrees[HuffIndex.Alpha][0].BitsUsed > 0)
                {
                    return false;
                }
            }

            return true;
        }

        [MethodImpl(InliningOptions.ShortMethod)]
        private static int GradientPredictor(byte a, byte b, byte c)
        {
            int g = a + b - c;
            return ((g & ~0xff) is 0) ? g : (g < 0) ? 0 : 255;  // clip to 8bit
        }

        [MethodImpl(InliningOptions.ShortMethod)]
        private static void ExtractGreen(Span<uint> argb, Span<byte> alpha, int size)
        {
            for (int i = 0; i < size; ++i)
            {
                alpha[i] = (byte)(argb[i] >> 8);
            }
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            this.Vp8LDec?.Dispose();
            this.Data.Dispose();
            this.Alpha?.Dispose();
        }
    }
}