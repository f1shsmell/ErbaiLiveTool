using System.IO.Compression;
using Erbai.App.Services;

namespace Erbai.App.Backup;

/// <summary>
/// 备份/恢复（阶段 6 M8，简化版）：备份 = 数据库一致性快照（SQLite backup
/// API）+ 配置文件 → 单个 zip（Documents/ErbaiLiveTool/backups/）；恢复 =
/// 解包 → 关存储 → 替换文件 → 重开存储（部分配置热生效，其余提示重启）。
/// </summary>
public static class BackupService
{
    public static string BackupDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "ErbaiLiveTool",
        "backups");

    /// <summary>创建备份 zip，返回路径。</summary>
    public static async Task<string> CreateBackupAsync(AppServices services, CancellationToken ct = default)
    {
        Directory.CreateDirectory(BackupDirectory);
        var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var zipPath = Path.Combine(BackupDirectory, $"backup-{timestamp}.zip");

        var dbSnapshot = Path.Combine(Path.GetTempPath(), $"erbai-db-{Guid.NewGuid():N}.db");
        try
        {
            await services.Storage.BackupToAsync(dbSnapshot, ct);

            // 备份刚写完的 .db 在 %TEMP% 可能被实时扫描/索引服务短暂占用
            // （实测偶发 "The process cannot access the file ... erbai-db-*.db"）：
            // 打包动作整体带短重试；成功后的临时文件删除同样重试。
            await RetryOnShareViolationAsync(() =>
            {
                using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
                zip.CreateEntryFromFile(dbSnapshot, "data.db");
                zip.CreateEntryFromFile(services.Config.ConfigPath, "config.json");
            }, ct);
        }
        catch
        {
            // 失败不残留半成品 zip（否则下拉列表出现 0 KB 假备份）
            TryDeleteQuietly(zipPath);
            throw;
        }
        finally
        {
            try
            {
                await RetryOnShareViolationAsync(() => File.Delete(dbSnapshot), ct);
            }
            catch (Exception)
            {
                // 临时文件清理失败不升级为备份失败；%TEMP% 残留由系统回收
            }
        }

        return zipPath;
    }

    /// <summary>从备份 zip 恢复（替换数据库 + 配置），返回提示文本。</summary>
    public static async Task<string> RestoreBackupAsync(AppServices services, string zipPath, CancellationToken ct = default)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var dbEntry = zip.GetEntry("data.db");
        var configEntry = zip.GetEntry("config.json");
        if (dbEntry is null && configEntry is null)
        {
            throw new InvalidOperationException("备份文件不包含 data.db 或 config.json。");
        }

        var tempDir = Path.Combine(Path.GetTempPath(), $"erbai-restore-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var restoredDb = Path.Combine(tempDir, "data.db");
        var restoredConfig = Path.Combine(tempDir, "config.json");
        try
        {
            if (dbEntry is not null)
            {
                dbEntry.ExtractToFile(restoredDb, overwrite: true);
            }

            if (configEntry is not null)
            {
                configEntry.ExtractToFile(restoredConfig, overwrite: true);
            }

            // 关闭存储后替换文件，再重开（SQLite 文件被引擎持有）
            await services.Storage.CloseAsync();
            try
            {
                // 目标文件可能被杀软/索引短暂占用（偶发共享冲突）：替换带短重试
                await RetryOnShareViolationAsync(() =>
                {
                    if (File.Exists(restoredDb))
                    {
                        File.Copy(restoredDb, services.Storage.DatabasePath, overwrite: true);
                    }

                    if (File.Exists(restoredConfig))
                    {
                        File.Copy(restoredConfig, services.Config.ConfigPath, overwrite: true);
                    }
                }, ct);
            }
            finally
            {
                await services.Storage.OpenAsync(ct);
            }

            // 配置热重载：替换 Settings 内存快照（ConfigStore 无公开 reload——
            // 简化版提示重启使配置段完全生效）
            var restored = new List<string>();
            if (File.Exists(restoredDb))
            {
                restored.Add("数据库");
            }

            if (File.Exists(restoredConfig))
            {
                restored.Add("配置");
            }

            return $"已恢复：{string.Join("、", restored)}。数据库已生效；配置建议重启应用后完全生效。";
        }
        finally
        {
            try
            {
                Directory.Delete(tempDir, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>最近备份文件列表（按时间倒序）。</summary>
    public static IReadOnlyList<string> ListBackups()
    {
        if (!Directory.Exists(BackupDirectory))
        {
            return [];
        }

        return Directory.EnumerateFiles(BackupDirectory, "backup-*.zip")
            .OrderByDescending(File.GetLastWriteTime)
            .ToArray();
    }

    private const int RetryCount = 5;
    private const int RetryDelayMs = 250;

    /// <summary>
    /// 对 Windows 偶发文件共享冲突（杀软实时扫描/索引服务短暂持有文件句柄）
    /// 做短重试；重试耗尽后继续抛原 IOException。
    /// </summary>
    private static async Task RetryOnShareViolationAsync(Action action, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (IOException) when (attempt < RetryCount - 1)
            {
                await Task.Delay(RetryDelayMs, ct);
            }
        }
    }

    /// <summary>尽力删除（失败静默，绝不升级为调用方异常）。</summary>
    private static void TryDeleteQuietly(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
    }
}
