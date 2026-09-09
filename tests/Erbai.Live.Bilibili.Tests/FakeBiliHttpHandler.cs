using System.Net;
using System.Text;

namespace Erbai.Live.Bilibili.Tests;

/// <summary>
/// B站 HTTP 假服务：buvid3 主页 / get_info / nav(wbi_img) / getDanmuInfo（指向假 WS 服务器）。
/// 可配置 get_info 失败次数（InitError 重试测试）。
/// </summary>
internal sealed class FakeBiliHttpHandler : HttpMessageHandler
{
    public FakeBiliHttpHandler(int danmuWsPort)
    {
        DanmuWsPort = danmuWsPort;
    }

    public int DanmuWsPort { get; }

    public int RoomInfoCalls { get; private set; }

    public int DanmuInfoCalls { get; private set; }

    public int NavCalls { get; private set; }

    /// <summary>get_info 连续失败次数（返回 code=-1），之后恢复成功。</summary>
    public int FailRoomInfoTimes { get; set; }

    public string? LastDanmuInfoQuery { get; private set; }

    public IReadOnlyList<string>? LastDanmuInfoCookie { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.AbsoluteUri;
        if (url.StartsWith("https://www.bilibili.com/", StringComparison.Ordinal))
        {
            var resp = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<!doctype html>") };
            resp.Headers.Add("Set-Cookie", "buvid3=test-buvid-123; Path=/; Domain=.bilibili.com");
            return Task.FromResult(resp);
        }

        if (url.Contains("/room/v1/Room/get_info", StringComparison.Ordinal))
        {
            RoomInfoCalls++;
            if (FailRoomInfoTimes > 0)
            {
                FailRoomInfoTimes--;
                return Json("""{"code":-1,"message":"风控失败"}""");
            }

            return Json("""{"code":0,"data":{"room_id":5050,"uid":17152307,"live_status":1}}""");
        }

        if (url.Contains("/x/web-interface/nav", StringComparison.Ordinal))
        {
            NavCalls++;
            return Json(
                """{"code":0,"data":{"isLogin":false,"wbi_img":{"img_url":"https://i0.hdslb.com/bfs/wbi/7cd084941338484aae1ad9425b84077c.png","sub_url":"https://i0.hdslb.com/bfs/wbi/4932caff0ff746eab6f01bf08b70ac45.png"}}}""");
        }

        if (url.Contains("/xlive/web-room/v1/index/getDanmuInfo", StringComparison.Ordinal))
        {
            DanmuInfoCalls++;
            LastDanmuInfoQuery = request.RequestUri!.Query;
            LastDanmuInfoCookie = request.Headers.TryGetValues("Cookie", out var cookies) ? [.. cookies] : null;
            return Json(
                $"{{\"code\":0,\"data\":{{\"host_list\":[{{\"host\":\"127.0.0.1\",\"wss_port\":{DanmuWsPort}}}],\"token\":\"tok-123\"}}}}");
        }

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    private static Task<HttpResponseMessage> Json(string json) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        });
}
