using System.Buffers.Binary;

namespace FeishuWss.Frames;

/// <summary>
/// 帧结构。对应 oapi-sdk-go/ws/pbbp2.proto 中定义的 Frame 消息。
/// 字段顺序、tag、wire type 都按 protobuf 规范：
///   1:SeqID(u64)         2:LogID(u64)         3:service(i32)        4:method(i32)
///   5:headers(Header[])   6:payload_encoding   7:payload_type        8:payload(bytes)   9:LogIDNew(string)
/// </summary>
public sealed class Frame
{
    public ulong SeqId { get; set; }
    public ulong LogId { get; set; }
    public int Service { get; set; }
    public int Method { get; set; }
    public FrameHeaders Headers { get; } = new();
    public string PayloadEncoding { get; set; } = string.Empty;
    public string PayloadType { get; set; } = string.Empty;
    public byte[] Payload { get; set; } = Array.Empty<byte>();
    public string LogIdNew { get; set; } = string.Empty;

    public FrameType FrameType => (FrameType)Method;

    public string MessageType => Headers.GetString(FrameHeader.Type);
    public string MessageId => Headers.GetString(FrameHeader.MessageId);
    public string TraceId => Headers.GetString(FrameHeader.TraceId);
    public int Sum => Headers.GetInt(FrameHeader.Sum);
    public int Seq => Headers.GetInt(FrameHeader.Seq);

    /// <summary>
    /// 构造一个 Ping 帧。method=0(control)，type=ping，service 取自连接 url 的 service_id。
    /// </summary>
    public static Frame CreatePing(int serviceId)
    {
        var f = new Frame
        {
            Method = (int)FrameType.Control,
            Service = serviceId,
        };
        f.Headers.Add(FrameHeader.Type, WssMessageType.Ping);
        return f;
    }

    public override string ToString() =>
        $"Frame[method={Method},service={Service},type={MessageType},messageId={MessageId},traceId={TraceId},sum={Sum},seq={Seq},payloadLen={Payload?.Length ?? 0}]";
}
