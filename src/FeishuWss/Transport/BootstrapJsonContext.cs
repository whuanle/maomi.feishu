using System.Text.Json.Serialization;

namespace FeishuWss.Transport;

/// <summary>
/// System.Text.Json 源生成上下文。避免在 AOT / 反射禁用环境下运行时反射。
/// </summary>
[JsonSerializable(typeof(BootstrapRequest))]
[JsonSerializable(typeof(BootstrapResponse))]
public partial class BootstrapJsonContext : JsonSerializerContext
{
}
