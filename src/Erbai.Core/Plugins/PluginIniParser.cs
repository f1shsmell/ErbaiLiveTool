using System.Globalization;
using Erbai.Contracts.Plugins;

namespace Erbai.Core.Plugins;

/// <summary>
/// 插件 config.ini 解析器（只读元数据；格式契约见 docs/07 §2，字段面忠实 FufuLauncher）：
/// - 注释行（; 或 # 开头）与空行忽略；键名大小写不敏感（OrdinalIgnoreCase），值 trim；
/// - [General] 固定键：Name/File/Version 必需，Description/Developer/Enabled/ContractVersion 可选；
/// - 其余每个 section = 一个自定义配置项（Key=section 名，内含 Name/Type/Value）；
/// - 未知键宽容忽略（FufuLauncher 的 config.ini 存在 help= 等扩展键）。
/// 解析失败抛 <see cref="FormatException"/>（原因信息面向插件作者，由 PluginLoader 聚合上报）。
/// </summary>
internal static class PluginIniParser
{
    public static PluginManifest Parse(string iniText, string directoryKey)
    {
        var general = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var items = new List<(string Section, Dictionary<string, string> Values)>();
        string currentSection = string.Empty;
        Dictionary<string, string>? currentValues = null;

        foreach (var rawLine in iniText.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line[0] is ';' or '#')
            {
                continue;
            }

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                currentSection = line[1..^1].Trim();
                if (string.Equals(currentSection, "General", StringComparison.OrdinalIgnoreCase))
                {
                    currentValues = null;
                }
                else
                {
                    currentValues = [];
                    items.Add((currentSection, currentValues));
                }

                continue;
            }

            var eq = line.IndexOf('=');
            if (eq <= 0)
            {
                continue; // 无键值对的行宽容忽略
            }

            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();
            if (key.Length == 0)
            {
                continue;
            }

            if (currentValues is null)
            {
                general[key] = value;
            }
            else
            {
                currentValues[key] = value;
            }
        }

        return BuildManifest(general, items, directoryKey);
    }

    private static PluginManifest BuildManifest(
        IReadOnlyDictionary<string, string> general,
        IReadOnlyList<(string Section, Dictionary<string, string> Values)> items,
        string directoryKey)
    {
        var name = Require(general, "Name", directoryKey);
        var file = Require(general, "File", directoryKey);
        if (!IsSafeFileName(file))
        {
            throw new FormatException($"config.ini 的 File 必须是纯文件名（当前值 \"{file}\" 禁止路径分隔符）");
        }

        var versionText = Require(general, "Version", directoryKey);
        if (!System.Version.TryParse(versionText, out var version))
        {
            throw new FormatException($"config.ini 的 Version 不是有效版本号：\"{versionText}\"");
        }

        System.Version? contractVersion = null;
        if (general.TryGetValue("ContractVersion", out var cv) && cv.Length > 0)
        {
            if (!System.Version.TryParse(cv, out contractVersion))
            {
                throw new FormatException($"config.ini 的 ContractVersion 不是有效版本号：\"{cv}\"");
            }
        }

        var enabled = true;
        if (general.TryGetValue("Enabled", out var enabledText) && enabledText.Length > 0)
        {
            if (!TryParseBool(enabledText, out enabled))
            {
                throw new FormatException($"config.ini 的 Enabled 必须为 true/false（1/0/yes/no），当前值：\"{enabledText}\"");
            }
        }

        var configItems = new List<PluginConfigItem>(items.Count);
        foreach (var (section, values) in items)
        {
            values.TryGetValue("Name", out var itemName);
            values.TryGetValue("Type", out var itemType);
            values.TryGetValue("Value", out var itemValue);
            configItems.Add(new PluginConfigItem(section, itemName ?? string.Empty, itemType ?? string.Empty, itemValue ?? string.Empty));
        }

        return new PluginManifest
        {
            DirectoryKey = directoryKey,
            Name = name,
            Description = Get(general, "Description"),
            Developer = Get(general, "Developer"),
            File = file,
            Version = version,
            ContractVersion = contractVersion,
            Enabled = enabled,
            Config = configItems,
        };
    }

    /// <summary>
    /// 改写 config.ini 的 [General] Enabled 键（启停操作，UI 用）：
    /// 有 Enabled 行 → 替换值（保留原缩进）；无 → 在 [General] 段尾插入。
    /// 其余内容（注释/其他键/自定义配置 section）原样保留；换行风格跟随原文件。
    /// </summary>
    public static string SetEnabled(string iniText, bool enabled)
    {
        var value = enabled ? "true" : "false";
        var newline = iniText.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = iniText.Split(["\r\n", "\n"], StringSplitOptions.None);

        var generalStart = -1;
        var generalEnd = lines.Length;
        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']')
                && string.Equals(trimmed[1..^1].Trim(), "General", StringComparison.OrdinalIgnoreCase))
            {
                generalStart = i;
                for (var j = i + 1; j < lines.Length; j++)
                {
                    var t = lines[j].Trim();
                    if (t.StartsWith('[') && t.EndsWith(']'))
                    {
                        generalEnd = j;
                        break;
                    }
                }

                break;
            }
        }

        if (generalStart < 0)
        {
            throw new FormatException("config.ini 缺少 [General] 段，无法改写启停标记");
        }

        for (var i = generalStart + 1; i < generalEnd; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.Length == 0 || trimmed[0] is ';' or '#')
            {
                continue;
            }

            var eq = trimmed.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }

            if (string.Equals(trimmed[..eq].Trim(), "Enabled", StringComparison.OrdinalIgnoreCase))
            {
                var indent = lines[i][..lines[i].IndexOf(lines[i].TrimStart(), StringComparison.Ordinal)];
                lines[i] = indent + "Enabled = " + value;
                return string.Join(newline, lines);
            }
        }

        // 无 Enabled 行 → 在 [General] 段尾（下一个 section 之前）插入
        var enabledLine = "Enabled = " + value;
        if (generalEnd < lines.Length)
        {
            // 避免破坏下一个 section 的独立行
            lines = [.. lines[..generalEnd], enabledLine, .. lines[generalEnd..]];
        }
        else
        {
            lines = [.. lines, enabledLine];
        }

        return string.Join(newline, lines);
    }

    private static string Require(IReadOnlyDictionary<string, string> general, string key, string directoryKey)
    {
        if (!general.TryGetValue(key, out var value) || value.Length == 0)
        {
            throw new FormatException($"config.ini 缺少必需键 [{key}]（插件目录 {directoryKey}）");
        }

        return value;
    }

    private static string? Get(IReadOnlyDictionary<string, string> general, string key) =>
        general.TryGetValue(key, out var value) ? value : null;

    /// <summary>File 必须是纯文件名：禁止目录分隔符 / 绝对路径 / 父目录穿越。</summary>
    private static bool IsSafeFileName(string file) =>
        file.Length > 0
        && file.IndexOfAny(['/', '\\', ':']) < 0
        && file != "."
        && file != ".."
        && !file.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);

    private static bool TryParseBool(string text, out bool value)
    {
        switch (text.Trim().ToLowerInvariant())
        {
            case "1":
            case "true":
            case "yes":
                value = true;
                return true;
            case "0":
            case "false":
            case "no":
                value = false;
                return true;
            default:
                value = false;
                return false;
        }
    }
}