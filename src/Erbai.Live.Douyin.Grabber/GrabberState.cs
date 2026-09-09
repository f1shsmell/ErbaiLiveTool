using System;

namespace BarrageGrab
{
    /// <summary>
    /// headless 结构化状态日志（阶段 4 定稿，docs/04 §3.2「状态走 stdout 结构化日志」）。
    /// 宿主（Erbai.Live.Douyin.DouyinGrabberHost）从 stdout 逐行读取，识别
    /// <c>[grabber-state]</c> 前缀行并解析 JSON 状态；其余行按中英文 token 分级。
    /// 状态行只含 ASCII（子进程 stdout 可能被宿主按 GBK 回退解码，避免编码炸）。
    /// </summary>
    public static class GrabberState
    {
        public const string Prefix = "[grabber-state] ";

        /// <summary>WS 服务与代理就绪（端口可连、弹幕可推送）。</summary>
        public static void Ready(int wsPort, int proxyPort, bool sysProxy)
        {
            Emit("ready", $"\"wsPort\":{wsPort},\"proxyPort\":{proxyPort},\"sysProxy\":{(sysProxy ? "true" : "false")},\"pid\":{Environment.ProcessId}");
        }

        /// <summary>房间进入抓取（信息级；同一房间首次出现时输出）。</summary>
        public static void Room(string webRoomId)
        {
            Emit("room", $"\"webRoomId\":{Quote(webRoomId)}");
        }

        /// <summary>致命错误（初始化失败等；进程随后退出）。</summary>
        public static void Error(string message)
        {
            Emit("error", $"\"message\":{Quote(message)}");
        }

        /// <summary>进程正常退出。</summary>
        public static void Exit(int code)
        {
            Emit("exit", $"\"code\":{code}");
        }

        private static void Emit(string name, string payload)
        {
            try
            {
                Console.Out.WriteLine($"{Prefix}{{\"event\":\"{name}\",{payload}}}");
                Console.Out.Flush();
            }
            catch (Exception)
            {
                // 状态输出失败不拖垮主流程（宿主主要靠端口探测兜底）
            }
        }

        private static string Quote(string value)
        {
            if (value == null) return "\"\"";
            // JSON 字符串转义的最小集（房间号/昵称等常规文本足够）
            return "\"" + value
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\r", "\\r")
                .Replace("\n", "\\n") + "\"";
        }
    }
}
