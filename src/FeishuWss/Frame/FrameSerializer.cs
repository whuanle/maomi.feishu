using System.Buffers.Binary;

namespace FeishuWss.Frames;

/// <summary>
/// 帧编解码。把 Frame 序列化为 protobuf 二进制，对应 oapi-sdk-go/ws/pbbp2.proto。
/// 这里不依赖 protobuf-net，纯手写 wire-format，理由：
///   1. 字段少且固定，没有反射开销。
///   2. 与 Go 端 gogoproto 默认（按字段声明顺序写出）兼容，避免类型差异。
/// </summary>
public static class FrameSerializer
{
    // Frame 字段 tag
    private const int TagSeqId = 1;
    private const int TagLogId = 2;
    private const int TagService = 3;
    private const int TagMethod = 4;
    private const int TagHeaders = 5;
    private const int TagPayloadEncoding = 6;
    private const int TagPayloadType = 7;
    private const int TagPayload = 8;
    private const int TagLogIdNew = 9;

    // Header 子字段 tag
    private const int TagHeaderKey = 1;
    private const int TagHeaderValue = 2;

    private const int WireVarint = 0;
    private const int WireLengthDelimited = 2;

    public static byte[] Marshal(Frame f)
    {
        // 预估容量：固定部分约 12 字节，加上 header / payload 长度
        var payloadLen = f.Payload?.Length ?? 0;
        var encLen = EncodingUtf8ByteCount(f.PayloadEncoding);
        var typeLen = EncodingUtf8ByteCount(f.PayloadType);
        var logIdLen = EncodingUtf8ByteCount(f.LogIdNew);

        var capacity = 32 + payloadLen + encLen + typeLen + logIdLen;
        foreach (var kv in f.Headers)
        {
            capacity += 12 + EncodingUtf8ByteCount(kv.Key) + EncodingUtf8ByteCount(kv.Value);
        }
        var buffer = new byte[capacity];
        int pos = 0;

        WriteVarintField(ref buffer, ref pos, TagSeqId, f.SeqId);
        WriteVarintField(ref buffer, ref pos, TagLogId, f.LogId);
        WriteVarintFieldS32(ref buffer, ref pos, TagService, f.Service);
        WriteVarintFieldS32(ref buffer, ref pos, TagMethod, f.Method);

        if (f.Headers.Count > 0)
        {
            foreach (var kv in f.Headers)
            {
                // 写外层 tag
                EnsureCapacity(ref buffer, ref pos, 16);
                WriteVarint(ref buffer, ref pos, (TagHeaders << 3) | WireLengthDelimited);
                // 写 Header 子消息长度（先占位，再回填）
                int lenPos = pos;
                WriteVarint(ref buffer, ref pos, 0);
                int contentStart = pos;
                WriteStringField(ref buffer, ref pos, TagHeaderKey, kv.Key);
                WriteStringField(ref buffer, ref pos, TagHeaderValue, kv.Value);
                int contentLen = pos - contentStart;
                // 回填长度
                WriteVarintAt(ref buffer, lenPos, (ulong)contentLen);
            }
        }

        if (f.PayloadEncoding.Length > 0)
            WriteStringField(ref buffer, ref pos, TagPayloadEncoding, f.PayloadEncoding);
        if (f.PayloadType.Length > 0)
            WriteStringField(ref buffer, ref pos, TagPayloadType, f.PayloadType);
        if (payloadLen > 0)
            WriteBytesField(ref buffer, ref pos, TagPayload, f.Payload);
        if (f.LogIdNew.Length > 0)
            WriteStringField(ref buffer, ref pos, TagLogIdNew, f.LogIdNew);

        return buffer.AsSpan(0, pos).ToArray();
    }

    public static Frame Unmarshal(ReadOnlySpan<byte> data)
    {
        var f = new Frame();
        int pos = 0;
        while (pos < data.Length)
        {
            ulong tag = ReadVarint(data, ref pos);
            int fieldNumber = (int)(tag >> 3);
            int wireType = (int)(tag & 0x7);
            switch (fieldNumber)
            {
                case TagSeqId:
                    f.SeqId = ReadVarint(data, ref pos);
                    break;
                case TagLogId:
                    f.LogId = ReadVarint(data, ref pos);
                    break;
                case TagService:
                    f.Service = (int)ReadVarint(data, ref pos);
                    break;
                case TagMethod:
                    f.Method = (int)ReadVarint(data, ref pos);
                    break;
                case TagHeaders:
                    {
                        if (wireType != WireLengthDelimited)
                            throw new InvalidDataException($"field {fieldNumber} expects length-delimited, got {wireType}");
                        int headerLen = (int)ReadVarint(data, ref pos);
                        int headerEnd = pos + headerLen;
                        if (headerEnd > data.Length)
                            throw new InvalidDataException("header length exceeds buffer");
                        string key = string.Empty;
                        string value = string.Empty;
                        while (pos < headerEnd)
                        {
                            ulong subTag = ReadVarint(data, ref pos);
                            int subField = (int)(subTag >> 3);
                            int subWire = (int)(subTag & 0x7);
                            switch (subField)
                            {
                                case TagHeaderKey:
                                    key = ReadString(data, ref pos, subWire);
                                    break;
                                case TagHeaderValue:
                                    value = ReadString(data, ref pos, subWire);
                                    break;
                                default:
                                    SkipField(data, ref pos, subWire);
                                    break;
                            }
                        }
                        pos = headerEnd;
                        f.Headers.Add(key, value);
                        break;
                    }
                case TagPayloadEncoding:
                    f.PayloadEncoding = ReadString(data, ref pos, wireType);
                    break;
                case TagPayloadType:
                    f.PayloadType = ReadString(data, ref pos, wireType);
                    break;
                case TagPayload:
                    f.Payload = ReadBytes(data, ref pos, wireType);
                    break;
                case TagLogIdNew:
                    f.LogIdNew = ReadString(data, ref pos, wireType);
                    break;
                default:
                    SkipField(data, ref pos, wireType);
                    break;
            }
        }
        return f;
    }

