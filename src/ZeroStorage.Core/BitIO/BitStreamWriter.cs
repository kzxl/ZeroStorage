using System;
using System.IO;

namespace ZeroStorage.Core.BitIO
{
    /// <summary>
    /// High-performance bit-level stream writer for variable-length encoding.
    /// </summary>
    public sealed class BitStreamWriter : IDisposable
    {
        private readonly MemoryStream _stream;
        private byte _currentByte;
        private int _bitPosition; // 0 to 7 (bits written to _currentByte)

        public BitStreamWriter(int initialCapacity = 4096)
        {
            _stream = new MemoryStream(initialCapacity);
            _currentByte = 0;
            _bitPosition = 0;
        }

        /// <summary>
        /// Writes a single bit (0 or 1).
        /// </summary>
        public void WriteBit(int bit)
        {
            if (bit != 0)
            {
                _currentByte |= (byte)(1 << (7 - _bitPosition));
            }

            _bitPosition++;
            if (_bitPosition == 8)
            {
                _stream.WriteByte(_currentByte);
                _currentByte = 0;
                _bitPosition = 0;
            }
        }

        /// <summary>
        /// Writes the lowest 'numBits' of 'value' to the stream, most significant bit first.
        /// numBits must be between 1 and 64.
        /// </summary>
        public void WriteBits(ulong value, int numBits)
        {
            if (numBits < 0 || numBits > 64)
                throw new ArgumentOutOfRangeException(nameof(numBits));

            for (int i = numBits - 1; i >= 0; i--)
            {
                int bit = (int)((value >> i) & 1UL);
                WriteBit(bit);
            }
        }

        /// <summary>
        /// Flushes any pending bits by padding the remainder of the current byte with zeros.
        /// </summary>
        public void Flush()
        {
            if (_bitPosition > 0)
            {
                _stream.WriteByte(_currentByte);
                _currentByte = 0;
                _bitPosition = 0;
            }
        }

        /// <summary>
        /// Flushes pending bits and returns the complete byte array.
        /// </summary>
        public byte[] ToByteArray()
        {
            Flush();
            return _stream.ToArray();
        }

        public void Dispose()
        {
            _stream.Dispose();
        }
    }
}
