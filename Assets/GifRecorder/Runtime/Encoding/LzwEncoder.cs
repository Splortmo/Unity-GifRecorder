using System;
using System.IO;

namespace GifRecorder.Encoding
{
    /// <summary>
    /// Variable-length-code LZW compressor as used by GIF, writing LSB-first codes packed into 255-byte sub-blocks.
    /// Input can be fed in pieces (<see cref="Begin"/>, <see cref="Write"/>…, <see cref="Finish"/>) so that it can be
    /// time-sliced. Not thread-safe; use one instance per thread.
    /// </summary>
    internal sealed class LzwEncoder
    {
        const int MaxCode = 4095;
        const int TableSize = 8191; // prime, ~2x the max dictionary size

        readonly int[] keys = new int[TableSize];
        readonly short[] codes = new short[TableSize];
        readonly byte[] block = new byte[256];

        Stream output;
        int blockLength;
        uint bitBuffer;
        int bitCount;

        int minCodeSize, clearCode, endCode, codeSize, lastCode;
        int prefix = -1;

        public void Begin(int minimumCodeSize, Stream stream)
        {
            output = stream;
            blockLength = 0;
            bitBuffer = 0;
            bitCount = 0;

            minCodeSize = minimumCodeSize;
            clearCode = 1 << minCodeSize;
            endCode = clearCode + 1;
            codeSize = minCodeSize + 1;
            lastCode = endCode;
            prefix = -1;
            Array.Clear(keys, 0, TableSize);

            WriteCode(clearCode, codeSize);
        }

        public void Write(byte[] indices, int start, int end)
        {
            int i = start;
            if (prefix < 0 && i < end) prefix = indices[i++];

            for (; i < end; i++)
            {
                int c = indices[i];
                int key = ((prefix << 8) | c) + 1; // +1 so that 0 means "empty slot"
                int slot = (int)((uint)key * 2654435761u % TableSize);

                bool found = false;
                while (true)
                {
                    int k = keys[slot];
                    if (k == key)
                    {
                        prefix = codes[slot];
                        found = true;
                        break;
                    }
                    if (k == 0) break;
                    if (++slot == TableSize) slot = 0;
                }
                if (found) continue;

                WriteCode(prefix, codeSize);

                lastCode++;
                keys[slot] = key;
                codes[slot] = (short)lastCode;
                if (lastCode >= (1 << codeSize)) codeSize++;

                if (lastCode == MaxCode)
                {
                    WriteCode(clearCode, codeSize);
                    Array.Clear(keys, 0, TableSize);
                    codeSize = minCodeSize + 1;
                    lastCode = endCode;
                }

                prefix = c;
            }
        }

        /// <summary>Writes the pending code and the end code, and flushes the last sub-block (without the terminator).</summary>
        public void Finish()
        {
            if (prefix < 0) throw new InvalidOperationException("LZW stream has no data.");
            WriteCode(prefix, codeSize);

            // The decoder grows its code size one code "late" (it allocates an entry for every code it reads),
            // so account for that entry before writing the end code.
            if (lastCode + 1 >= (1 << codeSize) && codeSize < 12) codeSize++;
            WriteCode(endCode, codeSize);

            if (bitCount > 0) WriteByte((byte)bitBuffer);
            FlushBlock();
            output = null;
            prefix = -1;
        }

        void WriteCode(int code, int size)
        {
            bitBuffer |= (uint)code << bitCount;
            bitCount += size;
            while (bitCount >= 8)
            {
                WriteByte((byte)bitBuffer);
                bitBuffer >>= 8;
                bitCount -= 8;
            }
        }

        void WriteByte(byte value)
        {
            block[blockLength++] = value;
            if (blockLength == 255) FlushBlock();
        }

        void FlushBlock()
        {
            if (blockLength == 0) return;
            output.WriteByte((byte)blockLength);
            output.Write(block, 0, blockLength);
            blockLength = 0;
        }
    }
}
