using System;

namespace GifRecorder.Encoding
{
    /// <summary>
    /// Median-cut palette quantizer over a 15-bit (5-5-5) colour histogram, with optional Floyd–Steinberg dithering.
    /// Every phase works on ranges so it can be time-sliced. Holds large scratch buffers and is not thread-safe.
    /// </summary>
    /// <remarks>
    /// Usage: <see cref="BeginHistogram"/>, <see cref="Accumulate"/> (any number of ranges), <see cref="BeginPalette"/>,
    /// <see cref="SplitStep"/> until it returns true, then <see cref="BeginMap"/> and <see cref="MapRows"/>.
    /// </remarks>
    internal sealed class ColorQuantizer
    {
        /// <summary>Marks a pixel that should be written as the transparent index instead of being quantized.</summary>
        public const uint TransparentFlag = 0xFF000000;

        const int BinCount = 1 << 15;

        // Perceptual channel weights (roughly luma-proportional), used both for splitting and for matching.
        const int WeightR = 3, WeightG = 4, WeightB = 2;

        readonly int[] counts = new int[BinCount];
        readonly long[] sumR = new long[BinCount];
        readonly long[] sumG = new long[BinCount];
        readonly long[] sumB = new long[BinCount];
        readonly short[] lookup = new short[BinCount];
        readonly int[] entries = new int[BinCount];
        readonly int[] sortKeys = new int[BinCount];
        readonly Box[] boxes = new Box[256];
        int[] errorRow = Array.Empty<int>();
        int[] errorNext = Array.Empty<int>();

        int entryCount, boxCount, maxColors;
        int mapWidth, transparentIndex;
        bool dither;

        public readonly byte[] Palette = new byte[256 * 3];
        public int ColorCount { get; private set; }

        /// <summary>Whether any pixel was added to the histogram since <see cref="BeginHistogram"/>.</summary>
        public bool HasSamples { get; private set; }

        struct Box
        {
            public int Start, Count;
            public long Pixels;
            public int Axis, Range;
        }

        static int BinOf(uint c) => ((int)(c >> 3) & 0x1F) | ((int)(c >> 6) & 0x3E0) | ((int)(c >> 9) & 0x7C00);
        static int BinOf(int r, int g, int b) => (r >> 3) | ((g >> 3) << 5) | ((b >> 3) << 10);
        static int Component(int bin, int axis) => (bin >> (5 * axis)) & 0x1F;

        public void BeginHistogram()
        {
            Array.Clear(counts, 0, BinCount);
            Array.Clear(sumR, 0, BinCount);
            Array.Clear(sumG, 0, BinCount);
            Array.Clear(sumB, 0, BinCount);
            HasSamples = false;
        }

        /// <summary>Adds every <paramref name="step"/>-th opaque pixel of [start, end) (aligned to absolute indices).</summary>
        public void Accumulate(uint[] pixels, int start, int end, int step)
        {
            step = Math.Max(1, step);
            int first = (start + step - 1) / step * step;
            for (int i = first; i < end; i += step)
            {
                uint c = pixels[i];
                if ((c & TransparentFlag) == TransparentFlag) continue;
                int bin = BinOf(c);
                counts[bin]++;
                sumR[bin] += c & 0xFF;
                sumG[bin] += (c >> 8) & 0xFF;
                sumB[bin] += (c >> 16) & 0xFF;
                HasSamples = true;
            }
        }

        public void BeginPalette(int maxColors)
        {
            this.maxColors = Math.Max(1, Math.Min(256, maxColors));
            entryCount = 0;
            for (int bin = 0; bin < BinCount; bin++)
                if (counts[bin] > 0) entries[entryCount++] = bin;
            for (int i = 0; i < BinCount; i++) lookup[i] = -1;

            boxCount = 0;
            ColorCount = 0;
            if (entryCount > 0)
            {
                boxes[0] = MakeBox(0, entryCount);
                boxCount = 1;
            }
        }

        /// <summary>Performs up to <paramref name="maxSplits"/> median cuts. Returns true when the palette is final.</summary>
        public bool SplitStep(int maxSplits)
        {
            if (entryCount == 0) return true;

            if (entryCount <= maxColors)
            {
                for (int i = 0; i < entryCount; i++) SetPaletteFromBins(i, i, 1);
                ColorCount = entryCount;
                return true;
            }

            for (int split = 0; split < maxSplits && boxCount < maxColors; split++)
            {
                int best = -1;
                long bestScore = 0;
                for (int i = 0; i < boxCount; i++)
                {
                    if (boxes[i].Count < 2) continue;
                    long score = boxes[i].Pixels * boxes[i].Range;
                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = i;
                    }
                }
                if (best < 0)
                {
                    maxColors = boxCount; // nothing left to split
                    break;
                }

                var box = boxes[best];
                for (int i = box.Start, end = box.Start + box.Count; i < end; i++)
                    sortKeys[i] = Component(entries[i], box.Axis);
                Array.Sort(sortKeys, entries, box.Start, box.Count);

                long half = box.Pixels / 2, acc = 0;
                int cut = box.Start + 1;
                for (int i = box.Start, end = box.Start + box.Count - 1; i < end; i++)
                {
                    acc += counts[entries[i]];
                    cut = i + 1;
                    if (acc >= half) break;
                }

                boxes[best] = MakeBox(box.Start, cut - box.Start);
                boxes[boxCount++] = MakeBox(cut, box.Start + box.Count - cut);
            }

            if (boxCount < maxColors) return false;

