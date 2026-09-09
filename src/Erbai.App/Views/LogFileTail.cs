using System.Text;

namespace Erbai.App.Views;

/// <summary>
/// 日志文件尾部只读窗口（按行）。纯逻辑、无 WinUI/UI 依赖，可被测试项目以
/// 源文件链接方式复用（与 OverlayRenderers.cs 同款；见 Erbai.App.Tests.csproj）。
///
/// 背景：日志页打开时要回看当日日志，但当日文件可能积累数十万行。若先全量读入
/// 再裁剪（旧 <c>ReadAllLinesShared</c>），打开日志页会做全文件 I/O + 逐行处理，
/// 是"打开日志页卡很久"的主要来源（docs/00 修复记录）。本类只解码文件最尾部
/// ≤<paramref name="maxLines"/> 行的窗口：从文件尾部向前分块扫换行，攒够即停。
/// </summary>
internal static class LogFileTail
{
    /// <summary>
    /// 只读日志文件尾部 ≤maxLines 行（共享句柄，兼容 Serilog 等仍持有写入句柄的进程）。
    /// 从文件尾部向前分块读换行，攒够 maxLines 即停，只解码尾部窗口。
    /// </summary>
    /// <param name="path">日志文件路径。</param>
    /// <param name="maxLines">返回行数上限。</param>
    /// <returns>文件正序的尾部窗口（每行已去掉末尾 \r）。窗口头部若停在某行中间会丢弃。</returns>
    public static List<string> ReadTailLinesShared(string path, int maxLines)
    {
        const int chunkSize = 64 * 1024;
        var result = new List<string>();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length == 0)
        {
            return result;
        }

        // 从尾向前读块，把字节倒序累积（reverse 前是"文件尾部→头部"序），
        // 换行数超限即停——窗口大小 ≈ maxLines 行 + 至多一个块
        var buffer = new byte[chunkSize];
        var tail = new List<byte>(chunkSize * 2);
        long position = stream.Length;
        var newlines = 0;
        var truncatedHead = false; // 提前停止 → 窗口头部是半行，须丢弃
        while (position > 0)
        {
            var readLength = (int)Math.Min(chunkSize, position);
            position -= readLength;
            stream.Seek(position, SeekOrigin.Begin);
            var read = stream.Read(buffer, 0, readLength);
            for (var i = read - 1; i >= 0; i--)
            {
                tail.Add(buffer[i]);
                if (buffer[i] == (byte)'\n')
                {
                    newlines++;
                }
            }

            if (newlines > maxLines)
            {
                truncatedHead = true;
                break;
            }
        }

        tail.Reverse(); // 恢复文件正序的尾部窗口
        var text = Encoding.UTF8.GetString(tail.ToArray());
        var lines = text.Split('\n');
        // Split('\n') 在末尾换行时会产生一个虚拟空元素，它不是真实日志行，须剔除
        // （否则 CRLF 日志 / 末尾换行文件会多出一行空日志）。窗口最后一个字节恒等于
        // 文件最后一个字节，故该空元素是否出现由文件本身决定，与是否截断无关。
        var end = lines.Length;
        if (end > 0 && lines[end - 1].Length == 0)
        {
            end--;
        }

        // 窗口头部可能截断在某行中间（提前停止的多读冗余），丢弃；否则从 0 起
        var start = truncatedHead ? Math.Max(end - maxLines, 0) : 0;
        for (var i = start; i < end; i++)
        {
            result.Add(lines[i].TrimEnd('\r'));
        }

        return result;
    }
}