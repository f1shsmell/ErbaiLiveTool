using System.Net;

namespace Erbai.Modules.SongRequest.Import;

/// <summary>
/// 分享短链解析：手动跟随重定向链（302 Location），每到一跳先用最终 URL 提取
/// 目标 id，再兜底解析响应体（HTML meta refresh / JS location）。带 handler 缝
/// 供单测注入假响应链。
/// </summary>
public static class ShortLinkResolver
{
    private static readonly IReadOnlyCollection<HttpStatusCode> RedirectStatuses =
    [
        HttpStatusCode.MovedPermanently,
        HttpStatusCode.Found,
        HttpStatusCode.TemporaryRedirect,
        HttpStatusCode.PermanentRedirect,
    ];

    /// <summary>
    /// 从 startUrl 出发跟随重定向，extractId 对「每一跳 URL + 每个响应体」依次尝试，
    /// 首个非空结果即为答案；耗尽跳数仍无结果返回 null。
    /// </summary>
    public static async Task<string?> ResolveAsync(
        string startUrl,
        Func<string, string?> extractId,
        HttpMessageHandler? handler = null,
        int maxHops = 5,
        CancellationToken ct = default)
    {
        using var http = handler is null ? new HttpClient() : new HttpClient(handler);
        http.Timeout = TimeSpan.FromSeconds(15);
        http.DefaultRequestHeaders.UserAgent.ParseAdd(Search.SearchHttpClient.UserAgent);

        var current = startUrl;
        for (var hop = 0; hop < maxHops; hop++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            using var response = await http.SendAsync(request, ct);

            var fromUrl = extractId(current);
            if (fromUrl is not null)
            {
                return fromUrl;
            }

            if (RedirectStatuses.Contains(response.StatusCode) && response.Headers.Location is { } location)
            {
                current = location.IsAbsoluteUri
                    ? location.ToString()
                    : new Uri(new Uri(current), location).ToString();
                continue;
            }

            // 终态响应：先试最终 URL（可能已被重定向到带 id 的页面），再读响应体
            using var content = response.Content;
            var body = await content.ReadAsStringAsync(ct);
            return extractId(current) ?? extractId(body);
        }

        return null;
    }
}
