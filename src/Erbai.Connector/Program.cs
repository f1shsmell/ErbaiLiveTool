using Erbai.Connector;
using Erbai.Connector.LxMusic;

// 连接器宿主 exe（决策 #15）：NDJSON-stdio 服务端循环，协议规范见 docs/04 §1.4。
//
// 本进程只承载 **lxmusic 原生后端**。网易云 / 酷狗 / QQ 音乐 / Folia 已改为插件形态：
// 由用户按清单安装上游第三方连接器（各自独立的 exe），本进程既不实现也不分发它们。
// 因此这里的后端表不再是"五平台"，而是"原生通道 + 插件通道"里的原生一侧——
// 插件平台的连接器由 Erbai.Player.Connectors 直接拉起各自 exe，不经过本宿主。
//
// lxmusic 配置经环境变量 LX_* 注入（见 LxMusicOptions）；dummy 供测试与黑盒对照。
var backends = new Dictionary<string, IConnectorBackend>
{
    ["dummy"] = new DummyConnector(),
    ["lxmusic"] = new LxMusicConnector(LxMusicOptions.FromEnvironment()),
};

using var input = Console.OpenStandardInput();
using var output = Console.OpenStandardOutput();
var reader = new StreamReader(input, System.Text.Encoding.UTF8);
var writer = new StreamWriter(output, System.Text.Encoding.UTF8)
{
    AutoFlush = true,
};

var host = new ConnectorHost(backends);
try
{
    await host.RunAsync(reader, writer, CancellationToken.None);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"connector host fatal: {ex.Message}");
    return 1;
}

return 0;
