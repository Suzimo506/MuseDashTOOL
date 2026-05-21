using System;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;

namespace MdModManager.Helpers;

public static class MsgPackDecoder
{
    public static JsonNode? Decode(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        return ReadNode(ms);
    }

    private static JsonNode? ReadNode(Stream stream)
    {
        int b = stream.ReadByte();
        if (b == -1) throw new EndOfStreamException();

        if (b >= 0x00 && b <= 0x7f)
        {
            return JsonValue.Create((long)b);
        }

        if (b >= 0x80 && b <= 0x8f)
        {
            int count = b - 0x80;
            return ReadMap(stream, count);
        }

        if (b >= 0x90 && b <= 0x9f)
        {
            int count = b - 0x90;
            return ReadArray(stream, count);
        }

        if (b >= 0xa0 && b <= 0xbf)
        {
            int length = b - 0xa0;
            return JsonValue.Create(ReadString(stream, length));
        }

        if (b == 0xc0)
        {
            return null;
        }

        if (b == 0xc2)
        {
            return JsonValue.Create(false);
        }

        if (b == 0xc3)
        {
            return JsonValue.Create(true);
        }

        if (b == 0xc4)
        {
            int len = stream.ReadByte();
            if (len == -1) throw new EndOfStreamException();
            var bin = ReadBytes(stream, len);
            return JsonValue.Create(Convert.ToBase64String(bin));
        }
        if (b == 0xc5)
        {
            int len = ReadBigEndianUint16(stream);
            var bin = ReadBytes(stream, len);
            return JsonValue.Create(Convert.ToBase64String(bin));
        }
        if (b == 0xc6)
        {
            int len = (int)ReadBigEndianUint32(stream);
            var bin = ReadBytes(stream, len);
            return JsonValue.Create(Convert.ToBase64String(bin));
        }

        if (b == 0xca)
        {
            var buf = ReadBytes(stream, 4);
            if (BitConverter.IsLittleEndian) Array.Reverse(buf);
            float val = BitConverter.ToSingle(buf, 0);
            return JsonValue.Create(val);
        }

        if (b == 0xcb)
        {
            var buf = ReadBytes(stream, 8);
            if (BitConverter.IsLittleEndian) Array.Reverse(buf);
            double val = BitConverter.ToDouble(buf, 0);
            return JsonValue.Create(val);
        }

        if (b == 0xcc)
        {
            int val = stream.ReadByte();
            if (val == -1) throw new EndOfStreamException();
            return JsonValue.Create((long)val);
        }

        if (b == 0xcd)
        {
            return JsonValue.Create((long)ReadBigEndianUint16(stream));
        }

        if (b == 0xce)
        {
            return JsonValue.Create((long)ReadBigEndianUint32(stream));
        }

        if (b == 0xcf)
        {
            return JsonValue.Create((long)ReadBigEndianUint64(stream));
        }

        if (b == 0xd0)
        {
            int val = stream.ReadByte();
            if (val == -1) throw new EndOfStreamException();
            sbyte sval = (sbyte)val;
            return JsonValue.Create((long)sval);
        }

        if (b == 0xd1)
        {
            short val = (short)ReadBigEndianUint16(stream);
            return JsonValue.Create((long)val);
        }

        if (b == 0xd2)
        {
            int val = (int)ReadBigEndianUint32(stream);
            return JsonValue.Create((long)val);
        }

        if (b == 0xd3)
        {
            long val = (long)ReadBigEndianUint64(stream);
            return JsonValue.Create(val);
        }

        if (b == 0xd9)
        {
            int len = stream.ReadByte();
            if (len == -1) throw new EndOfStreamException();
            return JsonValue.Create(ReadString(stream, len));
        }
        if (b == 0xda)
        {
            int len = ReadBigEndianUint16(stream);
            return JsonValue.Create(ReadString(stream, len));
        }
        if (b == 0xdb)
        {
            int len = (int)ReadBigEndianUint32(stream);
            return JsonValue.Create(ReadString(stream, len));
        }

        if (b == 0xdc)
        {
            int count = ReadBigEndianUint16(stream);
            return ReadArray(stream, count);
        }
        if (b == 0xdd)
        {
            int count = (int)ReadBigEndianUint32(stream);
            return ReadArray(stream, count);
        }

        if (b == 0xde)
        {
            int count = ReadBigEndianUint16(stream);
            return ReadMap(stream, count);
        }
        if (b == 0xdf)
        {
            int count = (int)ReadBigEndianUint32(stream);
            return ReadMap(stream, count);
        }

        if (b >= 0xe0 && b <= 0xff)
        {
            sbyte sval = (sbyte)b;
            return JsonValue.Create((long)sval);
        }

        throw new NotSupportedException();
    }

    private static JsonObject ReadMap(Stream stream, int count)
    {
        var obj = new JsonObject();
        for (int i = 0; i < count; i++)
        {
            var keyNode = ReadNode(stream);
            if (keyNode == null) throw new InvalidDataException();
            string key = keyNode.ToString();
            var valNode = ReadNode(stream);
            obj.Add(key, valNode);
        }
        return obj;
    }

    private static JsonArray ReadArray(Stream stream, int count)
    {
        var arr = new JsonArray();
        for (int i = 0; i < count; i++)
        {
            arr.Add(ReadNode(stream));
        }
        return arr;
    }

    private static string ReadString(Stream stream, int length)
    {
        if (length == 0) return string.Empty;
        var bytes = ReadBytes(stream, length);
        return Encoding.UTF8.GetString(bytes);
    }

    private static byte[] ReadBytes(Stream stream, int length)
    {
        var buffer = new byte[length];
        int totalRead = 0;
        while (totalRead < length)
        {
            int read = stream.Read(buffer, totalRead, length - totalRead);
            if (read <= 0) throw new EndOfStreamException();
            totalRead += read;
        }
        return buffer;
    }

    private static int ReadBigEndianUint16(Stream stream)
    {
        int b1 = stream.ReadByte();
        int b2 = stream.ReadByte();
        if (b1 == -1 || b2 == -1) throw new EndOfStreamException();
        return (b1 << 8) | b2;
    }

    private static uint ReadBigEndianUint32(Stream stream)
    {
        var buf = ReadBytes(stream, 4);
        return ((uint)buf[0] << 24) | ((uint)buf[1] << 16) | ((uint)buf[2] << 8) | buf[3];
    }

    private static ulong ReadBigEndianUint64(Stream stream)
    {
        var buf = ReadBytes(stream, 8);
        return ((ulong)buf[0] << 56) | ((ulong)buf[1] << 48) | ((ulong)buf[2] << 40) | ((ulong)buf[3] << 32) |
               ((ulong)buf[4] << 24) | ((ulong)buf[5] << 16) | ((ulong)buf[6] << 8) | buf[7];
    }
}
