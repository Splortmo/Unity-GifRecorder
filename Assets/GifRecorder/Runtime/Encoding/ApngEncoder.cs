using System;
using System.Collections;
using System.IO;
using System.IO.Compression;

namespace GifRecorder.Encoding
{
    public enum PngCompression
    {
        /// <summary>Quick to encode, larger files.</summary>
        Fastest = 0,
        /// <summary>Slower to encode, smaller files.</summary>
        Optimal = 1,
    }

    /// <summary>
    /// Lossless animated PNG (APNG) encoder, 24-bit RGB or 32-bit RGBA. Each frame is cropped to the region that
    /// changed since the previous frame, filtered per row with the PNG adaptive heuristic and deflate-compressed
    /// with System.IO.Compression.
    /// </summary>
    public sealed class ApngEncoder : IAnimationEncoder
    {
        public PngCompression Compression { get; set; } = PngCompression.Optimal;

        /// <summary>Number of times to play the animation; 0 loops forever.</summary>
        public int LoopCount { get; set; }

        /// <summary>Write an alpha channel (RGBA). Fully transparent pixels are normalised to (0,0,0,0).</summary>
        public bool Transparency { get; set; }

        public string FileExtension => "png";
        public int TimeQuantumMs => 1;
        public int MinDelayMs => 11; // browsers slow down delays of 10 ms or less

        const int PixelsPerStep = 8 * 1024;
        const int DeflateBytesPerStep = 4 * 1024;

        static readonly byte[] Signature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        static readonly byte[] IHDR = Type("IHDR"), acTL = Type("acTL"), fcTL = Type("fcTL"),
            IDAT = Type("IDAT"), fdAT = Type("fdAT"), IEND = Type("IEND");

        long animationControlPosition = -1;

        [ThreadStatic] static Workspace workspace;

        sealed class Workspace
        {
            public readonly ZlibWriter Zlib = new ZlibWriter();
            public readonly MemoryStream Output = new MemoryStream();
            public readonly byte[] Small = new byte[32];
            public byte[] Raw = Array.Empty<byte>();
            public byte[] Row = Array.Empty<byte>();
            public byte[] PreviousRow = Array.Empty<byte>();

            public void Ensure(int rawLength, int stride)
            {
                if (Raw.Length < rawLength) Raw = new byte[rawLength];
                if (Row.Length < stride)
                {
                    Row = new byte[stride];
                    PreviousRow = new byte[stride];
                }
            }
        }

        public void WriteHeader(Stream output, int width, int height)
        {
            output.Write(Signature, 0, Signature.Length);

            var header = new byte[13];
            WriteUInt32(header, 0, (uint)width);
            WriteUInt32(header, 4, (uint)height);
            header[8] = 8;                       // bit depth
            header[9] = Transparency ? (byte)6 : (byte)2; // colour type: RGBA / RGB
            header[10] = 0;                      // deflate
            header[11] = 0;                      // adaptive filtering
            header[12] = 0;                      // no interlace
            PngChunk.Write(output, IHDR, header);

            // Frame count isn't known while streaming; WriteTrailer patches this chunk.
            animationControlPosition = output.Position;
            PngChunk.Write(output, acTL, AnimationControl(0));
        }

        byte[] AnimationControl(int frameCount)
        {
            var data = new byte[8];
            WriteUInt32(data, 0, (uint)frameCount);
            WriteUInt32(data, 4, (uint)Math.Max(0, LoopCount));
            return data;
        }

        public EncodeJob CreateJob(Frame frame, Frame previous, Frame next, FrameInfo info) =>
            EncodeJob.Create(job => Encode(job, frame, previous, info));

