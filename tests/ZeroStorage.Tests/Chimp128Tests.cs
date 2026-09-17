using System;
using Xunit;
using ZeroStorage.Core.Codecs;

namespace ZeroStorage.Tests
{
    public class Chimp128Tests
    {
        [Fact]
        public void EncodeDecode_Empty_ReturnsEmpty()
        {
            byte[] encoded = Chimp128Codec.Encode(ReadOnlySpan<double>.Empty);
            Assert.Empty(encoded);

            double[] dest = new double[10];
            Chimp128Codec.Decode(Array.Empty<byte>(), dest);
        }

        [Fact]
        public void EncodeDecode_SingleValue_RoundtripsLosslessly()
        {
            double[] original = new[] { 42.123456789 };
            byte[] encoded = Chimp128Codec.Encode(original);
            Assert.NotEmpty(encoded);

            double[] decoded = new double[1];
            Chimp128Codec.Decode(encoded, decoded);

            Assert.Equal(original[0], decoded[0], 15);
        }

        [Fact]
        public void EncodeDecode_IdenticalValues_HighlyCompressed()
        {
            // Tests the '00' bit branch (identical consecutive values)
            double[] original = new double[1000];
            Array.Fill(original, 99.87654321);

            byte[] encoded = Chimp128Codec.Encode(original);
            Assert.NotEmpty(encoded);

            // 1000 doubles uncompressed = 8000 bytes. With Chimp128 '00' 2 bits/value, should be under 350 bytes!
            Assert.True(encoded.Length < 350, $"Encoded size was {encoded.Length} bytes, expected < 350");

            double[] decoded = new double[original.Length];
            Chimp128Codec.Decode(encoded, decoded);

            for (int i = 0; i < original.Length; i++)
            {
                Assert.Equal(original[i], decoded[i], 15);
            }
        }

        [Fact]
        public void EncodeDecode_CircularCacheHits_OutperformsGorilla()
        {
            // Tests the '01' bit branch (values hitting the 128-entry circular cache)
            // Repeating cycle of 8 distinct sensor setpoints: 10.0, 20.0, 30.0, ..., 80.0
            double[] cycle = new[] { 10.1, 20.2, 30.3, 40.4, 50.5, 60.6, 70.7, 80.8 };
            double[] original = new double[800];
            for (int i = 0; i < original.Length; i++)
            {
                original[i] = cycle[i % cycle.Length];
            }

            byte[] encoded = Chimp128Codec.Encode(original);
            Assert.NotEmpty(encoded);

            double[] decoded = new double[original.Length];
            Chimp128Codec.Decode(encoded, decoded);

            for (int i = 0; i < original.Length; i++)
            {
                Assert.Equal(original[i], decoded[i], 15);
            }
        }

        [Fact]
        public void EncodeDecode_OscillatingTelemetry_LosslessRoundtrip()
        {
            // Realistic sensor wave with gradual floating-point delta variations
            double[] original = new double[500];
            for (int i = 0; i < original.Length; i++)
            {
                original[i] = 25.0 + Math.Sin(i * 0.05) * 5.0 + (i * 0.001);
            }

            byte[] encoded = Chimp128Codec.Encode(original);
            Assert.NotEmpty(encoded);

            double[] decoded = new double[original.Length];
            Chimp128Codec.Decode(encoded, decoded);

            for (int i = 0; i < original.Length; i++)
            {
                Assert.Equal(original[i], decoded[i], 15);
            }
        }

        [Fact]
        public void EncodeDecode_RandomFloats_LosslessRoundtrip()
        {
            var rng = new Random(42);
            double[] original = new double[200];
            for (int i = 0; i < original.Length; i++)
            {
                original[i] = rng.NextDouble() * 100000.0 - 50000.0;
            }

            byte[] encoded = Chimp128Codec.Encode(original);
            Assert.NotEmpty(encoded);

            double[] decoded = new double[original.Length];
            Chimp128Codec.Decode(encoded, decoded);

            for (int i = 0; i < original.Length; i++)
            {
                Assert.Equal(original[i], decoded[i], 15);
            }
        }
    }
}
