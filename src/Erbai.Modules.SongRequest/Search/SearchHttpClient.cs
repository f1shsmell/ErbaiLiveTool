using System.Net.Http.Headers;
using System.Text;

namespace Erbai.Modules.SongRequest.Search;

/// <summary>
/// 搜索 HTTP 会话（10s 超时、浏览器 UA；00 简报可测性缝：留
/// HttpMessageHandler 注入点，自动化测试全走 fake handler 不打真网）。
/// </summary>
public sealed class SearchHttpClient : IDisposable
{
    public const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36" +
        " (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36";

    private readonly HttpClient _http;

    public SearchHttpClient(HttpMessageHandler? handler = null, TimeSpan? timeout = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = timeout ?? TimeSpan.FromSeconds(10);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
    }

    /// <summary>GET JSON：返回响应体文本；非 200 返回 null（不算失败，由调用方当无结果）。</summary>
    public async Task<string?> GetJsonAsync(string url, IReadOnlyDictionary<string, string> query,
        IReadOnlyDictionary<string, string> headers, CancellationToken ct)
    {
        var uri = BuildUri(url, query);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        foreach (var (name, value) in headers)
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        using var response = await _http.SendAsync(request, ct);
        if (response.StatusCode != System.Net.HttpStatusCode.OK)
        {
            return null;
        }

        return await response.Content.ReadAsStringAsync(ct);
    }

    /// <summary>POST 表单：返回响应体文本；非 200 返回 null。</summary>
    public async Task<string?> PostFormAsync(string url, IReadOnlyDictionary<string, string> form,
        IReadOnlyDictionary<string, string> headers, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new FormUrlEncodedContent(form),
        };
        foreach (var (name, value) in headers)
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        using var response = await _http.SendAsync(request, ct);
        if (response.StatusCode != System.Net.HttpStatusCode.OK)
        {
            return null;
        }

        return await response.Content.ReadAsStringAsync(ct);
    }

    /// <summary>POST JSON：返回响应体文本；非 200 返回 null。</summary>
    /// <remarks>Content-Type 固定 application/json，body 按 UTF-8 原文发送（不经过
    /// URI 编码/Uri 规范化）——三源搜索传中文 payload（如 QQ musicu.fcg rpc）必须走
    /// 此路径：JsonSerializer 默认 \uXXXX 转义 + new Uri 对百分号编码的改写，都会让
    /// 严格的服务端搜不到结果（docs/00 2026-09 QQ musicu.fcg 编码双坑）。</remarks>
    public async Task<string?> PostJsonAsync(string url, string json,
        IReadOnlyDictionary<string, string> headers, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        foreach (var (name, value) in headers)
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        using var response = await _http.SendAsync(request, ct);
        if (response.StatusCode != System.Net.HttpStatusCode.OK)
        {
            return null;
        }

        return await response.Content.ReadAsStringAsync(ct);
    }

    private static Uri BuildUri(string url, IReadOnlyDictionary<string, string> query)
    {
        if (query.Count == 0)
        {
            return new Uri(url);
        }

        // 直接拼接：UriBuilder 会对已编码的 %XX 二次编码
        var queryString = string.Join("&", query.Select(kv =>
            $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
        return new Uri($"{url}?{queryString}");
    }

    public void Dispose() => _http.Dispose();
}
