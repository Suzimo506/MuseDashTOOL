using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;

namespace MdModManager.Services;

// Only the disk envelope changes; the complete v9 JSON and its numeric precision stay intact.
internal static class ReplayStorage
{
    private static readonly byte[] Magic = "MDRPGZ1\n"u8.ToArray();
    private const int HeaderSize = 56;


    internal static Stream OpenRead(string path, bool verifyPayload = true, Action? checkpoint = null)
    {
        var input = File.OpenRead(path);
        try
        {
            Span<byte> header = stackalloc byte[HeaderSize];
            var prefixLength = ReadUntilFull(input, header[..Magic.Length]);
            if (prefixLength != Magic.Length || !header[..Magic.Length].SequenceEqual(Magic))
            {
                if (prefixLength >= 4 && header[..4].SequenceEqual(Magic.AsSpan(0, 4)))
                    throw new InvalidDataException("Unsupported replay storage header.");
                input.Position = 0;
                return input; // Existing uncompressed v9 JSON remains readable.
            }
            if (ReadUntilFull(input, header[Magic.Length..]) != HeaderSize - Magic.Length)
                throw new InvalidDataException("Incomplete replay storage header.");
            var compressedLength = BinaryPrimitives.ReadInt64LittleEndian(header[8..]);
            var jsonLength = BinaryPrimitives.ReadInt64LittleEndian(header[16..]);
            if (compressedLength <= 0 || jsonLength <= 0 || compressedLength != input.Length - HeaderSize)
                throw new InvalidDataException("Replay storage length does not match the file.");
            if (verifyPayload)
            {
                var checksum = ComputeChecksum(input, checkpoint);
                if (!CryptographicOperations.FixedTimeEquals(checksum, header[24..]))
                    throw new InvalidDataException("Replay storage checksum does not match.");
                input.Position = HeaderSize;
            }
            return new LengthCheckedReadStream(new GZipStream(input, CompressionMode.Decompress), jsonLength);
        }
        catch
        {
            input.Dispose();
            throw;
        }
    }

    private static byte[] ComputeChecksum(Stream input, Action? checkpoint = null)
    {
        if (checkpoint != null)
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            Span<byte> block = stackalloc byte[16384];
            while (true)
            {
                checkpoint();
                var count = input.Read(block);
                if (count == 0) return hash.GetHashAndReset();
                hash.AppendData(block[..count]);
            }
        }
        using var sha = SHA256.Create();
        return sha.ComputeHash(input);
    }

    private static int ReadUntilFull(Stream input, Span<byte> buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var count = input.Read(buffer[total..]);
            if (count == 0) break;
            total += count;
        }
        return total;
    }


    private sealed class LengthCheckedReadStream(Stream input, long expectedLength) : Stream
    {
        private long _position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => expectedLength;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            var count = input.Read(buffer);
            _position += count;
            if (_position > expectedLength || count == 0 && buffer.Length > 0 && _position != expectedLength)
                throw new InvalidDataException("Replay JSON length does not match the storage header.");
            return count;
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) input.Dispose();
            base.Dispose(disposing);
        }
        public override void Flush() => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
