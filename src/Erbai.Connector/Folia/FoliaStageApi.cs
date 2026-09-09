using System.Net.Http.Json;
using System.Text.Json;
using Erbai.Contracts.Players;

namespace Erbai.Connector.Folia;

/// <summary>Stage HTTP 调用的统一结果（Success = 2xx）。</summary>
public sealed record FoliaHttpResult(bool Success, int StatusCode, string? Error);

/// <summary>
/// Folia Stage 本地 HTTP API 客户端（机制 docs/04 §1.5.1）：
/// POST /stage/player/queue {action:"insert-next", songId}（插队下一首）、
/// POST /stage/player/control {action: previous|pause|play|next}（控制）、
/// POST /stage/player/search {query, limit}（搜索）。
/// 全部带 Authorization: Bearer &lt;token&gt;。留 HttpMessageHandler 注入点（测试走 fake）。
/// </summary>
public sealed class FoliaStageApi
{
    private readonly HttpClient _http;

    public FoliaStageApi(string baseUrl, string token, HttpMessageHandler? handler = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.BaseAddress = new Uri((baseUrl ?? "").TrimEnd('/') + "/");
        _http.Timeout = TimeSpan.FromSeconds(12);
        if (!string.IsNullOrWhiteSpace(token))
        {
            _http.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        }
    }

    /// <summary>插队下一首（InsertNext / PlaySelected 的前置）。</summary>
    public Task<FoliaHttpResult> InsertNextAsync(long songId, CancellationToken ct) =>
        PostAsync("/stage/player/queue", new { action = "insert-next", songId }, ct);

    /// <summary>控制指令：previous / pause / play / next。</summary>
    public Task<FoliaHttpResult> ControlAsync(string action, CancellationToken ct) =>
        PostAsync("/stage/player/control", new { action }, ct);

    /// <summary>Stage 搜索，返回裸 track 数组（失败/无法解析时返回空数组）。</summary>
    public async Task<IReadOnlyList<PlayerTrack>> SearchAsync(string query, int limit, CancellationToken ct)
    {
        try
        {
            var (result, body) = await PostJsonAsync("/stage/player/search", new { query, limit }, ct);
            if (!result.Success || body is null)
            {
                return [];
            }

            var songs = FoliaStageJson.FindSongs(body.Value);
            return songs is null
                ? []
                : songs.Value.EnumerateArray()
                    .Select(FoliaStageJson.ParseTrack)
                    .Where(t => t is not null)
                    .Cast<PlayerTrack>()
                    .ToArray();
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            return [];
        }
    }

    private Task<FoliaHttpResult> PostAsync(string route, object payload, CancellationToken ct) =>
        PostJsonAsync(route, payload, ct).ContinueWith(
            t => t.Result.Result,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    private async Task<(FoliaHttpResult Result, JsonElement? Body)> PostJsonAsync(
        string route,
        object payload,
        CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, route)
            {
                Content = JsonContent.Create(payload),
            };
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            var result = new FoliaHttpResult(response.IsSuccessStatusCode, (int)response.StatusCode, null);
            if (!response.IsSuccessStatusCode)
            {
                return (result, null);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            return (result, doc.RootElement.Clone());
        }
        catch (HttpRequestException ex)
        {
            return (new FoliaHttpResult(false, 0, ex.Message), null);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return (new FoliaHttpResult(false, 0, "timeout"), null);
        }
    }
}
