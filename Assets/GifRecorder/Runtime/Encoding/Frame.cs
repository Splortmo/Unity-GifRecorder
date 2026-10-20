using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace GifRecorder.Encoding
{
    /// <summary>
    /// A single RGBA32 frame. Rows are stored top to bottom. The alpha channel is ignored by the encoders.
    /// </summary>
    public sealed class Frame
    {
        public int Width { get; }
        public int Height { get; }
        public byte[] Pixels { get; }

        readonly FramePool pool;
        int released;

        public Frame(int width, int height, byte[] pixels) : this(width, height, pixels, null) { }

        internal Frame(int width, int height, byte[] pixels, FramePool pool)
        {
            if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width), "Frame size must be positive.");
            if (pixels == null) throw new ArgumentNullException(nameof(pixels));
            if (pixels.Length < width * height * 4) throw new ArgumentException("Pixel buffer is smaller than width * height * 4.", nameof(pixels));
            Width = width;
            Height = height;
            Pixels = pixels;
            this.pool = pool;
        }

        /// <summary>Pixels reinterpreted as packed 0xAABBGGRR values.</summary>
        internal ReadOnlySpan<uint> Packed => MemoryMarshal.Cast<byte, uint>(Pixels.AsSpan(0, Width * Height * 4));

        /// <summary>Returns the pixel buffer to its pool (if any). Safe to call more than once.</summary>
        public void Release()
        {
            if (Interlocked.Exchange(ref released, 1) == 0)
                pool?.Return(Pixels);
        }
    }

    /// <summary>
    /// Thread-safe pool of equally sized frame buffers, so recording doesn't churn the GC. Large buffers are
    /// expensive to allocate in Mono, so the pool keeps a few spares and refills them on a worker thread.
    /// </summary>
    public sealed class FramePool
    {
        readonly ConcurrentBag<byte[]> buffers = new ConcurrentBag<byte[]>();
        readonly int spareTarget;
        int refilling;

        public int Width { get; }
        public int Height { get; }
        public int BufferLength => Width * Height * 4;

        /// <param name="spareTarget">Number of free buffers to keep ready in the background (0 disables refilling).</param>
        public FramePool(int width, int height, int spareTarget = 0)
        {
            Width = width;
            Height = height;
            this.spareTarget = Math.Max(0, spareTarget);
            Refill();
        }

        public Frame Rent()
        {
            if (!buffers.TryTake(out var buffer))
                buffer = new byte[BufferLength];
            if (buffers.Count < spareTarget / 2 + 1) Refill();
            return new Frame(Width, Height, buffer, this);
        }

        internal void Return(byte[] buffer) => buffers.Add(buffer);

        void Refill()
        {
            if (spareTarget == 0 || Interlocked.Exchange(ref refilling, 1) == 1) return;
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    while (buffers.Count < spareTarget) buffers.Add(new byte[BufferLength]);
                }
                finally
                {
                    Volatile.Write(ref refilling, 0);
                }
            });
        }
    }

    /// <summary>Pool for encoded frame data handed from encoders to the writer.</summary>
    public static class EncodedBuffers
    {
        static readonly ArrayPool<byte> Pool = ArrayPool<byte>.Create(64 * 1024 * 1024, 64);

        /// <summary>Copies a stream's contents into a pooled buffer.</summary>
        public static ArraySegment<byte> From(MemoryStream stream)
        {
            int length = (int)stream.Length;
            var buffer = Pool.Rent(length);
            Buffer.BlockCopy(stream.GetBuffer(), 0, buffer, 0, length);
            return new ArraySegment<byte>(buffer, 0, length);
        }

        public static void Return(ArraySegment<byte> segment)
        {
            if (segment.Array != null) Pool.Return(segment.Array);
        }
    }

    /// <summary>Per-frame metadata handed to an encoder.</summary>
    public readonly struct FrameInfo
    {
        public readonly int Index;
        public readonly int DelayMs;

        public FrameInfo(int index, int delayMs)
        {
            Index = index;
            DelayMs = delayMs;
        }
    }

    internal interface IPixelComparer
    {
        bool Equal(uint a, uint b);
    }

    /// <summary>Compares colour only; alpha is ignored (opaque recordings).</summary>
    internal struct RgbComparer : IPixelComparer
    {
        public bool Equal(uint a, uint b) => ((a ^ b) & 0x00FFFFFF) == 0;
    }

    /// <summary>1-bit alpha: pixels are visible when alpha ≥ threshold; invisible pixels are all equal.</summary>
    internal struct ThresholdComparer : IPixelComparer
    {
        public uint Threshold;

        public bool Equal(uint a, uint b)
        {
            bool va = (a >> 24) >= Threshold, vb = (b >> 24) >= Threshold;
            if (va != vb) return false;
            return !va || ((a ^ b) & 0x00FFFFFF) == 0;
        }
    }

    /// <summary>Exact RGBA, except that fully transparent pixels are all equal.</summary>
    internal struct RgbaComparer : IPixelComparer
    {
        public bool Equal(uint a, uint b) => a == b || ((a | b) >> 24) == 0;
    }

    internal static class FrameDiff
    {
        /// <summary>
        /// Finds the bounding rectangle of pixels that differ between two frames.
        /// Returns false when the frames are identical.
        /// </summary>
        public static bool TryGetChangedBounds<T>(Frame current, Frame previous, T comparer, out int x, out int y, out int w, out int h)
            where T : struct, IPixelComparer
        {
            int width = current.Width, height = current.Height;
            var a = current.Packed;
            var b = previous.Packed;

            int top = -1;
            for (int row = 0; row < height && top < 0; row++)
                if (!RowEqual(a, b, row * width, width, comparer)) top = row;

            if (top < 0)
            {
                x = y = w = h = 0;
                return false;
            }

            int bottom = top;
            for (int row = height - 1; row > top; row--)
            {
                if (!RowEqual(a, b, row * width, width, comparer))
                {
                    bottom = row;
                    break;
                }
            }

            int left = width, right = -1;
            for (int row = top; row <= bottom; row++)
            {
                int o = row * width;
                for (int i = 0; i < left; i++)
                {
                    if (!comparer.Equal(a[o + i], b[o + i]))
                    {
                        left = i;
                        break;
                    }
                }
                for (int i = width - 1; i > right; i--)
                {
                    if (!comparer.Equal(a[o + i], b[o + i]))
                    {
                        right = i;
                        break;
                    }
                }
            }

            x = left;
            y = top;
            w = right - left + 1;
            h = bottom - top + 1;
            return true;
        }

        /// <summary>True if some pixel is visible in <paramref name="from"/> but invisible in <paramref name="to"/>.</summary>
        public static bool HasVisibleToInvisible(Frame from, Frame to, uint threshold)
        {
            var a = from.Packed;
            var b = to.Packed;
            for (int i = 0; i < a.Length; i++)
                if ((a[i] >> 24) >= threshold && (b[i] >> 24) < threshold) return true;
            return false;
        }

        static bool RowEqual<T>(ReadOnlySpan<uint> a, ReadOnlySpan<uint> b, int offset, int count, T comparer)
            where T : struct, IPixelComparer
        {
            for (int i = offset, end = offset + count; i < end; i++)
                if (!comparer.Equal(a[i], b[i])) return false;
            return true;
        }
    }
}
