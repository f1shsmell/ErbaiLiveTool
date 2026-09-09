using System.Security.Cryptography;
using System.Text.Json;
using Erbai.Contracts.Abstractions;
using Erbai.Contracts.Configuration;

namespace Erbai.Core.Configuration;

/// <summary>
/// 配置单一事实源：内存快照 +
/// 原子写盘（临时文件 + flush(fsync) + replace），先写盘成功再更新内存，
/// 失败抛 <see cref="ConfigException"/> 且内存/磁盘都不变。写操作串行化。
/// </summary>
public sealed class ConfigStore : IConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        // 与旧 config.json 的 snake_case 键名一致（max_size/display_limit/...）
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly string _path;
    private readonly object _sync = new();
    private AppConfig _settings;

    public ConfigStore(string path)
    {
        _path = Path.GetFullPath(path);
        _settings = AppConfig.CreateDefault();
    }

    public string ConfigPath => _path;

    public AppConfig Settings
    {
        get
        {
            lock (_sync)
            {
                return _settings;
            }
        }
    }

    /// <summary>
    /// 从磁盘加载并校验成为新快照；文件不存在时用默认值创建并写盘
    /// （首次启动自动生成 overlay token，对齐旧 create_config 语义）。
    /// </summary>
    public async Task<AppConfig> LoadAsync(CancellationToken ct = default)
    {
        AppConfig loaded;
        if (File.Exists(_path))
        {
            string json;
            try
            {
                json = await File.ReadAllTextAsync(_path, ct);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new ConfigException($"无法读取配置文件 {_path}: {ex.Message}", ex);
            }

            try
            {
                loaded = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions)
                         ?? throw new ConfigException("配置文件为空");
            }
            catch (JsonException ex)
            {
                throw new ConfigException(
                    $"配置文件格式错误：{_path}（{ex.Message}）。Windows 路径请使用正斜杠或双反斜杠", ex);
            }
        }
        else
        {
            loaded = AppConfig.CreateDefault();
        }

        loaded = ConfigValidator.Validate(loaded);
        if (!File.Exists(_path))
        {
            // 首次启动落盘默认配置（2026-09 起无 overlay token，不再因 token 生成而补写）
            await PersistAsync(loaded, ct);
        }

        lock (_sync)
        {
            _settings = loaded;
        }

        return loaded;
    }

    /// <summary>候选配置先校验 + 原子落盘成功，再提交为内存快照。</summary>
    public Task PersistAsync(AppConfig candidate, CancellationToken ct = default)
    {
        var validated = ConfigValidator.Validate(candidate);
        WriteAtomic(validated, ct);

        lock (_sync)
        {
            _settings = validated;
        }

        return Task.CompletedTask;
    }

    /// <summary>临时文件 + flush(fsync) + 原子替换（防半写）。</summary>
    private void WriteAtomic(AppConfig settings, CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".config.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");
        try
        {
            var json = JsonSerializer.Serialize(settings, JsonOptions);
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 4096,
                       FileOptions.WriteThrough))
            {
                using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false));
                writer.Write(json);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, _path, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch (IOException)
            {
                // 清理失败不影响结果（Windows 上 replace 已成功）
            }
        }
    }
}
