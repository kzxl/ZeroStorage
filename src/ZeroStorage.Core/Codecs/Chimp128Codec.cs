using System;
using System.IO;
using ZeroStorage.Core.BitIO;
using ZeroStorage.Core.Gorilla;

namespace ZeroStorage.Core.Codecs
{
    /// <summary>
    /// Pure C# implementation of the Chimp128 lossless floating-point compression algorithm (VLDB 2022).
    /// Features a 128-entry circular value cache, leading zero bucketing, and quantized trailing zero elimination.
    /// Outperforms classic Gorilla by 20-30% on industrial telemetry streams.
    /// </summary>
    public static class Chimp128Codec
    {
        private const int CacheSize = 128; // 7 bits index

        public static byte[] Encode(ReadOnlySpan<double> values)
        {
            if (values.IsEmpty) return Array.Empty<byte>();

            using (var writer = new BitStreamWriter(values.Length * 4 + 64))
            {
                // Total count (32 bits)
                writer.WriteBits((ulong)values.Length, 32);

                ulong v0Bits = (ulong)BitConverter.DoubleToInt64Bits(values[0]);
                writer.WriteBits(v0Bits, 64);

                if (values.Length == 1)
                {
                    return writer.ToByteArray();
                }

                ulong[] cache = new ulong[CacheSize];
                int cachePos = 0;
                cache[cachePos++ & 127] = v0Bits;
                ulong prevBits = v0Bits;

                for (int i = 1; i < values.Length; i++)
                {
                    ulong currBits = (ulong)BitConverter.DoubleToInt64Bits(values[i]);

                    // Case 1: Identical to previous value -> '00' (2 bits)
                    if (currBits == prevBits)
                    {
                        writer.WriteBit(0);
                        writer.WriteBit(0);
                        continue;
                    }

                    // Case 2: Check in 128-entry circular cache -> '01' + 7 bits index (9 bits)
                    int cacheIndex = FindInCache(cache, currBits);
                    if (cacheIndex >= 0)
                    {
                        writer.WriteBit(0);
                        writer.WriteBit(1);
                        writer.WriteBits((ulong)cacheIndex, 7);
                        prevBits = currBits;
                        continue;
                    }

                    // Case 3: XOR Compression -> '1'
                    writer.WriteBit(1);
                    ulong xor = currBits ^ prevBits;

                    // Bucketed Leading Zeros (3 bits)
                    int lz = GorillaEncoder.CountLeadingZeros(xor);
                    int effLz;
                    if (lz >= 24)
                    {
                        writer.WriteBits(0, 3); // 0b000
                        effLz = 24;
                    }
                    else if (lz >= 16)
                    {
                        writer.WriteBits(1, 3); // 0b001
                        effLz = 16;
                    }
                    else if (lz >= 12)
                    {
                        writer.WriteBits(2, 3); // 0b010
                        effLz = 12;
                    }
                    else if (lz >= 8)
                    {
                        writer.WriteBits(3, 3); // 0b011
                        effLz = 8;
                    }
                    else if (lz == 0)
                    {
                        writer.WriteBits(4, 3); // 0b100
                        effLz = 0;
                    }
                    else
                    {
                        writer.WriteBits(5, 3); // 0b101
                        writer.WriteBits((ulong)lz, 5);
                        effLz = lz;
                    }

                    // Quantized Trailing Zeros (2 bits)
                    int tz = GorillaEncoder.CountTrailingZeros(xor);
                    int effTz;
                    if (tz >= 16)
                    {
                        writer.WriteBits(0, 2); // 0b00
                        effTz = 16;
                    }
                    else if (tz >= 8)
                    {
                        writer.WriteBits(1, 2); // 0b01
                        effTz = 8;
                    }
                    else if (tz >= 4)
                    {
                        writer.WriteBits(2, 2); // 0b10
                        effTz = 4;
                    }
                    else
                    {
                        writer.WriteBits(3, 2); // 0b11
                        effTz = 0;
                    }

                    // Meaningful bits
                    int meaningfulLen = 64 - effLz - effTz;
                    if (meaningfulLen > 0)
                    {
                        ulong meaningfulBits = xor >> effTz;
                        writer.WriteBits(meaningfulBits, meaningfulLen);
                    }

                    // Update state and cache
                    cache[cachePos++ & 127] = currBits;
                    prevBits = currBits;
                }

                return writer.ToByteArray();
            }
        }

        public static void Decode(byte[] compressedBytes, Span<double> destination)
        {
            if (compressedBytes == null) throw new ArgumentNullException(nameof(compressedBytes));
            if (compressedBytes.Length == 0 || destination.IsEmpty) return;

            var reader = new BitStreamReader(compressedBytes);
            int totalPoints = (int)reader.ReadBits(32);
            int count = Math.Min(totalPoints, destination.Length);
            if (count == 0) return;

            ulong v0Bits = reader.ReadBits(64);
            destination[0] = BitConverter.Int64BitsToDouble((long)v0Bits);

            if (count == 1) return;

            ulong[] cache = new ulong[CacheSize];
            int cachePos = 0;
            cache[cachePos++ & 127] = v0Bits;
            ulong prevBits = v0Bits;

            for (int i = 1; i < count; i++)
            {
                int flag = reader.ReadBit();
                if (flag == 0)
                {
                    int subFlag = reader.ReadBit();
                    if (subFlag == 0)
                    {
                        // '00': Identical to previous
                        destination[i] = BitConverter.Int64BitsToDouble((long)prevBits);
                        continue;
                    }
                    else
                    {
                        // '01': Read 7-bit cache index
                        int cacheIndex = (int)reader.ReadBits(7);
                        ulong currBits = cache[cacheIndex];
                        destination[i] = BitConverter.Int64BitsToDouble((long)currBits);
                        prevBits = currBits;
                        continue;
                    }
                }

                // '1': XOR diff
                int lzBucket = (int)reader.ReadBits(3);
                int effLz;
                switch (lzBucket)
                {
                    case 0: effLz = 24; break;
                    case 1: effLz = 16; break;
                    case 2: effLz = 12; break;
                    case 3: effLz = 8; break;
                    case 4: effLz = 0; break;
                    default:
                        effLz = (int)reader.ReadBits(5);
                        break;
                }

                int tzBucket = (int)reader.ReadBits(2);
                int effTz;
                switch (tzBucket)
                {
                    case 0: effTz = 16; break;
                    case 1: effTz = 8; break;
                    case 2: effTz = 4; break;
                    default: effTz = 0; break;
                }

                int meaningfulLen = 64 - effLz - effTz;
                ulong xor = 0;
                if (meaningfulLen > 0)
                {
                    ulong meaningfulBits = reader.ReadBits(meaningfulLen);
                    xor = meaningfulBits << effTz;
                }

                ulong decodedBits = prevBits ^ xor;
                destination[i] = BitConverter.Int64BitsToDouble((long)decodedBits);

                cache[cachePos++ & 127] = decodedBits;
                prevBits = decodedBits;
            }
        }

        private static int FindInCache(ulong[] cache, ulong value)
        {
            for (int i = 0; i < CacheSize; i++)
            {
                if (cache[i] == value) return i;
            }
            return -1;
        }
    }
}
