namespace Erbai.Connector.Folia;

/// <summary>
/// Folia 连接器配置（机制 docs/04 §1.5.1）：token 经环境变量
/// BILINCM_FOLIA_TOKEN 注入（与旧语义一致，不落盘），Stage 地址可覆盖（测试）。
/// </summary>
public sealed record FoliaOptions
{
    public string StageUrl { get; init; } = "http://127.0.0.1:32107";

    public string Token { get; init; } = "";

    public string NeteaseApiBase { get; init; } = "https://music.163.com";

    public static FoliaOptions FromEnvironment() => new()
    {
        StageUrl = Environment.GetEnvironmentVariable("FOLIA_STAGE_URL") ?? "http://127.0.0.1:32107",
        Token = Environment.GetEnvironmentVariable("BILINCM_FOLIA_TOKEN")?.Trim() ?? "",
        NeteaseApiBase = Environment.GetEnvironmentVariable("FOLIA_NETEASE_API_BASE") ?? "https://music.163.com",
    };
}
