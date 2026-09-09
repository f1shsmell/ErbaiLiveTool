using Erbai.Connector;
using Erbai.Connector.Folia;
using Erbai.Connector.Kugou;
using Erbai.Connector.LxMusic;
using Erbai.Connector.Netease;
using Erbai.Connector.QQMusic;

// 连接器宿主 exe（决策 #15）：NDJSON-stdio 服务端循环。
// 协议规范见 docs/04 §1.4；五平台后端注册在此（阶段 2 先落 dummy + lxmusic，
// 阶段 6 补齐 folia/netease/kugou/qqmusic）。lxmusic 配置经环境变量 LX_* 注入
// （见 LxMusicOptions）；Folia token 经 BILINCM_FOLIA_TOKEN 注入（见 FoliaOptions）；
// QQ 音乐画像经 profiles/qqmusic/*.json 外置加载（见 QQMusicProfiles）；
// 网易云 bridge DLL 随应用发布（bridge/AwooNcmCefBridge.dll，用户已拍板复用）。
var backends = new Dictionary<string, IConnectorBackend>
{
    ["dummy"] = new DummyConnector(),
    ["lxmusic"] = new LxMusicConnector(LxMusicOptions.FromEnvironment()),
    ["folia"] = new FoliaConnector(FoliaOptions.FromEnvironment()),
    ["kugou"] = new KugouConnector(),
    ["qqmusic"] = new QqMusicConnector(),
    ["netease"] = new NeteaseConnector(),
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
