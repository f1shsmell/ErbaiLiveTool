using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Erbai.Contracts.Logging;
using Erbai.Live.Bilibili.Protocol;

namespace Erbai.Live.Bilibili.Login;

/// <summary>扫码登录状态（UI 订阅渲染：waiting 带二维码 URL）。</summary>
public sealed record QrLoginState
{
    public const string StatusWaiting = "waiting";
    public const string StatusScanned = "scanned";
    public const string StatusConfirmed = "confirmed";
    public const string StatusExpired = "expired";
    public const string StatusTimeout = "timeout";
    public const string StatusCancelled = "cancelled";
    public const string StatusError = "error";
    public const string StatusSuccess = "success";

    public required string Status { get; init; }

    public string Message { get; init; } = "";

    /// <summary>waiting 状态时有效：登录二维码内容（UI 生成图片）。</summary>
    public string? QrUrl { get; init; }

    public string? QrCodeKey { get; init; }
}

/// <summary>登录结果：凭据 + 登录账号直播间号（可能为 null）。</summary>
public sealed record BilibiliLoginResult
{
    public required BilibiliCookies Cookies { get; init; }

    public string? LiveRoomId { get; init; }

    public string AccountName { get; init; } = "";
}

/// <summary>
/// B站登录（docs/04 §2.3）：nav 校验 → cookie/info 需刷新则
/// refresh+confirm → 都不行走扫码（generate → 2s×180 轮询）→ DPAPI 凭据持久化。
/// ⚠️ correspond_path 是旧版 MD5 简化实现而非官方 RSA（临时方案）：
/// 原样移植并标记，B站若严格校验该字段刷新会失效，届时需引入 RSA 实现；失败兜底重新扫码。
/// </summary>
public sealed class BilibiliLoginService
{
    private const string QrGenerateUrl = "https://passport.bilibili.com/x/passport-login/web/qrcode/generate";
    private const string QrPollUrl = "https://passport.bilibili.com/x/passport-login/web/qrcode/poll";
    private const string RefreshCookieUrl = "https://passport.bilibili.com/x/passport-login/web/cookie/refresh";
    private const string ConfirmRefreshUrl = "https://passport.bilibili.com/x/passport-login/web/confirm/refresh";
    private const string CookieInfoUrl = "https://passport.bilibili.com/x/passport-login/web/cookie/info";

    private const int PollIntervalMs = 2000;
    private const int MaxPolls = 180;

    private readonly BilibiliApiClient _api;
    private readonly BilibiliCredentials _credentials;
    private readonly ILogBus? _logs;
    private readonly TimeSpan _pollInterval;
    private readonly int _maxPolls;

    public BilibiliLoginService(
        BilibiliApiClient api,
        BilibiliCredentials credentials,
        ILogBus? logs = null,
        TimeSpan? pollInterval = null,
        int? maxPolls = null)
    {
        _api = api;
        _credentials = credentials;
        _logs = logs;
        _pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(PollIntervalMs);
        _maxPolls = maxPolls ?? MaxPolls;
    }

    /// <summary>当前账号（未登录为 null）。</summary>
    public BilibiliCookies? CurrentCredentials { get; private set; }

    /// <summary>凭据文件路径（设置页显示用）。</summary>
    public string CredentialsPath => _credentials.FilePath;

