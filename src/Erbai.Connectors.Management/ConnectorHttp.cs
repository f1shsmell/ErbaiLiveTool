using System.Net.Http.Headers;

namespace Erbai.Connectors.Management;

/// <summary>
/// 连接器相关 HTTP 客户端的统一构造：清单获取 / 包下载 / 私有运行时下载都从这里取。
/// </summary>
/// <remarks>
/// <para>
/// <b>必须集中的第一个理由：超时。</b>连接器包约 7 MB、私有 .NET 运行时约 33–48 MB，
/// <see cref="HttpClient"/> 默认的 100 秒在弱网下不够稳（下载器虽然按 2 MB 分块，
/// 但每块仍受客户端超时约束），故统一放宽到 5 分钟。
/// </para>
/// <para>
/// <b>第二个理由：User-Agent 与项目既有约定一致。</b>
/// <c>BilibiliApiClient</c> / <c>SearchHttpClient</c> 都显式声明 UA，
/// 而 <see cref="HttpClient"/> 默认**不发** UA。上游站点在 Cloudflare 之后，
/// 带一个可识别的 UA 是零成本的稳健性投入。
/// </para>
/// <para>
/// <b>一处需要澄清的历史记录（勿据此写结论）：</b>P7 期间曾观察到
/// <c>app.enkianss.us</c> 对无 UA 请求返回 <c>403</c>，并据此把"上游强制要求 UA"
/// 当成故障根因。该现象<b>未能复现</b>——随后 20 次连续无 UA 请求、以及清单与包两个
/// 端点、普通 GET 与 <c>Range</c> 请求，全部返回 200/206。本机出网经 HTTP 代理
/// （<c>http_proxy</c> 指向 127.0.0.1），那批 403 更可能是代理或 Cloudflare 边缘的
/// 瞬时行为。<b>因此 UA 不是本类存在的原因</b>，只是顺带保持一致的约定。
/// </para>
/// <para>
/// 这段"记录一个被推翻的假设"的注释是刻意保留的：真实的坑往往不是"少了个头"，
/// 而是"把一次瞬时故障当成了稳定规律，并写进了代码注释"。
/// </para>
/// </remarks>
public static class ConnectorHttp
{
    /// <summary>
    /// 默认超时。见类型注释：33–48 MB 的运行时归档需要比 <see cref="HttpClient"/>
    /// 默认的 100 秒更宽的余量。
    /// </summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    /// 对外标识。上游不校验具体值；带上产品名便于对方在自己日志里区分流量来源。
    /// </summary>
    /// <remarks>
    /// 刻意保持成最简单的 <c>product/version</c> 形式：<see cref="HttpRequestHeaders.UserAgent"/>
    /// 的 <c>ParseAdd</c> 对带注释（括号）或多余空格的写法比较挑，而这里的目标只是"别是空的"，
    /// 不值得为更花哨的格式引入一个可能抛 <see cref="FormatException"/> 的解析路径。
    /// </remarks>
    public const string UserAgent = "ErbaiLiveTool/0.1.0";

    /// <summary>
    /// 造一个已配置好的 <see cref="HttpClient"/>。
    /// </summary>
    /// <param name="handler">
    /// 测试用的注入点（如假的 <c>HttpMessageHandler</c>）；为 <see langword="null"/> 时用默认 handler。
    /// </param>
    public static HttpClient Create(HttpMessageHandler? handler = null)
    {
        var client = handler is null
            ? new HttpClient()
            : new HttpClient(handler, disposeHandler: false);

        client.Timeout = DefaultTimeout;
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        return client;
    }
}
