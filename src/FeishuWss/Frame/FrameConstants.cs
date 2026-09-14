namespace FeishuWss.Frames;

/// <summary>
/// 飞书长连接帧头部常量。字段名与 oapi-sdk-go/ws/const.go 保持一致。
/// </summary>
public static class FrameHeader
{
    public const string Timestamp = "timestamp";
    public const string Type = "type";
    public const string MessageId = "message_id";
    public const string Sum = "sum";
    public const string Seq = "seq";
    public const string TraceId = "trace_id";
    public const string InstanceId = "instance_id";
    public const string BizRt = "biz_rt";

    public const string HandshakeStatus = "Handshake-Status";
    public const string HandshakeMsg = "Handshake-Msg";
    public const string HandshakeAuthErrCode = "Handshake-Autherrcode";
}

/// <summary>
/// 消息类型。事件 / 卡片回调 / 心跳。
/// </summary>
public static class WssMessageType
{
    public const string Event = "event";
    public const string Card = "card";
    public const string Ping = "ping";
    public const string Pong = "pong";
}

/// <summary>
/// 帧类型：控制帧(0) / 数据帧(1)。
/// </summary>
public enum FrameType : int
{
    Control = 0,
    Data = 1,
}
