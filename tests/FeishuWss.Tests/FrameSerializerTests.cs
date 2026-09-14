using System.Text;
using FeishuWss.Frames;
using Xunit;

namespace FeishuWss.Tests;

/// <summary>
/// Frame / FrameSerializer 的 round-trip 测试。
/// 这一层是整个长连接正确性的基石：二进制格式必须与 oapi-sdk-go/ws/pbbp2.proto
/// 完全一致（protobuf wire format），否则握手后收到的第一帧就解不开。
/// </summary>
public class FrameSerializerTests
{
    [Fact]
    public void EmptyFrame_RoundTrip()
    {
        var f = new Frame();
        var bytes = FrameSerializer.Marshal(f);
        var back = FrameSerializer.Unmarshal(bytes);

        Assert.Equal(0UL, back.SeqId);
        Assert.Equal(0UL, back.LogId);
        Assert.Equal(0, back.Service);
        Assert.Equal(0, back.Method);
        Assert.Empty(back.Headers);
        Assert.Equal(string.Empty, back.PayloadEncoding);
        Assert.Equal(string.Empty, back.PayloadType);
        Assert.Empty(back.Payload);
        Assert.Equal(string.Empty, back.LogIdNew);
    }

    [Fact]
    public void Frame_AllFields_RoundTrip()
    {
        var f = new Frame
        {
            SeqId = 12345UL,
            LogId = 67890UL,
            Service = 100,
            Method = (int)FrameType.Data,
        };
        f.Headers.Add(FrameHeader.Type, WssMessageType.Event);
        f.Headers.Add(FrameHeader.MessageId, "om_xxx");
        f.Headers.Add(FrameHeader.TraceId, "trace_abc");
        f.Headers.Add(FrameHeader.Sum, "1");
        f.Headers.Add(FrameHeader.Seq, "0");
        f.PayloadEncoding = "json";
        f.PayloadType = "application/json";
        f.Payload = Encoding.UTF8.GetBytes("{\"hello\":\"world\"}");
        f.LogIdNew = "log_id_v2";

        var bytes = FrameSerializer.Marshal(f);
        var back = FrameSerializer.Unmarshal(bytes);

        Assert.Equal(12345UL, back.SeqId);
        Assert.Equal(67890UL, back.LogId);
        Assert.Equal(100, back.Service);
        Assert.Equal((int)FrameType.Data, back.Method);
        Assert.Equal(5, back.Headers.Count);
        Assert.Equal(WssMessageType.Event, back.Headers.GetString(FrameHeader.Type));
        Assert.Equal("om_xxx", back.Headers.GetString(FrameHeader.MessageId));
        Assert.Equal("trace_abc", back.Headers.GetString(FrameHeader.TraceId));
        Assert.Equal(1, back.Headers.GetInt(FrameHeader.Sum));
        Assert.Equal("json", back.PayloadEncoding);
        Assert.Equal("application/json", back.PayloadType);
        Assert.Equal("{\"hello\":\"world\"}", Encoding.UTF8.GetString(back.Payload));
        Assert.Equal("log_id_v2", back.LogIdNew);
    }

    [Fact]
    public void Frame_MultipleHeaders_Preserved()
    {
        var f = new Frame();
        f.Headers.Add(FrameHeader.Type, WssMessageType.Event);
        f.Headers.Add("custom_1", "v1");
        f.Headers.Add("custom_2", "v2");
        f.Headers.Add("custom_3", "v3");

        var back = FrameSerializer.Unmarshal(FrameSerializer.Marshal(f));
        Assert.Equal(4, back.Headers.Count);
        Assert.Equal("v1", back.Headers.GetString("custom_1"));
        Assert.Equal("v2", back.Headers.GetString("custom_2"));
        Assert.Equal("v3", back.Headers.GetString("custom_3"));
    }

    [Fact]
    public void PingFrame_HasCorrectShape()
    {
        var f = Frame.CreatePing(serviceId: 42);
        Assert.Equal((int)FrameType.Control, f.Method);
        Assert.Equal(42, f.Service);
        Assert.Equal(WssMessageType.Ping, f.Headers.GetString(FrameHeader.Type));

        var bytes = FrameSerializer.Marshal(f);
        // 字段顺序：SeqID(1) LogID(2) Service(3) Method(4) Headers(5) ...
        // tag(1, varint) = 0x08, value 0 = 0x00
        // tag(2, varint) = 0x10, value 0 = 0x00
        // tag(3, varint) = 0x18, value 42 = 0x2A
        // tag(4, varint) = 0x20, value 0  = 0x00
        Assert.True(bytes.Length > 10, $"ping frame too small: {bytes.Length}");
        Assert.Equal(0x08, bytes[0]); // tag(SeqID=1, varint)
        Assert.Equal(0x00, bytes[1]); // SeqID value = 0
        Assert.Equal(0x18, bytes[4]); // tag(Service=3, varint)
        Assert.Equal(0x2A, bytes[5]); // Service value = 42
        Assert.Equal(0x20, bytes[6]); // tag(Method=4, varint)
        Assert.Equal(0x00, bytes[7]); // Method value = 0 (control)
    }

    [Fact]
    public void Frame_VarintEncoding_BigNumbers()
    {
        // 2^63 - 1：会占用 9 个字节
        var f = new Frame { SeqId = 0xFFFFFFFFFFFFFFFUL };
        var bytes = FrameSerializer.Marshal(f);
        var back = FrameSerializer.Unmarshal(bytes);
        Assert.Equal(0xFFFFFFFFFFFFFFFUL, back.SeqId);
    }

    [Fact]
    public void Frame_PartialFields_RoundTrip()
    {
        var f = new Frame
        {
            Method = (int)FrameType.Data,
        };
        f.Headers.Add(FrameHeader.Type, WssMessageType.Event);
        f.Payload = Encoding.UTF8.GetBytes("{}");

        var back = FrameSerializer.Unmarshal(FrameSerializer.Marshal(f));
        Assert.Equal((int)FrameType.Data, back.Method);
        Assert.Equal("{}", Encoding.UTF8.GetString(back.Payload));
        Assert.Equal(string.Empty, back.PayloadEncoding);
    }

    [Fact]
    public void Frame_UnknownField_Skipped()
    {
        // 手工构造一个含未知 tag 的字节流，模拟老版本 / 新字段
        // tag 99 (varint) + varint 7
        var bytes = new List<byte>();
        // method=4, value=0
        bytes.Add(0x20); bytes.Add(0x00);
        // unknown field 99, varint, value=7. tag = (99<<3)|0 = 792，需 2 字节 varint
        bytes.Add(0x98); bytes.Add(0x06); bytes.Add(0x07);
        // type header
        var headBytes = new List<byte>();
        headBytes.Add(0x0A); headBytes.Add(0x04); headBytes.AddRange(Encoding.UTF8.GetBytes("type"));
        headBytes.Add(0x12); headBytes.Add(0x04); headBytes.AddRange(Encoding.UTF8.GetBytes("pong"));
        // tag(5, length-delimited) + length
        bytes.Add((byte)((5 << 3) | 2));
        bytes.Add((byte)headBytes.Count);
        bytes.AddRange(headBytes);

        var f = FrameSerializer.Unmarshal(bytes.ToArray());
        Assert.Equal(0, f.Method);
        Assert.Equal(WssMessageType.Pong, f.Headers.GetString(FrameHeader.Type));
    }
}