    /// <summary>
    /// 确保已登录主入口（ensure_login 语义）：本地凭据 → nav 校验 → 需刷新则自动刷新
    /// → 仍无效则走扫码。interactive=false 时（测试/无人值守）不扫码直接返回失败。
    /// </summary>
    public async Task<BilibiliLoginResult?> EnsureLoginAsync(
        bool interactive, Action<QrLoginState>? onState = null, CancellationToken ct = default)
    {
        var saved = await _credentials.ReadAsync(ct);
        if (saved is { HasLogin: true })
        {
            _api.ApplyCookies(saved);
            CurrentCredentials = saved;
            _logs?.Information($"[登录] 已从本地加载凭据（{MaskToken(saved.Sessdata)}）");

            var nav = await _api.GetNavInfoAsync(ct);
            if (nav.IsLogin)
            {
                if (await CheckNeedRefreshAsync(ct))
                {
                    _logs?.Information("[登录] Cookie 即将过期，尝试自动刷新");
                    var refreshed = await RefreshAndSaveAsync(saved, ct);
                    if (refreshed is not null)
                    {
                        return refreshed;
                    }

                    // 刷新失败（网络抖动等）不得删除仍有效的凭据（审计 T3-2）：
                    // nav 校验已通过说明 SESSDATA 还能用，删除只会让用户被迫重扫码
                    //（无人值守启动还可能沦为匿名）。保留文件与内存态，下次启动重试；
                    // 确已失效（nav 校验失败）才走下方清除/扫码兜底。
                    _logs?.Warning("[登录] Cookie 刷新失败（保留现存凭据，下次启动重试；失效后走扫码）");
                }
                else
                {
                    return await GetResultWithRoomAsync(saved, nav, ct);
                }
            }
            else
            {
                _logs?.Warning("[登录] Cookie 已失效，尝试用 refresh_token 刷新");
                var refreshed = await RefreshAndSaveAsync(saved, ct);
                if (refreshed is not null)
                {
                    var navAfter = await _api.GetNavInfoAsync(ct);
                    if (navAfter.IsLogin)
                    {
                        return refreshed;
                    }
                }
            }
        }

        if (!interactive)
        {
            return null;
        }

        return await QrLoginAsync(onState, ct);
    }