        IEnumerator Encode(EncodeJob job, Frame frame, Frame previous, FrameInfo info)
        {
            var ws = workspace ?? (workspace = new Workspace());
            bool alpha = Transparency;
            int bpp = alpha ? 4 : 3;

            int rx = 0, ry = 0, rw = frame.Width, rh = frame.Height;
            if (previous != null)
            {
                // Frames are composited with blend_op SOURCE, so a sub-rectangle replaces pixels (alpha included) exactly.
                bool changed = alpha
                    ? FrameDiff.TryGetChangedBounds(frame, previous, new RgbaComparer(), out rx, out ry, out rw, out rh)
                    : FrameDiff.TryGetChangedBounds(frame, previous, new RgbComparer(), out rx, out ry, out rw, out rh);
                if (!changed)
                {
                    // Nothing changed: re-emit a single (identical) pixel to carry the delay.
                    rx = ry = 0;
                    rw = rh = 1;
                }
                yield return null;
            }

            int stride = rw * bpp;
            int rawLength = (stride + 1) * rh;
            ws.Ensure(rawLength, stride);
            Array.Clear(ws.PreviousRow, 0, stride);

            int rowsPerStep = Math.Max(1, PixelsPerStep / rw);
            for (int y = 0; y < rh; y += rowsPerStep)
            {
                FilterRows(frame, rx, ry, rw, y, Math.Min(rh, y + rowsPerStep), bpp, ws);
                yield return null;
            }

            var zlib = ws.Zlib;
            zlib.Begin(ws.Raw, rawLength, Compression == PngCompression.Fastest ? CompressionLevel.Fastest : CompressionLevel.Optimal);
            while (!zlib.Step(DeflateBytesPerStep)) yield return null;
            job.Result = WriteFrameChunks(ws, info, rx, ry, rw, rh);
        }

        static ArraySegment<byte> WriteFrameChunks(Workspace ws, FrameInfo info, int rx, int ry, int rw, int rh)
        {
            var segment = ws.Zlib.Data;
            var data = new ReadOnlySpan<byte>(segment.Array, segment.Offset, segment.Count);

            // Sequence numbers are shared by fcTL and fdAT: frame 0 -> fcTL 0 (+IDAT); frame n -> fcTL 2n-1, fdAT 2n.
            int sequence = info.Index == 0 ? 0 : info.Index * 2 - 1;

            var small = ws.Small;
            WriteUInt32(small, 0, (uint)sequence);
            WriteUInt32(small, 4, (uint)rw);
            WriteUInt32(small, 8, (uint)rh);
            WriteUInt32(small, 12, (uint)rx);
            WriteUInt32(small, 16, (uint)ry);
            int delay = Math.Max(0, Math.Min(info.DelayMs, ushort.MaxValue));
            small[20] = (byte)(delay >> 8);
            small[21] = (byte)delay;
            small[22] = 1000 >> 8;
            small[23] = 1000 & 0xFF;
            small[24] = 0; // dispose: none
            small[25] = 0; // blend: source

            var output = ws.Output;
            output.SetLength(0);
            PngChunk.Write(output, fcTL, new ReadOnlySpan<byte>(small, 0, 26));

            if (info.Index == 0)
            {
                PngChunk.Write(output, IDAT, data);
            }
            else
            {
                WriteUInt32(small, 0, (uint)(sequence + 1));
                PngChunk.Write(output, fdAT, new ReadOnlySpan<byte>(small, 0, 4), data);
            }

            return EncodedBuffers.From(output);
        }

        public void WriteTrailer(Stream output, int frameCount)
        {
            PngChunk.Write(output, IEND, ReadOnlySpan<byte>.Empty);

            long end = output.Position;
            output.Position = animationControlPosition;
            PngChunk.Write(output, acTL, AnimationControl(frameCount));
            output.Position = end;
        }

