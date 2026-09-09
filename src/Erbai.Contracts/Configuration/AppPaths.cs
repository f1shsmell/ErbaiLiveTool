namespace Erbai.Contracts.Configuration;

/// <summary>
/// 应用随附文件定位（config.json / logs / sqlite / 子进程 exe / bridge / profiles）。
/// 一律以【宿主 exe 所在目录】为准（app-dir 语义，docs/00 修复记录）：
/// 单文件发布（PublishSingleFile + IncludeAllContentForSelfExtract）运行时，
/// <see cref="AppContext.BaseDirectory"/> 指向 %TEMP%\.net\&lt;App&gt;\&lt;hash&gt; 的
/// bundle 解压缓存目录（.NET 8 实测，PathProbe 复现），随附文件并不在那里——
/// 按 BaseDirectory 拼接会把配置/日志/数据库/子进程 exe 全部错误定位到 Temp，
/// 表现为"抖音抓包器不存在 C:\Users\...\Temp\.net\..."、连接器未找到等。
/// <see cref="Environment.ProcessPath"/> 在单文件下返回宿主 exe 真实路径（已实测），
/// 多文件布局下与 BaseDirectory 等价，两种布局行为一致。
/// </summary>
public static class AppPaths
{
    /// <summary>宿主可执行文件所在目录（末尾带目录分隔符，见 Path.GetDirectoryName 语义）。</summary>
    public static string HostDir { get; } = ResolveHostDir();

    /// <summary>按宿主目录拼接（Path.Combine(宿主目录, parts...)）。</summary>
    public static string Combine(params string[] parts)
    {
        var all = new string[parts.Length + 1];
        all[0] = HostDir;
        Array.Copy(parts, 0, all, 1, parts.Length);
        return System.IO.Path.Combine(all);
    }

    private static string ResolveHostDir()
    {
        try
        {
            var exePath = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(exePath))
            {
                var dir = System.IO.Path.GetDirectoryName(exePath);
                if (!string.IsNullOrWhiteSpace(dir))
                {
                    return dir;
                }
            }
        }
        catch (Exception)
        {
            // 取不到时回退 BaseDirectory（至少不抛异常）
        }

        return AppContext.BaseDirectory;
    }
}