            for (int i = 0; i < boxCount; i++)
                SetPaletteFromBins(i, boxes[i].Start, boxes[i].Count);
            ColorCount = boxCount;
            return true;
        }

        Box MakeBox(int start, int count)
        {
            int minR = 31, minG = 31, minB = 31, maxR = 0, maxG = 0, maxB = 0;
            long pixels = 0;
            for (int i = start, end = start + count; i < end; i++)
            {
                int bin = entries[i];
                int r = bin & 0x1F, g = (bin >> 5) & 0x1F, b = (bin >> 10) & 0x1F;
                if (r < minR) minR = r; if (r > maxR) maxR = r;
                if (g < minG) minG = g; if (g > maxG) maxG = g;
                if (b < minB) minB = b; if (b > maxB) maxB = b;
                pixels += counts[bin];
            }

            int rr = (maxR - minR) * WeightR, rg = (maxG - minG) * WeightG, rb = (maxB - minB) * WeightB;
            int axis = 1, range = rg;
            if (rr > range) { axis = 0; range = rr; }
            if (rb > range) { axis = 2; range = rb; }
            return new Box { Start = start, Count = count, Pixels = pixels, Axis = axis, Range = range + 1 };
        }

        void SetPaletteFromBins(int paletteIndex, int start, int count)
        {
            long n = 0, r = 0, g = 0, b = 0;
            for (int i = start, end = start + count; i < end; i++)
            {
                int bin = entries[i];
                n += counts[bin];
                r += sumR[bin];
                g += sumG[bin];
                b += sumB[bin];
            }
            int o = paletteIndex * 3;
            Palette[o] = (byte)((r + n / 2) / n);
            Palette[o + 1] = (byte)((g + n / 2) / n);
            Palette[o + 2] = (byte)((b + n / 2) / n);
        }

        int Lookup(int bin)
        {
            int index = lookup[bin];
            if (index >= 0) return index;

            int r, g, b;
            int n = counts[bin];
            if (n > 0)
            {
                r = (int)(sumR[bin] / n);
                g = (int)(sumG[bin] / n);
                b = (int)(sumB[bin] / n);
            }
            else
            {
                r = ((bin & 0x1F) << 3) | 4;
                g = (((bin >> 5) & 0x1F) << 3) | 4;
                b = (((bin >> 10) & 0x1F) << 3) | 4;
            }

            int best = 0, bestDistance = int.MaxValue;
            for (int i = 0, o = 0; i < ColorCount; i++, o += 3)
            {
                int dr = Palette[o] - r, dg = Palette[o + 1] - g, db = Palette[o + 2] - b;
                int d = dr * dr * WeightR + dg * dg * WeightG + db * db * WeightB;
                if (d < bestDistance)
                {
                    bestDistance = d;
                    best = i;
                    if (d == 0) break;
                }
            }
            lookup[bin] = (short)best;
            return best;
        }

        /// <summary>Prepares mapping. Pixels flagged transparent will receive <paramref name="transparent"/>.</summary>
        public void BeginMap(int width, bool useDither, int transparent)
        {
            mapWidth = width;
            transparentIndex = Math.Max(0, transparent);
            dither = useDither && ColorCount > 1;
            if (!dither) return;

            int rowLength = (width + 2) * 3;
            if (errorRow.Length < rowLength)
            {
                errorRow = new int[rowLength];
                errorNext = new int[rowLength];
            }
            Array.Clear(errorRow, 0, rowLength);
        }

        /// <summary>Maps rows [y0, y1) to palette indices. Rows must be processed in order.</summary>
        public void MapRows(uint[] pixels, byte[] indices, int y0, int y1)
        {
            int width = mapWidth;
            byte transparent = (byte)transparentIndex;

            if (!dither)
            {
                for (int i = y0 * width, end = y1 * width; i < end; i++)
                {
                    uint c = pixels[i];
                    indices[i] = (c & TransparentFlag) == TransparentFlag ? transparent : (byte)Lookup(BinOf(c));
                }
                return;
            }

            // Floyd–Steinberg. Errors are kept in 1/16 units; rows have one pixel of padding on each side.
            int rowLength = (width + 2) * 3;
            for (int y = y0; y < y1; y++)
            {
                Array.Clear(errorNext, 0, rowLength);
                int rowStart = y * width;
                for (int x = 0; x < width; x++)
                {
                    int i = rowStart + x;
                    uint c = pixels[i];
                    if ((c & TransparentFlag) == TransparentFlag)
                    {
                        indices[i] = transparent;
                        continue;
                    }

                    int e = (x + 1) * 3;
                    int r = Clamp((int)(c & 0xFF) + (errorRow[e] >> 4));
                    int g = Clamp((int)((c >> 8) & 0xFF) + (errorRow[e + 1] >> 4));
                    int b = Clamp((int)((c >> 16) & 0xFF) + (errorRow[e + 2] >> 4));

                    int index = Lookup(BinOf(r, g, b));
                    indices[i] = (byte)index;

                    int p = index * 3;
                    Spread(e, r - Palette[p], 0);
                    Spread(e, g - Palette[p + 1], 1);
                    Spread(e, b - Palette[p + 2], 2);
                }
                var swap = errorRow;
                errorRow = errorNext;
                errorNext = swap;
            }
        }

        void Spread(int e, int error, int channel)
        {
            if (error == 0) return;
            errorRow[e + 3 + channel] += error * 7;
            errorNext[e - 3 + channel] += error * 3;
            errorNext[e + channel] += error * 5;
            errorNext[e + 3 + channel] += error;
        }

        static int Clamp(int v) => v < 0 ? 0 : v > 255 ? 255 : v;
    }
}
