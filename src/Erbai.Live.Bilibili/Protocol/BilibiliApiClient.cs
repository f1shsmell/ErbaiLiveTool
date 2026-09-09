using System.Net;
using System.Text.Json;

namespace Erbai.Live.Bilibili.Protocol;

/// <summary>登录凭据（SESSDATA/bili_jct/DedeUserID；可能为空 = 匿名）。</summary>
public sealed record BilibiliCookies
{
    public string Sessdata { get; init; } = "";
    public string BiliJct { get; init; } = "";
    public string DedeUserId { get; init; } = "";
    public string RefreshToken { get; init; } = "";

    public bool HasLogin => Sessdata.Length > 0;

    /// <summary>拼 WS 握手 Cookie 头（buvid3 + 登录三件套；仅含非空项）。</summary>
    public string ToCookieHeader(string? buvid3 = null)
    {
        var parts = new List<string>(4);
        if (!string.IsNullOrEmpty(buvid3))
        {
            parts.Add($"buvid3={buvid3}");
        }

        if (Sessdata.Length > 0)
        {
            parts.Add($"SESSDATA={Sessdata}");
        }

        if (BiliJct.Length > 0)
        {
            parts.Add($"bili_jct={BiliJct}");
        }

        if (DedeUserId.Length > 0)
        {
            parts.Add($"DedeUserID={DedeUserId}");
        }

        return string.Join("; ", parts);
    }
}

/// <summary>get_info 解析结果（真实房间号 / 主播 uid / 直播状态）。</summary>
public sealed record BilibiliRoomInfo
{
    public required long RealRoomId { get; init; }
    public required long OwnerUid { get; init; }

    /// <summary>0 未开播 / 1 直播中 / 2 轮播。</summary>
    public required int LiveStatus { get; init; }
}

/// <summary>getDanmuInfo 解析结果（弹幕服务器 + token；失败走兜底服务器）。</summary>
public sealed record DanmuServerInfo
{
    public required string Host { get; init; }
    public required int WssPort { get; init; }
    public string? Token { get; init; }

    public static DanmuServerInfo Fallback => new() { Host = "broadcastlv.chat.bilibili.com", WssPort = 443 };
}

/// <summary>nav 接口登录态。</summary>
public sealed record BilibiliNavInfo
{
    public required bool IsLogin { get; init; }
    public string UName { get; init; } = "";
    public long Uid { get; init; }
}

/// <summary>
/// B站直播 HTTP 面（docs/04 §2.2 房间初始化链 + §2.3 登录端点）：
/// buvid3 → get_info → getDanmuInfo(WBI) → nav/直播间查询/扫码轮询等登录端点。
/// Cookie 由本类自管（请求头注入 + 响应 Set-Cookie 落容器），与注入的
/// HttpMessageHandler 无关——测试用 fake handler 即可全链路模拟，不打真网。
/// </summary>
public sealed class BilibiliApiClient : IAsyncDisposable
{
    public const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36";

    private const string HomeUrl = "https://www.bilibili.com/";
    private const string RoomInfoUrl = "https://api.live.bilibili.com/room/v1/Room/get_info";
    private const string DanmuInfoUrl = "https://api.live.bilibili.com/xlive/web-room/v1/index/getDanmuInfo";
    private const string NavUrl = "https://api.bilibili.com/x/web-interface/nav";
    private const string UserLiveRoomUrl = "https://api.live.bilibili.com/live_user/v1/userInfo/getRoomInfoOld";

    private static readonly Uri[] CookieProbeUris =
    [
        new("https://bilibili.com"),
        new("https://passport.bilibili.com"),
        new("https://api.bilibili.com"),
    ];

    private readonly HttpClient _http;
    private readonly CookieContainer _cookies = new();
    private readonly bool _ownHandler;
    private string? _buvid3;

    public BilibiliApiClient(HttpMessageHandler? handler = null)
    {
        _ownHandler = handler is null;
        handler ??= new SocketsHttpHandler { UseCookies = false };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
    }

    /// <summary>当前 buvid3（首次访问主页后非空）。</summary>
    public string? Buvid3 => _buvid3;

    /// <summary>当前 Cookie 容器（供测试断言）。</summary>
    public CookieContainer CookieContainer => _cookies;

    /// <summary>当前登录凭据（从 Cookie 容器提取；匿名时 Sessdata 为空）。</summary>
    public BilibiliCookies Cookies
    {
        get
        {
            var result = new BilibiliCookies();
            foreach (var uri in CookieProbeUris)
            {
                var jar = _cookies.GetCookies(uri);
                if (result.Sessdata.Length == 0)
                {
                    result = result with { Sessdata = jar["SESSDATA"]?.Value ?? "" };
                }

                if (result.BiliJct.Length == 0)
                {
                    result = result with { BiliJct = jar["bili_jct"]?.Value ?? "" };
                }

                if (result.DedeUserId.Length == 0)
                {
                    result = result with { DedeUserId = jar["DedeUserID"]?.Value ?? "" };
                }
            }

            return result;
        }
    }

