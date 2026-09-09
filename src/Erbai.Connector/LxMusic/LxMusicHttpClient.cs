using System.Text.Json;

namespace Erbai.Connector.LxMusic;

/// <summary>
/// LX Music 内置 HTTP 服务客户端（官方 OpenAPI，默认 http://127.0.0.1:23330，
/// LXMusicHttpClient。任何异常返回 null/False 供降级；
/// 控制端点全部 GET，200 即成功。留 HttpMessageHandler 注入点（自动化测试
/// 走 fake 不打真网）。
/// </summary>
public sealed class LxMusicHttpClient
{
    private readonly string _baseUrl;
    private readonly HttpClient _http;

    public LxMusicHttpClient(string baseUrl = "http://127.0.0.1:23330", HttpMessageHandler? handler = null,
        TimeSpan? timeout = null)
    {
        _baseUrl = (baseUrl ?? "").TrimEnd('/');
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = timeout ?? TimeSpan.FromSeconds(3);
    }

    public bool Enabled => _baseUrl.Length > 0;

    /// <summary>GET /status（filter 只取状态机需要的字段）。</summary>
    public async Task<JsonElement?> GetPlayStateAsync(CancellationToken ct)
    {
        var body = await GetAsync("/status?filter=status,name,singer,duration,progress", ct);
        if (body is null)
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>GET /lyric（当前歌曲 LRC 纯文本）。</summary>
    public async Task<string?> GetLyricAsync(CancellationToken ct) => await GetAsync("/lyric", ct);

    /// <summary>执行控制动作（/play /pause /skip-next /skip-prev）；200 即成功。</summary>
    public async Task<bool> ControlAsync(string path, CancellationToken ct) =>
        await GetAsync(path, ct) is not null;

    private async Task<string?> GetAsync(string path, CancellationToken ct)
    {
        if (!Enabled)
        {
            return null;
        }

        try
        {
            using var response = await _http.GetAsync($"{_baseUrl}{path}", ct);
            if (response.StatusCode != System.Net.HttpStatusCode.OK)
            {
                return null;
            }

            return await response.Content.ReadAsStringAsync(ct);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