    // ---------- helpers ----------

    private static void WriteVarintField(ref byte[] buf, ref int pos, int field, ulong value)
    {
        EnsureCapacity(ref buf, ref pos, 16);
        WriteVarint(ref buf, ref pos, ((ulong)field << 3) | WireVarint);
        WriteVarint(ref buf, ref pos, value);
    }

    private static void WriteVarintFieldS32(ref byte[] buf, ref int pos, int field, int value)
    {
        // int32 用普通 varint 编码（与 sint32 的 zigzag 不同）。
        // Go gogoproto 默认也是用普通 varint 编码 int32。
        WriteVarintField(ref buf, ref pos, field, (ulong)value);
    }

    private static void WriteStringField(ref byte[] buf, ref int pos, int field, string s)
    {
        if (s.Length == 0) return;
        var bytes = System.Text.Encoding.UTF8.GetBytes(s);
        EnsureCapacity(ref buf, ref pos, 16 + bytes.Length);
        WriteVarint(ref buf, ref pos, ((ulong)field << 3) | WireLengthDelimited);
        WriteVarint(ref buf, ref pos, (ulong)bytes.Length);
        bytes.CopyTo(buf, pos);
        pos += bytes.Length;
    }

    private static void WriteBytesField(ref byte[] buf, ref int pos, int field, byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0) return;
        EnsureCapacity(ref buf, ref pos, 16 + bytes.Length);
        WriteVarint(ref buf, ref pos, ((ulong)field << 3) | WireLengthDelimited);
        WriteVarint(ref buf, ref pos, (ulong)bytes.Length);
        bytes.CopyTo(buf, pos);
        pos += bytes.Length;
    }

    private static void EnsureCapacity(ref byte[] buf, ref int pos, int need)
    {
        if (pos + need <= buf.Length) return;
        int newSize = Math.Max(buf.Length * 2, pos + need);
        Array.Resize(ref buf, newSize);
    }

    private static int EncodingUtf8ByteCount(string s) => string.IsNullOrEmpty(s) ? 0 : System.Text.Encoding.UTF8.GetByteCount(s);

    // ---- varint ----
    private static void WriteVarint(ref byte[] buf, ref int pos, ulong value)
    {
        while (value >= 0x80)
        {
            buf[pos++] = (byte)(value | 0x80);
            value >>= 7;
        }
        buf[pos++] = (byte)value;
    }

    private static void WriteVarintAt(ref byte[] buf, int pos, ulong value)
    {
        int p = pos;
        while (value >= 0x80)
        {
            buf[p++] = (byte)(value | 0x80);
            value >>= 7;
        }
        buf[p++] = (byte)value;
    }

    private static ulong ReadVarint(ReadOnlySpan<byte> data, ref int pos)
    {
        ulong result = 0;
        int shift = 0;
        while (true)
        {
            if (pos >= data.Length)
                throw new InvalidDataException("varint truncated");
            byte b = data[pos++];
            result |= ((ulong)(b & 0x7F)) << shift;
            if ((b & 0x80) == 0) return result;
            shift += 7;
            if (shift > 63)
                throw new InvalidDataException("varint too long");
        }
    }

    private static string ReadString(ReadOnlySpan<byte> data, ref int pos, int wireType)
    {
        var bytes = ReadBytes(data, ref pos, wireType);
        return System.Text.Encoding.UTF8.GetString(bytes);
    }

    private static byte[] ReadBytes(ReadOnlySpan<byte> data, ref int pos, int wireType)
    {
        if (wireType != WireLengthDelimited)
            throw new InvalidDataException($"expects length-delimited, got {wireType}");
        int len = (int)ReadVarint(data, ref pos);
        if (len < 0 || pos + len > data.Length)
            throw new InvalidDataException("length-delimited exceeds buffer");
        var slice = data.Slice(pos, len);
        pos += len;
        return slice.ToArray();
    }

    private static void SkipField(ReadOnlySpan<byte> data, ref int pos, int wireType)
    {
        switch (wireType)
        {
            case WireVarint:
                ReadVarint(data, ref pos);
                break;
            case WireLengthDelimited:
                int len = (int)ReadVarint(data, ref pos);
                pos += len;
                break;
            case 1: // 64-bit
                pos += 8;
                break;
            case 5: // 32-bit
                pos += 4;
                break;
            default:
                throw new InvalidDataException($"unknown wire type {wireType}");
        }
    }
}
