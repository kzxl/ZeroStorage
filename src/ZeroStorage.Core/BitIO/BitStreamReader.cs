using System;

namespace ZeroStorage.Core.BitIO
{
    /// <summary>
    /// High-performance bit-level stream reader for variable-length decoding.
    /// </summary>
    public sealed class BitStreamReader
    {
        private readonly byte[] _buffer;
        private int _byteOffset;
        private int _bitOffset; // 0 to 7 (bits read from current byte)

        public bool HasMoreBits => _byteOffset < _buffer.Length;

        public BitStreamReader(byte[] buffer, int offset = 0)
        {
            _buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
            _byteOffset = offset;
            _bitOffset = 0;
        }

        /// <summary>
        /// Reads a single bit (returns 0 or 1).
        /// </summary>
        public int ReadBit()
        {
            if (_byteOffset >= _buffer.Length)
                throw new InvalidOperationException("End of bit stream reached.");

            int bit = (_buffer[_byteOffset] >> (7 - _bitOffset)) & 1;
            _bitOffset++;
            if (_bitOffset == 8)
            {
                _byteOffset++;
                _bitOffset = 0;
            }
            return bit;
        }

        /// <summary>
        /// Reads 'numBits' from the stream and packs them into a 64-bit unsigned integer.
        /// </summary>
        public ulong ReadBits(int numBits)
        {
            if (numBits < 0 || numBits > 64)
                throw new ArgumentOutOfRangeException(nameof(numBits));

            ulong result = 0;
            for (int i = 0; i < numBits; i++)
            {
                uint bit = (uint)ReadBit();
                result = (result << 1) | (ulong)bit;
            }
            return result;
        }
    }
}