    /// <summary>注入登录凭据（登录服务调用；之后所有请求自动携带）。</summary>
    public void ApplyCookies(BilibiliCookies cookies)
    {
        var uri = new Uri("https://bilibili.com");
        if (cookies.Sessdata.Length > 0)
        {
            _cookies.Add(uri, new Cookie("SESSDATA", cookies.Sessdata) { Domain = ".bilibili.com" });
        }

        if (cookies.BiliJct.Length > 0)
        {
            _cookies.Add(uri, new Cookie("bili_jct", cookies.BiliJct) { Domain = ".bilibili.com" });
        }

        if (cookies.DedeUserId.Length > 0)
        {
            _cookies.Add(uri, new Cookie("DedeUserID", cookies.DedeUserId) { Domain = ".bilibili.com" });
        }
    }

    /// <summary>访问 bilibili.com 主页拿 buvid3（2025-06 风控下匿名收弹幕必需）。</summary>
    public async Task<string?> EnsureBuvid3Async(CancellationToken ct = default)
    {
        if (_buvid3 is not null)
        {
            return _buvid3;
        }

        using var resp = await SendAsync(new HttpRequestMessage(HttpMethod.Get, HomeUrl), ct);
        resp.EnsureSuccessStatusCode();
        _buvid3 = _cookies.GetCookies(new Uri("https://bilibili.com"))["buvid3"]?.Value;
        return _buvid3;
    }

    /// <summary>房间解析：get_info → 真实房间号 + 主播 uid + 直播状态。</summary>
    public async Task<BilibiliRoomInfo> GetRoomInfoAsync(string inputRoomId, CancellationToken ct = default)
    {
        var json = await GetJsonAsync($"{RoomInfoUrl}?room_id={Uri.EscapeDataString(inputRoomId)}", ct);
        if (!TryGetData(json, out var data))
        {
            throw new BilibiliApiException($"get_info 失败: {GetMessage(json)}");
        }

        return new BilibiliRoomInfo
        {
            RealRoomId = data.TryGetProperty("room_id", out var roomId) ? roomId.GetInt64() : 0,
            OwnerUid = data.TryGetProperty("uid", out var uid) ? uid.GetInt64() : 0,
            LiveStatus = data.TryGetProperty("live_status", out var ls) ? ls.GetInt32() : 0,
        };
    }

