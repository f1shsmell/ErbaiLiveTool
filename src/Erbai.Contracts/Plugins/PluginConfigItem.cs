namespace Erbai.Contracts.Plugins;

/// <summary>
/// 插件自定义配置项（config.ini 中 [General] 之外的每个 section 一个）。
/// 形态对齐 FufuLauncher：section 名 = 键，section 内 Name（显示名）/ Type / Value 三元组。
/// </summary>
/// <param name="Key">配置键（= config.ini 的 section 名，Ordinal）。</param>
/// <param name="Name">显示名（供 UI/文档；可空）。</param>
/// <param name="Type">值类型声明：string | int | float | bool | key（FufuLauncher 面；宿主不校验，原样保留）。</param>
/// <param name="Value">字符串原值（插件自行转换）。</param>
public sealed record PluginConfigItem(string Key, string Name, string Type, string Value);