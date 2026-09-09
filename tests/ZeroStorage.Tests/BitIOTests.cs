using Xunit;
using ZeroStorage.Core.BitIO;

namespace ZeroStorage.Tests
{
    public class BitIOTests
    {
        [Fact]
        public void BitStream_WriteAndRead_MatchesBitExact()
        {
            byte[] bytes;
            using (var writer = new BitStreamWriter())
            {
                writer.WriteBit(1);
                writer.WriteBit(0);
                writer.WriteBit(1);
                writer.WriteBits(0b1011, 4); // 1, 0, 1, 1
                writer.WriteBit(0);          // Total 8 bits in first byte: 10110110 = 0xB6
                writer.WriteBits(0xDEADBEEFCAFE, 48);
                bytes = writer.ToByteArray();
            }

            Assert.Equal(7, bytes.Length); // 8 bits + 48 bits = 56 bits = 7 bytes
            Assert.Equal(0xB6, bytes[0]);

            var reader = new BitStreamReader(bytes);
            Assert.Equal(1, reader.ReadBit());
            Assert.Equal(0, reader.ReadBit());
            Assert.Equal(1, reader.ReadBit());
            Assert.Equal(0b1011UL, reader.ReadBits(4));
            Assert.Equal(0, reader.ReadBit());
            Assert.Equal(0xDEADBEEFCAFEUL, reader.ReadBits(48));
        }
    }
}
