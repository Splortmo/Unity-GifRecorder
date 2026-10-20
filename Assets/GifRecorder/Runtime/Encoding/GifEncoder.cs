using System;
using System.Collections;
using System.IO;

namespace GifRecorder.Encoding
{
    /// <summary>
    /// Animated GIF (GIF89a) encoder. Every frame gets its own median-cut palette, is cropped to the region that
    /// changed since the previous frame, and marks unchanged pixels inside that region as transparent.
    /// Optionally keeps 1-bit transparency from the source alpha channel.
    /// </summary>
    public sealed class GifEncoder : IAnimationEncoder
    {
        /// <summary>Palette size per frame (2–256). Fewer colours give smaller files.</summary>
        public int MaxColors { get; set; } = 256;

        /// <summary>Floyd–Steinberg dithering. Smoother gradients, but noisier frames compress worse.</summary>
        public bool Dithering { get; set; } = true;

        /// <summary>Build palettes from every n-th pixel (1 = best quality).</summary>
        public int PaletteSampleStep { get; set; } = 1;

        /// <summary>Number of times to play the animation; 0 loops forever.</summary>
        public int LoopCount { get; set; }

        /// <summary>Keep transparency: pixels with alpha below <see cref="AlphaThreshold"/> become transparent.</summary>
        public bool Transparency { get; set; }

        /// <summary>Alpha (1–255) at or above which a pixel counts as opaque when <see cref="Transparency"/> is on.</summary>
        public int AlphaThreshold { get; set; } = 128;

        public string FileExtension => "gif";
        public int TimeQuantumMs => 10;
        public int MinDelayMs => 20; // browsers slow down anything faster than 2 cs

        const int PixelsPerStep = 16 * 1024;

        [ThreadStatic] static Workspace workspace;

        sealed class Workspace
        {
            public readonly ColorQuantizer Quantizer = new ColorQuantizer();
            public readonly LzwEncoder Lzw = new LzwEncoder();
            public readonly MemoryStream Stream = new MemoryStream();
            public uint[] Pixels = Array.Empty<uint>();
            public byte[] Indices = Array.Empty<byte>();

            public void Ensure(int count)
            {
                if (Pixels.Length < count)
                {
                    Pixels = new uint[count];
                    Indices = new byte[count];
                }
            }
        }

        public void WriteHeader(Stream output, int width, int height)
        {
            WriteAscii(output, "GIF89a");
            WriteUInt16(output, width);
            WriteUInt16(output, height);
            output.WriteByte(0x70); // no global colour table, 8-bit colour resolution
            output.WriteByte(0);    // background colour index
            output.WriteByte(0);    // pixel aspect ratio

            if (LoopCount != 1)
            {
                // NETSCAPE2.0 application extension: repeat count (0 = forever).
                output.WriteByte(0x21);
                output.WriteByte(0xFF);
                output.WriteByte(11);
                WriteAscii(output, "NETSCAPE2.0");
                output.WriteByte(3);
                output.WriteByte(1);
                WriteUInt16(output, LoopCount <= 0 ? 0 : Math.Min(LoopCount - 1, ushort.MaxValue));
                output.WriteByte(0);
            }
        }

        public EncodeJob CreateJob(Frame frame, Frame previous, Frame next, FrameInfo info) =>
            EncodeJob.Create(job => Encode(job, frame, previous, next, info));