    /// <summary>
    /// getDanmuInfo（WBI 签名）：弹幕服务器 host_list 首项 + token。
    /// 失败抛 <see cref="BilibiliApiException"/>，调用方兜底 <see cref="DanmuServerInfo.Fallback"/>。
    /// </summary>
    public async Task<DanmuServerInfo> GetDanmuInfoAsync(long realRoomId, CancellationToken ct = default)
    {
        var nav = await GetJsonAsync(NavUrl, ct);
        JsonElement wbiImg;
        try
        {
            wbiImg = nav.GetProperty("data").GetProperty("wbi_img");
        }
        catch (KeyNotFoundException)
        {
            throw new BilibiliApiException("nav 接口未返回 wbi_img（可能需要重新登录）");
        }

        var rawKey = WbiSigner.ExtractRawKey(
            wbiImg.GetProperty("img_url").GetString()!,
            wbiImg.GetProperty("sub_url").GetString()!);
        var mixinKey = WbiSigner.GetMixinKey(rawKey);

        var query = WbiSigner.SignQuery(new Dictionary<string, string>
        {
            ["id"] = realRoomId.ToString(),
            ["type"] = "0",
            ["web_location"] = "444.8",
        }, mixinKey);

        using var req = new HttpRequestMessage(HttpMethod.Get, $"{DanmuInfoUrl}?{query}");
        req.Headers.Referrer = new Uri($"https://live.bilibili.com/{realRoomId}");
        using var resp = await SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        var json = await JsonSerializer.DeserializeAsync<JsonElement>(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        if (!TryGetData(json, out var data))
        {
            throw new BilibiliApiException($"getDanmuInfo 失败: {GetMessage(json)}");
        }

        var hostList = data.GetProperty("host_list");
        if (hostList.GetArrayLength() == 0)
        {
            throw new BilibiliApiException("getDanmuInfo host_list 为空");
        }

        var first = hostList[0];
        return new DanmuServerInfo
        {
            Host = first.GetProperty("host").GetString()!,
            WssPort = first.TryGetProperty("wss_port", out var port) ? port.GetInt32() : 443,
            Token = data.TryGetProperty("token", out var token) ? token.GetString() : null,
        };
    }

    /// <summary>nav：校验登录态并取账号信息。</summary>
    public async Task<BilibiliNavInfo> GetNavInfoAsync(CancellationToken ct = default)
    {
        var json = await GetJsonAsync(NavUrl, ct);
        if (!TryGetData(json, out var data) || !data.TryGetProperty("isLogin", out var isLogin))
        {
            return new BilibiliNavInfo { IsLogin = false };
        }

        return new BilibiliNavInfo
        {
            IsLogin = isLogin.GetBoolean(),
            UName = data.TryGetProperty("uname", out var uname) ? uname.GetString() ?? "" : "",
            Uid = data.TryGetProperty("mid", out var mid) ? mid.GetInt64() : 0,
        };
    }

    /// <summary>按 uid 查自己的直播间号（登录账号的 roomid，可能为 0/无房间）。</summary>
    public async Task<string?> GetUserLiveRoomAsync(long uid, CancellationToken ct = default)
    {
        var json = await GetJsonAsync($"{UserLiveRoomUrl}?mid={uid}", ct);
        if (!TryGetData(json, out var data) || !data.TryGetProperty("roomid", out var roomId))
        {
            return null;
        }

        var id = roomId.GetInt64();
        return id > 0 ? id.ToString() : null;
    }

    /// <summary>GET JSON（带 UA/Cookie；业务 code!=0 抛 <see cref="BilibiliApiException"/>）。</summary>
    public async Task<JsonElement> GetJsonAsync(string url, CancellationToken ct = default)
    {
        using var resp = await SendAsync(new HttpRequestMessage(HttpMethod.Get, url), ct);
        resp.EnsureSuccessStatusCode();
        return await JsonSerializer.DeserializeAsync<JsonElement>(
            await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
    }

    /// <summary>POST 表单（登录端点用）；返回 (json, 响应 Set-Cookie 列表)。</summary>
    public async Task<(JsonElement Json, IReadOnlyList<Cookie> SetCookies)> PostFormAsync(
        string url, IReadOnlyDictionary<string, string> form, CancellationToken ct = default)
    {
        using var content = new FormUrlEncodedContent(form);
        using var resp = await SendAsync(new HttpRequestMessage(HttpMethod.Post, url) { Content = content }, ct);
        resp.EnsureSuccessStatusCode();
        var json = await JsonSerializer.DeserializeAsync<JsonElement>(
            await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        return (json, SnapshotLoginCookies());
    }

    public ValueTask DisposeAsync()
    {
        _http.Dispose();
        if (_ownHandler)
        {
            // SocketsHttpHandler 由 HttpClient 释放时一并释放
        }

        return ValueTask.CompletedTask;
    }

    // ── Cookie 自管：请求注入 + 响应 Set-Cookie 落容器 ─────────────────────────

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var cookieHeader = _cookies.GetCookieHeader(request.RequestUri!);
        if (!string.IsNullOrEmpty(cookieHeader))
        {
            request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
        }

        var resp = await _http.SendAsync(request, ct);
        if (resp.Headers.TryGetValues("Set-Cookie", out var setCookies))
        {
            foreach (var setCookie in setCookies)
            {
                try
                {
                    _cookies.SetCookies(request.RequestUri!, setCookie);
                }
                catch (CookieException)
                {
                    // 坏 Set-Cookie 头不影响主流程
                }
            }
        }

        return resp;
    }

    private List<Cookie> SnapshotLoginCookies()
    {
        var result = new List<Cookie>();
        foreach (var uri in CookieProbeUris)
        {
            foreach (Cookie cookie in _cookies.GetCookies(uri))
            {
                if (cookie.Name is "SESSDATA" or "bili_jct" or "DedeUserID" && !result.Any(c => c.Name == cookie.Name))
                {
                    result.Add(cookie);
                }
            }
        }

        return result;
    }

    private static bool TryGetData(JsonElement json, out JsonElement data)
    {
        if (json.TryGetProperty("code", out var code) && code.GetInt32() != 0)
        {
            data = default;
            return false;
        }

        return json.TryGetProperty("data", out data);
    }

    private static string GetMessage(JsonElement json) =>
        json.TryGetProperty("message", out var message) ? message.GetString() ?? "未知错误" : "未知错误";
}

/// <summary>B站 HTTP 接口错误（code!=0 / 网络失败 / 解析失败）。</summary>
public sealed class BilibiliApiException : Exception
{
    public BilibiliApiException(string message) : base(message)
    {
    }

    public BilibiliApiException(string message, Exception inner) : base(message, inner)
    {
    }
}
