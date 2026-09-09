using System;
using System.IO;

namespace BarrageGrab
{
    /// <summary>
    /// 随附文件定位（scripts / logs / appConfig.json）：以宿主 exe 所在目录为准。
    /// 单文件发布（PublishSingleFile + IncludeAllContentForSelfExtract）下
    /// AppContext.BaseDirectory / AppDomain.CurrentDomain.BaseDirectory 指向
    /// %TEMP%\.net\&lt;App&gt;\&lt;hash&gt; 解压缓存目录（.NET 8 实测），随附文件并不在那里；
    /// Environment.ProcessPath 返回宿主 exe 真实路径（同样实测），两种布局行为一致。
    /// </summary>
    internal static class AppPaths
    {
        public static string HostDir { get; } = ResolveHostDir();

        public static string Combine(params string[] parts)
        {
            var all = new string[parts.Length + 1];
            all[0] = HostDir;
            Array.Copy(parts, 0, all, 1, parts.Length);
            return Path.Combine(all);
        }

        private static string ResolveHostDir()
        {
            try
            {
                var exePath = Environment.ProcessPath;
                if (!string.IsNullOrWhiteSpace(exePath))
                {
                    var dir = Path.GetDirectoryName(exePath);
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
}