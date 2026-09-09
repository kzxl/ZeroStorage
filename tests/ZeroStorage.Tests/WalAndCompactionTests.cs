using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Xunit;
using ZeroStorage.Core.Gorilla;
using ZeroStorage.Core.Persistence;
using ZeroStorage.Core.TimeSeries;

namespace ZeroStorage.Tests
{
    public class WalAndCompactionTests
    {
        [Fact]
        public void TestWal_AppendAndReplay()
        {
            string tempFile = Path.Combine(Path.GetTempPath(), $"wal_test_{Guid.NewGuid():N}.wal");
            try
            {
                using (var wal = new WriteAheadLog(tempFile))
                {
                    wal.Append(1, Encoding.UTF8.GetBytes("Record 1"));
                    wal.Append(2, Encoding.UTF8.GetBytes("Record 2"));
                    wal.Append(1, Encoding.UTF8.GetBytes("Record 3"), flush: true);

                    Assert.Equal(3, wal.LastLsn);

                    var records = wal.ReadAllRecords();
                    Assert.Equal(3, records.Count);
                    Assert.Equal(1, records[0].Lsn);
                    Assert.Equal("Record 1", Encoding.UTF8.GetString(records[0].Payload));
                    Assert.Equal(2, records[1].Lsn);
                    Assert.Equal("Record 2", Encoding.UTF8.GetString(records[1].Payload));
                    Assert.Equal(3, records[2].Lsn);
                    Assert.Equal("Record 3", Encoding.UTF8.GetString(records[2].Payload));
                }

                // Re-open and verify persistence
                using (var wal = new WriteAheadLog(tempFile))
                {
                    Assert.Equal(3, wal.LastLsn);
                    var records = wal.ReadAllRecords();
                    Assert.Equal(3, records.Count);
                }
            }
            finally
            {
                if (File.Exists(tempFile)) File.Delete(tempFile);
            }
        }

        [Fact]
        public void TestWal_CrashRecovery_DetectsCorruptedCrc()
        {
            string tempFile = Path.Combine(Path.GetTempPath(), $"wal_corrupt_{Guid.NewGuid():N}.wal");
            try
            {
                using (var wal = new WriteAheadLog(tempFile))
                {
                    wal.Append(1, Encoding.UTF8.GetBytes("Good record 1"), flush: true);
                    wal.Append(1, Encoding.UTF8.GetBytes("Target record 2"), flush: true);
                    wal.Append(1, Encoding.UTF8.GetBytes("Record 3"), flush: true);
                }

                // Corrupt byte in the middle of file (in record 2)
                byte[] bytes = File.ReadAllBytes(tempFile);
                bytes[bytes.Length - 25] ^= 0xFF; // Flip bits
                File.WriteAllBytes(tempFile, bytes);

                // Recover
                using (var wal = new WriteAheadLog(tempFile))
                {
                    var records = wal.ReadAllRecords();
                    // Must cleanly recover at least the valid record before corruption
                    Assert.True(records.Count >= 1);
                    Assert.Equal("Good record 1", Encoding.UTF8.GetString(records[0].Payload));
                }
            }
            finally
            {
                if (File.Exists(tempFile)) File.Delete(tempFile);
            }
        }

        [Fact]
        public void TestWal_Truncate()
        {
            string tempFile = Path.Combine(Path.GetTempPath(), $"wal_trunc_{Guid.NewGuid():N}.wal");
            try
            {
                using (var wal = new WriteAheadLog(tempFile))
                {
                    wal.Append(1, Encoding.UTF8.GetBytes("Record A"));
                    wal.Append(1, Encoding.UTF8.GetBytes("Record B"));
                    Assert.Equal(2, wal.LastLsn);

                    wal.Truncate();
                    Assert.Equal(0, wal.LastLsn);
                    var records = wal.ReadAllRecords();
                    Assert.Empty(records);
                }
            }
            finally
            {
                if (File.Exists(tempFile)) File.Delete(tempFile);
            }
        }

        [Fact]
        public void TestCompactor_MergesMultipleBlocks()
        {
            var blocks = new List<TimeSeriesBlock>();
            long baseTime = 1700000000000L;

            // Create 4 blocks of 50 points each
            for (int b = 0; b < 4; b++)
            {
                var points = new List<TimeSeriesPoint>();
                for (int i = 0; i < 50; i++)
                {
                    long t = baseTime + (b * 50 + i) * 1000;
                    double v = Math.Sin(i * 0.1) + b;
                    points.Add(new TimeSeriesPoint(t, v));
                }
                blocks.Add(TimeSeriesBlock.FromPoints(metricId: 42, points));
            }

            // Compact into 1 block
            var compacted = TimeSeriesCompactor.Compact(42, blocks);

            Assert.Equal(42, compacted.MetricId);
            Assert.Equal(200, compacted.Count);
            Assert.Equal(baseTime, compacted.StartTimeMs);
            Assert.Equal(baseTime + 199 * 1000, compacted.EndTimeMs);

            var decompressed = compacted.Decompress();
            Assert.Equal(200, decompressed.Count);
            Assert.Equal(baseTime, decompressed[0].TimestampMs);
            Assert.Equal(baseTime + 199 * 1000, decompressed[199].TimestampMs);
        }

        [Fact]
        public void TestCompactor_CompactLogFile()
        {
            string srcLog = Path.Combine(Path.GetTempPath(), $"src_log_{Guid.NewGuid():N}.zts");
            string dstLog = Path.Combine(Path.GetTempPath(), $"dst_log_{Guid.NewGuid():N}.zts");

            try
            {
                long baseTime = 1700000000000L;
                using (var log = new MemoryMappedTimeSeriesLog(srcLog, 4 * 1024 * 1024))
                {
                    // Metric 1: 3 blocks
                    for (int b = 0; b < 3; b++)
                    {
                        var pts = new List<TimeSeriesPoint>();
                        for (int i = 0; i < 20; i++)
                            pts.Add(new TimeSeriesPoint(baseTime + (b * 20 + i) * 1000, i * 1.5));
                        log.AppendBlock(TimeSeriesBlock.FromPoints(1, pts));
                    }

                    // Metric 2: 2 blocks
                    for (int b = 0; b < 2; b++)
                    {
                        var pts = new List<TimeSeriesPoint>();
                        for (int i = 0; i < 30; i++)
                            pts.Add(new TimeSeriesPoint(baseTime + (b * 30 + i) * 1000, i * 2.0));
                        log.AppendBlock(TimeSeriesBlock.FromPoints(2, pts));
                    }
                }

                using (var src = new MemoryMappedTimeSeriesLog(srcLog, 4 * 1024 * 1024))
                {
                    int compactedCount = TimeSeriesCompactor.CompactLog(src, dstLog);
                    Assert.Equal(2, compactedCount); // 2 metrics compacted
                }

                // Verify target log
                using (var dst = new MemoryMappedTimeSeriesLog(dstLog, 4 * 1024 * 1024))
                {
                    var blocks = dst.ReadAllBlocks();
                    Assert.Equal(2, blocks.Count);

                    var b1 = dst.Query(1, 0, long.MaxValue);
                    Assert.Single(b1);
                    Assert.Equal(60, b1[0].Count);

                    var b2 = dst.Query(2, 0, long.MaxValue);
                    Assert.Single(b2);
                    Assert.Equal(60, b2[0].Count);
                }
            }
            finally
            {
                if (File.Exists(srcLog)) File.Delete(srcLog);
                if (File.Exists(dstLog)) File.Delete(dstLog);
            }
        }
    }
}
