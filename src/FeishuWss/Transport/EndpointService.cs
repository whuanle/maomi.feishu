using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FeishuWss.Errors;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FeishuWss.Transport;

/// <summary>
/// HTTP 调用飞书 /callback/ws/endpoint 获取长连接 url + 客户端配置。
/// 镜像 oapi-sdk-go/ws/client_transport.go 的 fetchEndpoint。
/// </summary>
public sealed class EndpointService
{
    public const string GenEndpointUri = "/callback/ws/endpoint";

    // 服务端响应码，参考 oapi-sdk-go/ws/const.go
    public const int Ok = 0;
    public const int SystemBusy = 1;
    public const int Forbidden = 403;
    public const int AuthFailed = 514;
    public const int ExceedConnLimit = 1000040350;
    public const int InternalError = 1000040343;

    private readonly HttpClient _httpClient;
    private readonly string _appId;
    private readonly string _appSecret;
    private readonly IClientAssertionProvider? _assertionProvider;
    private readonly string _domain;
    private readonly IDictionary<string, string>? _extraHeaders;
    private readonly string _userAgent;
    private readonly ILogger _logger;

    public EndpointService(
        HttpClient httpClient,
        string appId,
        string appSecret,
        IClientAssertionProvider? assertionProvider,
        string domain,
        IDictionary<string, string>? extraHeaders,
        string userAgent,
        ILogger? logger = null)
    {
        _httpClient = httpClient;
        _appId = appId;
        _appSecret = appSecret;
        _assertionProvider = assertionProvider;
        _domain = domain.TrimEnd('/');
        _extraHeaders = extraHeaders;
        _userAgent = userAgent;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// 调用飞书 endpoint 接口，返回可用的 WebSocket URL 与服务端下发的 ClientConfig。
    /// </summary>
    public async Task<EndpointResult> FetchAsync(CancellationToken ct)
    {
        if (_assertionProvider is null && string.IsNullOrEmpty(_appSecret))
            throw new WssClientError(-1, "appSecret and clientAssertionProvider cannot both be empty");

        var body = new BootstrapRequest { AppId = _appId };

        string requestUrl = _domain + GenEndpointUri;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (_assertionProvider is not null)
        {
            var aud = ExtractAudFromDomain(_domain);
            var token = await _assertionProvider.RetrieveTokenAsync(aud, ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(token.Value))
                throw new WssClientError(-2, "client assertion token is empty");
            body.ClientAssertion = token.Value;
            body.AppSecret = string.Empty;

            if (token.TargetInfo is not null)
            {
                requestUrl = BuildProxyUrl(token.TargetInfo.TargetService, token.TargetInfo.TargetPrefix, GenEndpointUri);
                headers["X-Target-Service"] = aud;
            }
        }
        else
        {
            body.AppSecret = _appSecret;
        }

        var json = JsonSerializer.Serialize(body, BootstrapJsonContext.Default.BootstrapRequest);
        using var req = new HttpRequestMessage(HttpMethod.Post, requestUrl)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        req.Headers.Add("locale", "zh");
        foreach (var kv in _extraHeaders ?? new Dictionary<string, string>())
        {
            req.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
        }
        foreach (var kv in headers)
        {
            req.Headers.Remove(kv.Key);
            req.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
        }
        req.Headers.UserAgent.ParseAdd(_userAgent);

        _logger.LogDebug("POST {Url}", SafeUrl(requestUrl));
        using var resp = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        var respBody = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);

        if (resp.StatusCode != System.Net.HttpStatusCode.OK)
        {
            string message = "system busy";
            try
            {
                var parsed = JsonSerializer.Deserialize(respBody, BootstrapJsonContext.Default.BootstrapResponse);
                if (parsed is { Msg.Length: > 0 }) message = parsed.Msg;
            }
            catch { /* ignore */ }
            throw new WssServerError((int)resp.StatusCode, message);
        }

        var ok = JsonSerializer.Deserialize(respBody, BootstrapJsonContext.Default.BootstrapResponse);
        if (ok is null)
            throw new WssProtocolException("endpoint response is empty");

        switch (ok.Code)
        {
            case Ok:
                break;
            case SystemBusy:
                throw new WssServerError(ok.Code, "system busy");
            case InternalError:
                throw new WssServerError(ok.Code, ok.Msg);
            default:
                throw new WssClientError(ok.Code, ok.Msg);
        }

        if (ok.Data is null || string.IsNullOrEmpty(ok.Data.Url))
            throw new WssServerError(500, "endpoint is null");

        return new EndpointResult(ok.Data.Url, ok.Data.ClientConfig ?? new WssClientConfig());
    }

    private static string ExtractAudFromDomain(string rawUrl)
    {
        if (!rawUrl.Contains("://"))
            rawUrl = "https://" + rawUrl;
        var uri = new Uri(rawUrl);
        return uri.Host;
    }

    private static string BuildProxyUrl(string targetService, string targetPrefix, string apiPath)
    {
        if (!targetService.Contains("://"))
            targetService = "https://" + targetService;
        return targetService.TrimEnd('/') + (targetPrefix ?? string.Empty) + apiPath;
    }

    private static string SafeUrl(string url)
    {
        // 不打印 query，避免泄漏 device_id / service_id
        var idx = url.IndexOf('?');
        return idx >= 0 ? url[..idx] : url;
    }
}

/// <summary>
/// EndpointService 的返回结果：可用 WebSocket URL + 服务端 ClientConfig。
/// </summary>
public sealed record EndpointResult(string Url, WssClientConfig ClientConfig);