        /// <summary>Filters rows [y0, y1) of the rectangle into the raw buffer. Rows must be processed in order.</summary>
        static void FilterRows(Frame frame, int rx, int ry, int rw, int y0, int y1, int bpp, Workspace ws)
        {
            int stride = rw * bpp;
            byte[] raw = ws.Raw;
            byte[] row = ws.Row;
            byte[] up = ws.PreviousRow;
            byte[] pixels = frame.Pixels;

            for (int y = y0; y < y1; y++)
            {
                int src = ((ry + y) * frame.Width + rx) * 4;
                if (bpp == 4)
                {
                    for (int x = 0, d = 0; x < rw; x++, src += 4, d += 4)
                    {
                        byte a = pixels[src + 3];
                        if (a == 0)
                        {
                            row[d] = row[d + 1] = row[d + 2] = row[d + 3] = 0;
                            continue;
                        }
                        row[d] = pixels[src];
                        row[d + 1] = pixels[src + 1];
                        row[d + 2] = pixels[src + 2];
                        row[d + 3] = a;
                    }
                }
                else
                {
                    for (int x = 0, d = 0; x < rw; x++, src += 4, d += 3)
                    {
                        row[d] = pixels[src];
                        row[d + 1] = pixels[src + 1];
                        row[d + 2] = pixels[src + 2];
                    }
                }

                // Pick the filter with the smallest sum of absolute (signed) residuals.
                long s0 = 0, s1 = 0, s2 = 0, s3 = 0, s4 = 0;
                for (int i = 0; i < stride; i++)
                {
                    int x = row[i], a = i >= bpp ? row[i - bpp] : 0, b = up[i], c = i >= bpp ? up[i - bpp] : 0;
                    s0 += Cost(x);
                    s1 += Cost(x - a);
                    s2 += Cost(x - b);
                    s3 += Cost(x - ((a + b) >> 1));
                    s4 += Cost(x - Paeth(a, b, c));
                }
                int filter = 0;
                long best = s0;
                if (s1 < best) { best = s1; filter = 1; }
                if (s2 < best) { best = s2; filter = 2; }
                if (s3 < best) { best = s3; filter = 3; }
                if (s4 < best) { filter = 4; }

                int o = y * (stride + 1);
                raw[o++] = (byte)filter;
                for (int i = 0; i < stride; i++)
                {
                    int x = row[i], a = i >= bpp ? row[i - bpp] : 0, b = up[i], c = i >= bpp ? up[i - bpp] : 0;
                    int predicted;
                    switch (filter)
                    {
                        case 1: predicted = a; break;
                        case 2: predicted = b; break;
                        case 3: predicted = (a + b) >> 1; break;
                        case 4: predicted = Paeth(a, b, c); break;
                        default: predicted = 0; break;
                    }
                    raw[o + i] = (byte)(x - predicted);
                }

                var swap = up;
                up = row;
                row = swap;
            }

            ws.Row = row;
            ws.PreviousRow = up;
        }

        static int Cost(int residual)
        {
            int v = (sbyte)(byte)residual;
            return v < 0 ? -v : v;
        }

        static int Paeth(int a, int b, int c)
        {
            int p = a + b - c;
            int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
            if (pa <= pb && pa <= pc) return a;
            return pb <= pc ? b : c;
        }

        static void WriteUInt32(byte[] buffer, int offset, uint value)
        {
            buffer[offset] = (byte)(value >> 24);
            buffer[offset + 1] = (byte)(value >> 16);
            buffer[offset + 2] = (byte)(value >> 8);
            buffer[offset + 3] = (byte)value;
        }

        static byte[] Type(string name) => new[] { (byte)name[0], (byte)name[1], (byte)name[2], (byte)name[3] };
    }

    internal static class PngChunk
    {
        static readonly uint[] CrcTable = BuildCrcTable();

        public static void Write(Stream s, byte[] type, ReadOnlySpan<byte> data) => Write(s, type, ReadOnlySpan<byte>.Empty, data);

        public static void Write(Stream s, byte[] type, ReadOnlySpan<byte> prefix, ReadOnlySpan<byte> data)
        {
            uint length = (uint)(prefix.Length + data.Length);
            Span<byte> word = stackalloc byte[4];
            BigEndian(word, length);
            s.Write(word);
            s.Write(type, 0, 4);
            s.Write(prefix);
            s.Write(data);

            uint crc = Crc(Crc(Crc(0xFFFFFFFF, type), prefix), data) ^ 0xFFFFFFFF;
            BigEndian(word, crc);
            s.Write(word);
        }

        public static uint Crc(uint crc, ReadOnlySpan<byte> data)
        {
            var table = CrcTable;
            for (int i = 0; i < data.Length; i++)
                crc = table[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
            return crc;
        }

        static void BigEndian(Span<byte> buffer, uint value)
        {
            buffer[0] = (byte)(value >> 24);
            buffer[1] = (byte)(value >> 16);
            buffer[2] = (byte)(value >> 8);
            buffer[3] = (byte)value;
        }

        static uint[] BuildCrcTable()
        {
            var table = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++)
                    c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
                table[n] = c;
            }
            return table;
        }
    }
}
