using Erbai.Live.Bilibili.Login;
using Erbai.Live.Bilibili.Protocol;

namespace Erbai.Live.Bilibili.Tests;

/// <summary>扫码登录 + cookie 自动刷新链路（本地假 HTTP，不打真网）。</summary>
public class BilibiliLoginServiceTests
{
    private static string TempCredentialsPath() =>
        Path.Combine(AppContext.BaseDirectory, "tmp", $"login-{Guid.NewGuid():N}", "bilibili_credentials.json");

    private static (FakeLoginHttpHandler Http, BilibiliApiClient Api, BilibiliLoginService Service, string Path) Create(
        TimeSpan? pollInterval = null, int? maxPolls = null)
    {
        var http = new FakeLoginHttpHandler();
        var api = new BilibiliApiClient(http);
        var path = TempCredentialsPath();
        var service = new BilibiliLoginService(api, new BilibiliCredentials(path), pollInterval: pollInterval, maxPolls: maxPolls);
        return (http, api, service, path);
    }

    [Fact]
    public async Task QrLogin_FullFlow_PollsAndSavesCredentials()
    {
        var (http, api, service, path) = Create(pollInterval: TimeSpan.FromMilliseconds(1));
        http.PollCodes.Enqueue(86101);
        http.PollCodes.Enqueue(86090);
        http.PollCodes.Enqueue(0);

        var states = new List<string>();
        var result = await service.QrLoginAsync(s => states.Add(s.Status), CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("sd123", result!.Cookies.Sessdata);
        Assert.Equal("jc123", result.Cookies.BiliJct);
        Assert.Equal("RT_QR", result.Cookies.RefreshToken);
        Assert.Equal("5050", result.LiveRoomId);
        Assert.Equal("测试账号", result.AccountName);

        // 状态序列：等待 → 已扫码 → 成功
        Assert.Contains(QrLoginState.StatusWaiting, states);
        Assert.Contains(QrLoginState.StatusScanned, states);
        Assert.Contains(QrLoginState.StatusSuccess, states);
        Assert.True(http.PollCalls >= 3, $"应至少轮询 3 次，实际 {http.PollCalls}");

        // 凭据已落盘且可读回
        Assert.True(File.Exists(path));
        var saved = await new BilibiliCredentials(path).ReadAsync();
        Assert.NotNull(saved);
        Assert.Equal("sd123", saved!.Sessdata);
        Assert.Equal("RT_QR", saved.RefreshToken);
    }

    [Fact]
    public async Task QrLogin_Expired_ReturnsNull()
    {
        var (http, api, service, path) = Create(pollInterval: TimeSpan.FromMilliseconds(1));
        http.PollCodes.Enqueue(86038);

        var states = new List<string>();
        var result = await service.QrLoginAsync(s => states.Add(s.Status), CancellationToken.None);

        Assert.Null(result);
        Assert.Contains(QrLoginState.StatusExpired, states);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task QrLogin_Timeout_ReturnsNull()
    {
        var (http, api, service, path) = Create(pollInterval: TimeSpan.FromMilliseconds(1), maxPolls: 3);

        var states = new List<string>();
        var result = await service.QrLoginAsync(s => states.Add(s.Status), CancellationToken.None);

        Assert.Null(result);
        Assert.Contains(QrLoginState.StatusTimeout, states);
        Assert.Equal(3, http.PollCalls);
    }

    [Fact]
    public async Task QrLogin_Cancelled_RaisesAndReports()
    {
        var (http, api, service, path) = Create(pollInterval: TimeSpan.FromMilliseconds(50));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(120));

        var states = new List<string>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.QrLoginAsync(s => states.Add(s.Status), cts.Token));

        Assert.Contains(QrLoginState.StatusCancelled, states);
    }

