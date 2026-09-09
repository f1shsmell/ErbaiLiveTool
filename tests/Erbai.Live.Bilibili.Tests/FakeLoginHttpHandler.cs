using System.Net;
using System.Text;

namespace Erbai.Live.Bilibili.Tests;

/// <summary>登录端点假服务：generate / poll（可编程状态序列）/ nav / cookie/info / refresh / confirm。</summary>
internal sealed class FakeLoginHttpHandler : HttpMessageHandler
{
    public Queue<int> PollCodes { get; } = new();

    public bool NeedRefresh { get; set; }

    public bool RefreshSucceeds { get; set; } = true;

    public int PollCalls { get; private set; }

    public int RefreshCalls { get; private set; }

    public int ConfirmCalls { get; private set; }

    public string? LastRefreshBody { get; private set; }

    public bool NavLoggedIn { get; set; } = true;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.AbsoluteUri;
        if (url.Contains("/qrcode/generate", StringComparison.Ordinal))
        {
            return Task.FromResult(Json("""{"code":0,"data":{"url":"https://passport.bilibili.com/h5-app/passport/login/scan?qrcode_key=KEY123","qrcode_key":"KEY123"}}"""));
        }

        if (url.Contains("/qrcode/poll", StringComparison.Ordinal))
        {
            PollCalls++;
            var code = PollCodes.Count > 0 ? PollCodes.Dequeue() : 86101;
            return Task.FromResult(Poll(code));
        }

        if (url.Contains("/x/web-interface/nav", StringComparison.Ordinal))
        {
            return Task.FromResult(NavLoggedIn
                ? Json("""{"code":0,"data":{"isLogin":true,"uname":"测试账号","mid":10086}}""")
                : Json("""{"code":0,"data":{"isLogin":false}}"""));
        }

        if (url.Contains("/cookie/info", StringComparison.Ordinal))
        {
            return Task.FromResult(Json($"{{\"code\":0,\"data\":{{\"refresh\":{NeedRefresh.ToString().ToLowerInvariant()}}}}}"));
        }

        if (url.Contains("/cookie/refresh", StringComparison.Ordinal))
        {
            RefreshCalls++;
            LastRefreshBody = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
            if (!RefreshSucceeds)
            {
                return Task.FromResult(Json("""{"code":-101,"message":"refresh failed"}"""));
            }

            var resp = Json("""{"code":0,"data":{"refresh_token":"RT_NEW"}}""");
            resp.Headers.TryAddWithoutValidation("Set-Cookie", new[] {
                "SESSDATA=sd_new; Path=/; Domain=.bilibili.com; HttpOnly",
                "bili_jct=jc_new; Path=/; Domain=.bilibili.com; HttpOnly",
                "DedeUserID=10086; Path=/; Domain=.bilibili.com" });
            return Task.FromResult(resp);
        }

        if (url.Contains("/confirm/refresh", StringComparison.Ordinal))
        {
            ConfirmCalls++;
            return Task.FromResult(Json("""{"code":0}"""));
        }

        if (url.Contains("/getRoomInfoOld", StringComparison.Ordinal))
        {
            return Task.FromResult(Json("""{"code":0,"data":{"roomid":5050}}"""));
        }

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    private static HttpResponseMessage Poll(int code)
    {
        var body = code switch
        {
            0 => """{"code":0,"data":{"code":0,"message":"","refresh_token":"RT_QR"}}""",
            86038 => """{"code":0,"data":{"code":86038,"message":"二维码已失效"}}""",
            86090 => """{"code":0,"data":{"code":86090,"message":"已扫码未确认"}}""",
            _ => """{"code":0,"data":{"code":86101,"message":"未扫码"}}""",
        };
        var resp = Json(body);
        if (code == 0)
        {
            resp.Headers.TryAddWithoutValidation("Set-Cookie", new[] {
                "SESSDATA=sd123; Path=/; Domain=.bilibili.com; HttpOnly",
                "bili_jct=jc123; Path=/; Domain=.bilibili.com; HttpOnly",
                "DedeUserID=10086; Path=/; Domain=.bilibili.com" });
        }

        return resp;
    }

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };
}