    /// <summary>完整扫码登录：generate → 展示（onState waiting）→ 2s×180 轮询 → 保存凭据。</summary>
    public async Task<BilibiliLoginResult?> QrLoginAsync(Action<QrLoginState>? onState = null, CancellationToken ct = default)
    {
        // 1. 获取二维码
        JsonElement data;
        try
        {
            var generate = await _api.GetJsonAsync(QrGenerateUrl, ct);
            if (!TryGetData(generate, out data))
            {
                onState?.Invoke(new QrLoginState { Status = QrLoginState.StatusError, Message = $"获取二维码失败：{GetMessage(generate)}" });
                return null;
            }
        }
        catch (Exception ex)
        {
            onState?.Invoke(new QrLoginState { Status = QrLoginState.StatusError, Message = $"获取二维码失败：{ex.Message}" });
            return null;
        }

        var qrUrl = data.GetProperty("url").GetString()!;
        var qrCodeKey = data.GetProperty("qrcode_key").GetString()!;
        onState?.Invoke(new QrLoginState
        {
            Status = QrLoginState.StatusWaiting,
            Message = "请用 B站 App 扫码并在手机上确认",
            QrUrl = qrUrl,
            QrCodeKey = qrCodeKey,
        });
        _logs?.Information("[登录] 请在窗口中扫描二维码完成登录");

        // 2. 轮询（默认 2s × 180 = 3 分钟；测试可注入缩短）
        for (var i = 0; i < _maxPolls; i++)
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                await Task.Delay(_pollInterval, ct);

                var poll = await _api.GetJsonAsync($"{QrPollUrl}?qrcode_key={Uri.EscapeDataString(qrCodeKey)}", ct);
                if (!TryGetData(poll, out var pollData))
                {
                    continue;
                }

                var code = pollData.GetProperty("code").GetInt32();
                switch (code)
                {
                    case 0:
                    {
                        // 登录成功：Set-Cookie + refresh_token
                        var cookies = ExtractLoginCookies(pollData);
                        if (cookies is null)
                        {
                            onState?.Invoke(new QrLoginState { Status = QrLoginState.StatusError, Message = "登录成功但未获取到 SESSDATA" });
                            return null;
                        }

                        await SaveAsync(cookies, ct);
                        onState?.Invoke(new QrLoginState { Status = QrLoginState.StatusSuccess, Message = "登录成功" });
                        return await GetResultWithRoomAsync(cookies, ct);
                    }
                    case 86038:
                        onState?.Invoke(new QrLoginState { Status = QrLoginState.StatusExpired, Message = "二维码已过期，请重新登录" });
                        return null;
                    case 86090:
                        onState?.Invoke(new QrLoginState { Status = QrLoginState.StatusScanned, Message = "已扫码，请在手机上确认" });
                        break;
                    case 86101:
                        // 未扫码：继续等待
                        break;
                    default:
                        onState?.Invoke(new QrLoginState { Status = QrLoginState.StatusError, Message = $"扫码状态码 {code}" });
                        return null;
                }
            }
            catch (OperationCanceledException)
            {
                onState?.Invoke(new QrLoginState { Status = QrLoginState.StatusCancelled, Message = "扫码登录已取消" });
                throw;
            }
            catch (Exception ex)
            {
                onState?.Invoke(new QrLoginState { Status = QrLoginState.StatusError, Message = $"扫码轮询出错：{ex.Message}" });
                return null;
            }
        }

        onState?.Invoke(new QrLoginState { Status = QrLoginState.StatusTimeout, Message = "等待扫码超时，登录已取消" });
        return null;
    }

    /// <summary>退出登录：删除凭据文件 + 清空 Cookie 容器。</summary>
    public void Logout()
    {
        _credentials.Delete();
        _api.CookieContainer.GetCookies(new Uri("https://bilibili.com")).ToList().ForEach(c => c.Expired = true);
        _api.CookieContainer.GetCookies(new Uri("https://passport.bilibili.com")).ToList().ForEach(c => c.Expired = true);
        CurrentCredentials = null;
        _logs?.Information("[登录] 已退出登录");
    }

    /// <summary>cookie/info：data.refresh == true 表示接近过期需刷新。</summary>
    public async Task<bool> CheckNeedRefreshAsync(CancellationToken ct = default)
    {
        try
        {
            var json = await _api.GetJsonAsync(CookieInfoUrl, ct);
            return TryGetData(json, out var data) && data.TryGetProperty("refresh", out var refresh) && refresh.GetBoolean();
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// 用 refresh_token 刷新 cookie（POST csrf + refresh_csrf=correspond_path + source=main_web
    /// + refresh_token；成功后 confirm/refresh 使旧 token 失效）。失败返回 null。
    /// </summary>
    public async Task<BilibiliCookies?> RefreshCookieAsync(BilibiliCookies cookies, CancellationToken ct = default)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var correspondPath = GetCorrespondPath(timestamp); // ⚠️ MD5 简化实现，见类注释

        try
        {
            var (json, setCookies) = await _api.PostFormAsync(RefreshCookieUrl, new Dictionary<string, string>
            {
                ["csrf"] = cookies.BiliJct,
                ["refresh_csrf"] = correspondPath,
                ["source"] = "main_web",
                ["refresh_token"] = cookies.RefreshToken,
            }, ct);

            if (!TryGetData(json, out var data))
            {
                _logs?.Warning($"[登录] 刷新 cookie 失败：{GetMessage(json)}");
                return null;
            }

            var newRefreshToken = data.TryGetProperty("refresh_token", out var rt) ? rt.GetString() ?? "" : "";
            var sessdata = setCookies.FirstOrDefault(c => c.Name == "SESSDATA")?.Value ?? "";
            if (sessdata.Length == 0 || newRefreshToken.Length == 0)
            {
                _logs?.Warning("[登录] 刷新 cookie 响应缺少 SESSDATA/refresh_token");
                return null;
            }

            var refreshed = new BilibiliCookies
            {
                Sessdata = sessdata,
                BiliJct = setCookies.FirstOrDefault(c => c.Name == "bili_jct")?.Value ?? cookies.BiliJct,
                DedeUserId = setCookies.FirstOrDefault(c => c.Name == "DedeUserID")?.Value ?? cookies.DedeUserId,
                RefreshToken = newRefreshToken,
            };

            // 确认刷新：使旧 refresh_token 失效（失败不阻断）
            await ConfirmRefreshAsync(refreshed.BiliJct, cookies.RefreshToken, ct);
            _logs?.Information("[登录] Cookie 刷新成功");
            return refreshed;
        }
        catch (Exception ex)
        {
            _logs?.Warning($"[登录] 刷新 cookie 出错：{ex.Message}");
            return null;
        }
    }

    // ── 内部 ────────────────────────────────────────────────────────────────

    private async Task<BilibiliLoginResult?> RefreshAndSaveAsync(BilibiliCookies saved, CancellationToken ct)
    {
        var refreshed = await RefreshCookieAsync(saved, ct);
        if (refreshed is null)
        {
            return null;
        }

        await SaveAsync(refreshed, ct);
        return await GetResultWithRoomAsync(refreshed, ct);
    }

    private async Task<BilibiliLoginResult> GetResultWithRoomAsync(BilibiliCookies cookies, CancellationToken ct)
    {
        var nav = await _api.GetNavInfoAsync(ct);
        return await GetResultWithRoomAsync(cookies, nav, ct);
    }

    private async Task<BilibiliLoginResult> GetResultWithRoomAsync(BilibiliCookies cookies, BilibiliNavInfo nav, CancellationToken ct)
    {
        var roomId = nav.Uid > 0 ? await _api.GetUserLiveRoomAsync(nav.Uid, ct) : null;
        _logs?.Information($"[登录] 已使用 B站账号：{nav.UName}（uid={nav.Uid}，直播间={roomId ?? "无"}）");
        return new BilibiliLoginResult { Cookies = cookies, LiveRoomId = roomId, AccountName = nav.UName };
    }

    private async Task SaveAsync(BilibiliCookies cookies, CancellationToken ct)
    {
        await _credentials.WriteAsync(cookies, ct);
        _api.ApplyCookies(cookies);
        CurrentCredentials = cookies;
        _logs?.Information("[登录] 凭据已保存（DPAPI 保护）");
    }

    private void ClearAsync()
    {
        _credentials.Delete();
        CurrentCredentials = null;
        _logs?.Warning("[登录] 已清除失效凭据");
    }

    /// <summary>从扫码 poll 成功响应提取登录三件套（优先响应 Set-Cookie，其次容器）。</summary>
    private BilibiliCookies? ExtractLoginCookies(JsonElement pollData)
    {
        var refreshToken = pollData.TryGetProperty("refresh_token", out var rt) ? rt.GetString() ?? "" : "";
        var cookies = _api.Cookies; // Set-Cookie 已由客户端落容器
        if (cookies.Sessdata.Length == 0)
        {
            return null;
        }

        return cookies with { RefreshToken = refreshToken };
    }

    /// <summary>confirm/refresh：使旧 refresh_token 失效（失败静默，对齐旧实现）。</summary>
    private async Task ConfirmRefreshAsync(string csrf, string oldRefreshToken, CancellationToken ct)
    {
        try
        {
            await _api.PostFormAsync(ConfirmRefreshUrl, new Dictionary<string, string>
            {
                ["csrf"] = csrf,
                ["refresh_token"] = oldRefreshToken,
            }, ct);
        }
        catch (Exception ex)
        {
            _logs?.Warning($"[登录] confirm/refresh 失败（忽略）：{ex.Message}");
        }
    }

    /// <summary>⚠️ correspond_path：MD5 简化实现（非官方 RSA）。</summary>
    internal static string GetCorrespondPath(long timestampMs) =>
        Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes($"refresh_{timestampMs}"))).ToLower();

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

    private static string MaskToken(string token) =>
        token.Length <= 8 ? "****" : $"{token[..4]}…{token[^4..]}";
}
