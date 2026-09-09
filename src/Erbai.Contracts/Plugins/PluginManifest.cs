namespace Erbai.Contracts.Plugins;

/// <summary>
/// 插件元数据（docs/07 §2 的 config.ini 解析结果；属于契约面，第三方插件据此自读配置）。
/// 字段面忠实 FufuLauncher：[General] 固定键 Name/Description/Developer/File/Version
/// + 可选 Enabled（补充标记），其余 section 为自定义配置项。
/// </summary>
public sealed record PluginManifest
{
    /// <summary>插件子目录名（Plugins/ 下的目录名，宿主定位用；非 config.ini 字段）。</summary>
    public required string DirectoryKey { get; init; }

    public required string Name { get; init; }

    public string? Description { get; init; }

    public string? Developer { get; init; }

    /// <summary>相对于插件目录的 DLL 文件名（如 "FPS.dll"）。</summary>
    public required string File { get; init; }

    public required Version Version { get; init; }

    /// <summary>
    /// 声明依赖的契约版本（config.ini 可选键 ContractVersion，格式 "1.0.0"）。
    /// 缺省 = 未声明，宿主按兼容处理（不校验）；声明后主版本必须与宿主一致且不得高于宿主。
    /// </summary>
    public Version? ContractVersion { get; init; }

    /// <summary>
    /// 启停标记：config.ini [General] Enabled=false 为禁用；缺省 true。
    /// 另有实物标记：<see cref="File"/> 对应文件不存在但存在 "{File}.disabled" 变体时同样视为禁用
    /// （FufuLauncher 的 DLL 改扩展名形态，见 docs/07 §2）。
    /// </summary>
    public bool Enabled { get; init; } = true;

    public IReadOnlyList<PluginConfigItem> Config { get; init; } = [];
}