    [Fact]
    public async Task EnsureLogin_ValidSavedCookie_NoRefresh_NoQr()
    {
        var (http, api, service, path) = Create();
        await new BilibiliCredentials(path).WriteAsync(new BilibiliCookies
        {
            Sessdata = "sd_saved",
            BiliJct = "jc_saved",
            DedeUserId = "10086",
            RefreshToken = "RT_saved",
        });

        var result = await service.EnsureLoginAsync(interactive: true, ct: CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("sd_saved", result!.Cookies.Sessdata);
        Assert.Equal("5050", result.LiveRoomId);
        Assert.True(http.PollCalls == 0, "凭据有效时不应走扫码");
        Assert.True(http.RefreshCalls == 0, "不需刷新时不应调 refresh");
    }

    [Fact]
    public async Task EnsureLogin_NeedRefresh_AutoRefreshesAndSaves()
    {
        var (http, api, service, path) = Create();
        http.NeedRefresh = true;
        await new BilibiliCredentials(path).WriteAsync(new BilibiliCookies
        {
            Sessdata = "sd_old",
            BiliJct = "jc_old",
            DedeUserId = "10086",
            RefreshToken = "RT_old",
        });

        var result = await service.EnsureLoginAsync(interactive: true, ct: CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("sd_new", result!.Cookies.Sessdata);
        Assert.Equal("RT_NEW", result.Cookies.RefreshToken);
        Assert.True(http.RefreshCalls == 1, "应调 refresh 一次");
        Assert.True(http.ConfirmCalls == 1, "刷新成功应 confirm");
        Assert.True(http.PollCalls == 0, "刷新成功不应走扫码");

        // 新凭据已持久化
        var saved = await new BilibiliCredentials(path).ReadAsync();
        Assert.Equal("sd_new", saved!.Sessdata);
    }

    [Fact]
    public async Task EnsureLogin_InvalidCookie_RefreshFails_FallsBackToQr()
    {
        var (http, api, service, path) = Create(pollInterval: TimeSpan.FromMilliseconds(1));
        http.NavLoggedIn = false;
        http.RefreshSucceeds = false;
        http.PollCodes.Enqueue(0);
        await new BilibiliCredentials(path).WriteAsync(new BilibiliCookies
        {
            Sessdata = "sd_dead",
            BiliJct = "jc_dead",
            DedeUserId = "10086",
            RefreshToken = "RT_dead",
        });

        var result = await service.EnsureLoginAsync(interactive: true, ct: CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("sd123", result!.Cookies.Sessdata);
        Assert.True(http.RefreshCalls == 1, "刷新失败前应调 refresh 一次");
        Assert.True(http.PollCalls >= 1, "刷新失败后应落到扫码登录");
    }

    [Fact]
    public async Task EnsureLogin_NoSavedCredentials_NonInteractive_ReturnsNull()
    {
        var (http, api, service, path) = Create();

        var result = await service.EnsureLoginAsync(interactive: false, ct: CancellationToken.None);

        Assert.Null(result);
        Assert.Equal(0, http.PollCalls);
    }

    [Fact]
    public async Task RefreshCookie_FormBody_ContainsCorrespondPath()
    {
        var (http, api, service, path) = Create();

        await service.RefreshCookieAsync(new BilibiliCookies
        {
            Sessdata = "sd",
            BiliJct = "csrf_token",
            RefreshToken = "RT",
        }, CancellationToken.None);

        var body = http.LastRefreshBody;
        Assert.NotNull(body);
        Assert.Contains("csrf=csrf_token", body, StringComparison.Ordinal);
        Assert.Contains("refresh_csrf=", body, StringComparison.Ordinal);
        Assert.Contains("source=main_web", body, StringComparison.Ordinal);
        Assert.Contains("refresh_token=RT", body, StringComparison.Ordinal);
    }

    [Fact]
    public void GetCorrespondPath_IsDeterministicMd5()
    {
        var a = BilibiliLoginService.GetCorrespondPath(1700000000000);
        var b = BilibiliLoginService.GetCorrespondPath(1700000000000);

        Assert.Equal(32, a.Length);
        Assert.Equal(a, b);
        Assert.All(a, c => Assert.True(Uri.IsHexDigit(c)));
    }

    [Fact]
    public async Task Logout_DeletesCredentialsAndClearsState()
    {
        var (http, api, service, path) = Create();
        await new BilibiliCredentials(path).WriteAsync(new BilibiliCookies
        {
            Sessdata = "sd",
            BiliJct = "jc",
            DedeUserId = "10086",
            RefreshToken = "RT",
        });
        Assert.True(File.Exists(path));

        service.Logout();

        Assert.False(File.Exists(path));
        Assert.Null(service.CurrentCredentials);
    }
}
