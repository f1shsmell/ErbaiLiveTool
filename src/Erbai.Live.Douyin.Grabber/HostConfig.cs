using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BarrageGrab
{
    /// <summary>
    /// 宿主下发配置（阶段 4 定稿，docs/04 §3.1「启动参数 + JSON 配置文件」）。
    /// 取代旧版"改 .exe.config XML appSettings"：宿主把白名单 19 键序列化为平铺
    /// JSON（键名与 Erbai.Contracts.GrabberAppSettings 一致）经 -config 参数下发。
    /// 校验规则：
    /// - 未知键报错（防宿主/抓包器键名分叉）；
    /// - 布尔键必须 bool、整数键必须 int；
    /// - 端口键（proxyPort/wsListenPort）限 1-65535，其余整数限 1000-60000；
    /// - pushFilter 强制保留 Type=7 粉丝团消息（点歌核心，任何配置不得移除）。
    /// disableLivePageScriptCache 在白名单内但本迁移版无对应行为（上游字段未使用），
    /// 接受并忽略。
    /// </summary>
    internal static class HostConfig
    {
        private static readonly HashSet<string> BoolKeys = new(StringComparer.Ordinal)
        {
            "hideConsole", "printBarrage", "sysProxy", "forcePolling", "autoPause",
            "filterHostName", "listenAny", "disableLivePageScriptCache",
            "barrageFileLog", "showWindow",
        };

        private static readonly HashSet<string> IntKeys = new(StringComparer.Ordinal)
        {
            "proxyPort", "pollingInterval", "wsListenPort",
        };

        private static readonly HashSet<string> PortKeys = new(StringComparer.Ordinal)
        {
            "proxyPort", "wsListenPort",
        };

        /// <summary>从宿主下发的 JSON 文件读取配置并应用到 AppSetting（未知键/类型错抛异常）。</summary>
        public static void ApplyFromFile(AppSetting setting, string path)
        {
            if (path == null) throw new ArgumentNullException(nameof(path));
            if (!File.Exists(path)) throw new FileNotFoundException($"宿主配置文件不存在: {path}", path);
            JObject root;
            try
            {
                root = JObject.Parse(File.ReadAllText(path));
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                throw new FormatException($"宿主配置文件解析失败: {path}（{ex.Message}）");
            }

            Apply(setting, root);
        }

        /// <summary>应用平铺键值（核心逻辑；供测试直接调用）。</summary>
        public static void Apply(AppSetting setting, JObject values)
        {
            var unknown = values.Properties()
                .Select(p => p.Name)
                .Where(name => !BoolKeys.Contains(name) && !IntKeys.Contains(name) &&
                               !StringKeys.Contains(name))
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToList();
            if (unknown.Count > 0)
            {
                throw new ArgumentException($"未知配置键: {string.Join(", ", unknown)}（白名单 19 键）");
            }

            foreach (var prop in values.Properties())
            {
                var name = prop.Name;
                var token = prop.Value;
                if (BoolKeys.Contains(name))
                {
                    if (token.Type != JTokenType.Boolean)
                    {
                        throw new ArgumentException($"配置键 {name} 必须是布尔值");
                    }

                    var value = token.Value<bool>();
                    switch (name)
                    {
                        case "hideConsole": setting.HideConsole = value; break;
                        case "printBarrage": setting.PrintBarrage = value; break;
                        case "sysProxy": setting.UsedProxy = value; break;
                        case "forcePolling": setting.ForcePolling = value; break;
                        case "autoPause": setting.AutoPause = value; break;
                        case "filterHostName": setting.FilterHostName = value; break;
                        case "listenAny": setting.ListenAny = value; break;
                        case "barrageFileLog": setting.BarrageLog = value; break;
                        case "showWindow": setting.ShowWindow = value; break;
                        // disableLivePageScriptCache：接受并忽略（见类注释）
                        default: break;
                    }

                    continue;
                }

                if (IntKeys.Contains(name))
                {
                    if (token.Type != JTokenType.Integer)
                    {
                        throw new ArgumentException($"配置键 {name} 必须是整数");
                    }

                    var value = token.Value<int>();
                    var lower = PortKeys.Contains(name) ? 1 : 1000;
                    var upper = PortKeys.Contains(name) ? 65535 : 60000;
                    if (value < lower || value > upper)
                    {
                        throw new ArgumentException($"配置键 {name} 必须在 {lower}-{upper} 之间");
                    }

                    switch (name)
                    {
                        case "proxyPort": setting.ProxyPort = value; break;
                        case "wsListenPort": setting.WsProt = value; break;
                        case "pollingInterval": setting.PollingInterval = value; break;
                    }

                    continue;
                }

                // 字符串键
                var text = token.Type == JTokenType.Null ? "" : token.Value<string>() ?? "";
                switch (name)
                {
                    case "printFilter": setting.PrintFilter = ParseIntList(text, name); break;
                    case "pushFilter":
                        // 强制保留 Type=7 粉丝团消息（点歌核心；04 §3.2 铁律）
                        setting.PushFilter = ParseIntList(text, name);
                        if (setting.PushFilter != null && !setting.PushFilter.Contains(7))
                        {
                            setting.PushFilter = setting.PushFilter.Concat(new[] { 7 }).ToArray();
                        }
                        break;
                    case "upstreamProxy": setting.UpstreamProxy = text; break;
                    case "processFilter": setting.ProcessFilter = SplitList(text); break;
                    case "webRoomIds": setting.WebRoomIds = SplitList(text); break;
                    case "hostNameFilter": setting.HostNameFilter = SplitList(text); break;
                }
            }
        }

        private static readonly HashSet<string> StringKeys = new(StringComparer.Ordinal)
        {
            "printFilter", "upstreamProxy", "processFilter",
            "webRoomIds", "hostNameFilter", "pushFilter",
        };

        private static string[] SplitList(string text) =>
            text.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();

        private static int[] ParseIntList(string text, string name)
        {
            var parts = SplitList(text);
            var result = new int[parts.Length];
            for (var i = 0; i < parts.Length; i++)
            {
                if (!int.TryParse(parts[i], out var value))
                {
                    throw new ArgumentException($"配置键 {name} 含非法整数: {parts[i]}");
                }

                result[i] = value;
            }

            return result;
        }
    }
}
