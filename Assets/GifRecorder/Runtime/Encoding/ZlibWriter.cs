using System;
using System.IO;
using System.IO.Compression;

namespace GifRecorder.Encoding
{
    /// <summary>
    /// zlib stream (RFC 1950) around the built-in <see cref="DeflateStream"/>. Input is fed in chunks via
    /// <see cref="Step"/> so compression can be time-sliced. Not thread-safe.
    /// </summary>
    internal sealed class ZlibWriter
    {
        readonly MemoryStream output = new MemoryStream();
        DeflateStream deflate;
        byte[] input;
        int length, position;
        uint adlerA, adlerB;

        /// <summary>The finished zlib stream; valid once <see cref="Step"/> has returned true.</summary>
        public ArraySegment<byte> Data => new ArraySegment<byte>(output.GetBuffer(), 0, (int)output.Length);

        public void Begin(byte[] data, int dataLength, CompressionLevel level)
        {
            input = data;
            length = dataLength;
            position = 0;
            adlerA = 1;
            adlerB = 0;

            output.SetLength(0);
            output.WriteByte(0x78);
            output.WriteByte(level == CompressionLevel.Optimal ? (byte)0xDA : (byte)0x01);
            deflate = new DeflateStream(output, level, true);
        }

        /// <summary>Compresses up to <paramref name="maxBytes"/> more input. Returns true once the stream is complete.</summary>
        public bool Step(int maxBytes)
        {
            if (deflate == null) return true;

            int count = Math.Min(Math.Max(1, maxBytes), length - position);
            if (count > 0)
            {
                deflate.Write(input, position, count);
                UpdateAdler(position, count);
                position += count;
            }
            if (position < length) return false;

            deflate.Dispose(); // flushes the final block
            deflate = null;
            uint adler = (adlerB << 16) | adlerA;
            output.WriteByte((byte)(adler >> 24));
            output.WriteByte((byte)(adler >> 16));
            output.WriteByte((byte)(adler >> 8));
            output.WriteByte((byte)adler);
            return true;
        }

        void UpdateAdler(int offset, int count)
        {
            const uint Mod = 65521;
            uint a = adlerA, b = adlerB;
            int end = offset + count;
            while (offset < end)
            {
                int blockEnd = Math.Min(end, offset + 5552); // largest block that can't overflow before the modulo
                for (; offset < blockEnd; offset++)
                {
                    a += input[offset];
                    b += a;
                }
                a %= Mod;
                b %= Mod;
            }
            adlerA = a;
            adlerB = b;
        }
    }
}
