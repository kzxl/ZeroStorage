using System;
using System.Collections.Generic;
using System.IO;

namespace ZeroStorage.Core.Persistence
{
    /// <summary>
    /// Represents an individual record within a Write-Ahead Log.
    /// </summary>
    public sealed class WalRecord
    {
        public long Lsn { get; }
        public byte RecordType { get; }
        public byte[] Payload { get; }

        public WalRecord(long lsn, byte recordType, byte[] payload)
        {
            Lsn = lsn;
            RecordType = recordType;
            Payload = payload ?? Array.Empty<byte>();
        }
    }

    /// <summary>
    /// High-durability append-only Write-Ahead Log (WAL) with CRC32 integrity verification and crash recovery.
    /// Pure C# with zero external dependencies.
    /// </summary>
    public sealed class WriteAheadLog : IDisposable
    {
        private const ushort FileMagic = 0x5741; // "WA"
        private const ushort FileVersion = 1;
        private const ushort RecordMarker = 0xA55A;
        private const int HeaderSize = 4; // Magic (2) + Version (2)

        private static readonly uint[] CrcTable = InitializeCrcTable();

        private readonly string _filePath;
        private readonly FileStream _stream;
        private readonly BinaryWriter _writer;
        private readonly object _syncRoot = new object();
        private long _lastLsn;
        private bool _disposed;

        public string FilePath => _filePath;
        public long LastLsn => _lastLsn;
        public long Length => _stream.Length;

        public WriteAheadLog(string filePath, bool autoFlush = true)
        {
            _filePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
            string? dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            bool isNew = !File.Exists(filePath) || new FileInfo(filePath).Length < HeaderSize;
            _stream = new FileStream(filePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
            _writer = new BinaryWriter(_stream);

            if (isNew)
            {
                _writer.Write(FileMagic);
                _writer.Write(FileVersion);
                _writer.Flush();
                _lastLsn = 0;
            }
            else
            {
                // Scan to find last valid LSN
                _lastLsn = RecoverLastLsn();
                _stream.Seek(0, SeekOrigin.End);
            }
        }

        private static uint[] InitializeCrcTable()
        {
            uint[] table = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint entry = i;
                for (int j = 0; j < 8; j++)
                {
                    if ((entry & 1) == 1)
                        entry = (entry >> 1) ^ 0xEDB88320u;
                    else
                        entry >>= 1;
                }
                table[i] = entry;
            }
            return table;
        }

        public static uint ComputeCrc32(byte[] buffer, int offset, int count)
        {
            uint crc = 0xFFFFFFFFu;
            for (int i = offset; i < offset + count; i++)
            {
                byte b = buffer[i];
                crc = (crc >> 8) ^ CrcTable[(crc & 0xFF) ^ b];
            }
            return ~crc;
        }

        public long Append(byte recordType, byte[] payload, bool flush = false)
        {
            if (payload == null) throw new ArgumentNullException(nameof(payload));

            lock (_syncRoot)
            {
                long lsn = ++_lastLsn;
                int recordLength = payload.Length;

                // Build record buffer for CRC
                // Marker (2) + LSN (8) + Type (1) + Length (4) + Payload (N)
                int headerLen = 2 + 8 + 1 + 4;
                byte[] raw = new byte[headerLen + recordLength];

                raw[0] = (byte)(RecordMarker & 0xFF);
                raw[1] = (byte)((RecordMarker >> 8) & 0xFF);

                for (int i = 0; i < 8; i++)
                    raw[2 + i] = (byte)((lsn >> (i * 8)) & 0xFF);

                raw[10] = recordType;

                raw[11] = (byte)(recordLength & 0xFF);
                raw[12] = (byte)((recordLength >> 8) & 0xFF);
                raw[13] = (byte)((recordLength >> 16) & 0xFF);
                raw[14] = (byte)((recordLength >> 24) & 0xFF);

                if (recordLength > 0)
                {
                    Buffer.BlockCopy(payload, 0, raw, headerLen, recordLength);
                }

                uint crc = ComputeCrc32(raw, 0, raw.Length);

                _stream.Seek(0, SeekOrigin.End);
                _writer.Write(raw);
                _writer.Write(crc);

                if (flush)
                {
                    _writer.Flush();
                    _stream.Flush(true);
                }

                return lsn;
            }
        }

        public List<WalRecord> ReadAllRecords()
        {
            lock (_syncRoot)
            {
                var records = new List<WalRecord>();
                _stream.Seek(0, SeekOrigin.Begin);

                using (var reader = new BinaryReader(_stream, System.Text.Encoding.UTF8, leaveOpen: true))
                {
                    if (_stream.Length < HeaderSize) return records;

                    ushort magic = reader.ReadUInt16();
                    ushort version = reader.ReadUInt16();
                    if (magic != FileMagic || version != FileVersion)
                        return records;

                    while (_stream.Position + 15 <= _stream.Length) // 2 (marker) + 8 (lsn) + 1 (type) + 4 (len)
                    {
                        long startPos = _stream.Position;
                        ushort marker = reader.ReadUInt16();
                        if (marker != RecordMarker)
                            break; // Corrupted or incomplete record, stop replay

                        long lsn = reader.ReadInt64();
                        byte type = reader.ReadByte();
                        int length = reader.ReadInt32();

                        if (length < 0 || _stream.Position + length + 4 > _stream.Length)
                            break; // Truncated record

                        byte[] payload = reader.ReadBytes(length);
                        uint expectedCrc = reader.ReadUInt32();

                        // Recompute CRC
                        int rawLen = 15 + length;
                        byte[] checkBuf = new byte[rawLen];
                        checkBuf[0] = (byte)(marker & 0xFF);
                        checkBuf[1] = (byte)((marker >> 8) & 0xFF);
                        for (int i = 0; i < 8; i++) checkBuf[2 + i] = (byte)((lsn >> (i * 8)) & 0xFF);
                        checkBuf[10] = type;
                        checkBuf[11] = (byte)(length & 0xFF);
                        checkBuf[12] = (byte)((length >> 8) & 0xFF);
                        checkBuf[13] = (byte)((length >> 16) & 0xFF);
                        checkBuf[14] = (byte)((length >> 24) & 0xFF);
                        if (length > 0) Buffer.BlockCopy(payload, 0, checkBuf, 15, length);

                        uint computedCrc = ComputeCrc32(checkBuf, 0, rawLen);
                        if (computedCrc != expectedCrc)
                            break; // CRC verification failed, stop replay

                        records.Add(new WalRecord(lsn, type, payload));
                    }
                }

                _stream.Seek(0, SeekOrigin.End);
                return records;
            }
        }

        private long RecoverLastLsn()
        {
            var records = ReadAllRecords();
            return records.Count > 0 ? records[records.Count - 1].Lsn : 0;
        }

        public void Truncate()
        {
            lock (_syncRoot)
            {
                _stream.SetLength(0);
                _stream.Seek(0, SeekOrigin.Begin);
                _writer.Write(FileMagic);
                _writer.Write(FileVersion);
                _writer.Flush();
                _stream.Flush(true);
                _lastLsn = 0;
            }
        }

        public void Flush()
        {
            lock (_syncRoot)
            {
                _writer.Flush();
                _stream.Flush(true);
            }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                lock (_syncRoot)
                {
                    _writer?.Dispose();
                    _stream?.Dispose();
                    _disposed = true;
                }
            }
        }
    }
}