        IEnumerator Encode(EncodeJob job, Frame frame, Frame previous, Frame next, FrameInfo info)
        {
            var ws = workspace ?? (workspace = new Workspace());
            bool transparency = Transparency;
            uint threshold = (uint)Math.Max(1, Math.Min(255, AlphaThreshold));

            // GIF frames draw on top of each other, so a pixel that turns transparent can only be shown by
            // clearing the canvas: the frame before it is then disposed to background and this one is drawn in full.
            bool cleared = previous == null || (transparency && FrameDiff.HasVisibleToInvisible(previous, frame, threshold));
            bool disposeAfter = transparency && next != null && FrameDiff.HasVisibleToInvisible(frame, next, threshold);
            yield return null;

            int rx = 0, ry = 0, rw = frame.Width, rh = frame.Height;
            if (!cleared && !disposeAfter)
            {
                bool changed = transparency
                    ? FrameDiff.TryGetChangedBounds(frame, previous, new ThresholdComparer { Threshold = threshold }, out rx, out ry, out rw, out rh)
                    : FrameDiff.TryGetChangedBounds(frame, previous, new RgbComparer(), out rx, out ry, out rw, out rh);
                if (!changed)
                {
                    // Nothing changed: emit a single transparent pixel to carry the delay.
                    rx = ry = 0;
                    rw = rh = 1;
                }
                yield return null;
            }

            int count = rw * rh;
            int rowsPerStep = Math.Max(1, PixelsPerStep / rw);
            ws.Ensure(count);

            bool hasTransparent = false;
            for (int y = 0; y < rh; y += rowsPerStep)
            {
                hasTransparent |= CopyRows(frame, cleared ? null : previous, rx, ry, rw, y, Math.Min(rh, y + rowsPerStep),
                    transparency, threshold, ws.Pixels);
                yield return null;
            }

            var quantizer = ws.Quantizer;
            quantizer.BeginHistogram();
            int sampleStep = Math.Max(1, PaletteSampleStep);
            for (int i = 0; i < count; i += PixelsPerStep)
            {
                quantizer.Accumulate(ws.Pixels, i, Math.Min(count, i + PixelsPerStep), sampleStep);
                yield return null;
            }
            if (!quantizer.HasSamples && sampleStep > 1)
                quantizer.Accumulate(ws.Pixels, 0, count, 1);

            int maxColors = Math.Max(2, Math.Min(256, MaxColors));
            if (hasTransparent) maxColors = Math.Min(maxColors, 255);
            quantizer.BeginPalette(maxColors);
            yield return null;
            while (!quantizer.SplitStep(4)) yield return null;

            int colorCount = quantizer.ColorCount;
            int transparentIndex = hasTransparent ? colorCount : -1;
            // Mapping is the most expensive phase early on (every new colour searches the palette), so use smaller steps.
            quantizer.BeginMap(rw, Dithering, transparentIndex);
            int mapRowsPerStep = Math.Max(1, rowsPerStep / 4);
            for (int y = 0; y < rh; y += mapRowsPerStep)
            {
                quantizer.MapRows(ws.Pixels, ws.Indices, y, Math.Min(rh, y + mapRowsPerStep));
                yield return null;
            }

            int totalColors = colorCount + (hasTransparent ? 1 : 0);
            int bits = 1;
            while ((1 << bits) < totalColors) bits++;

            var ms = ws.Stream;
            ms.SetLength(0);

            // Graphic control extension. Disposal 1 = keep (next frame draws on top), 2 = restore to background.
            ms.WriteByte(0x21);
            ms.WriteByte(0xF9);
            ms.WriteByte(4);
            ms.WriteByte((byte)(((disposeAfter ? 2 : 1) << 2) | (hasTransparent ? 1 : 0)));
            WriteUInt16(ms, Math.Min(info.DelayMs / 10, ushort.MaxValue));
            ms.WriteByte((byte)(hasTransparent ? transparentIndex : 0));
            ms.WriteByte(0);

            // Image descriptor with a local colour table.
            ms.WriteByte(0x2C);
            WriteUInt16(ms, rx);
            WriteUInt16(ms, ry);
            WriteUInt16(ms, rw);
            WriteUInt16(ms, rh);
            ms.WriteByte((byte)(0x80 | (bits - 1)));

            ms.Write(quantizer.Palette, 0, colorCount * 3);
            for (int i = colorCount * 3, end = (1 << bits) * 3; i < end; i++) ms.WriteByte(0);

            int minCodeSize = Math.Max(2, bits);
            ms.WriteByte((byte)minCodeSize);
            var lzw = ws.Lzw;
            lzw.Begin(minCodeSize, ms);
            for (int i = 0; i < count; i += PixelsPerStep)
            {
                lzw.Write(ws.Indices, i, Math.Min(count, i + PixelsPerStep));
                yield return null;
            }
            lzw.Finish();
            ms.WriteByte(0); // block terminator

            job.Result = EncodedBuffers.From(ms);
        }

        /// <summary>
        /// Copies rows [y0, y1) of the rectangle into packed RGB, flagging pixels that should be transparent:
        /// invisible pixels (with transparency on) and pixels that already show the right colour on the canvas.
        /// <paramref name="canvas"/> is the frame currently on screen, or null if the canvas has been cleared.
        /// </summary>
        static bool CopyRows(Frame frame, Frame canvas, int rx, int ry, int rw, int y0, int y1,
            bool transparency, uint threshold, uint[] pixels)
        {
            var current = frame.Packed;
            var before = canvas != null ? canvas.Packed : default;
            bool any = false;

            for (int y = y0; y < y1; y++)
            {
                int src = (ry + y) * frame.Width + rx;
                int dst = y * rw;
                for (int x = 0; x < rw; x++)
                {
                    uint p = current[src + x];
                    uint c = p & 0x00FFFFFF;
                    bool transparent;
                    if (transparency && (p >> 24) < threshold)
                    {
                        transparent = true;
                    }
                    else if (canvas != null)
                    {
                        uint q = before[src + x];
                        transparent = (q & 0x00FFFFFF) == c && (!transparency || (q >> 24) >= threshold);
                    }
                    else
                    {
                        transparent = false;
                    }

                    if (transparent)
                    {
                        pixels[dst + x] = ColorQuantizer.TransparentFlag;
                        any = true;
                    }
                    else
                    {
                        pixels[dst + x] = c;
                    }
                }
            }
            return any;
        }

        public void WriteTrailer(Stream output, int frameCount)
        {
            output.WriteByte(0x3B);
        }

        static void WriteUInt16(Stream s, int value)
        {
            s.WriteByte((byte)value);
            s.WriteByte((byte)(value >> 8));
        }

        static void WriteAscii(Stream s, string text)
        {
            foreach (char c in text) s.WriteByte((byte)c);
        }
    }
}
