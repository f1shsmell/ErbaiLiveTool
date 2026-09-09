using System.Text.Json;
using Erbai.Contracts.Configuration;
using Erbai.Core.Configuration;

namespace Erbai.Core.Tests;

/// <summary>
/// 配置系统（行为域：配置校验/原子写/回滚）。
/// 校验规则（docs/03 §4）。
/// </summary>
public class ConfigStoreTests
{
    private static string TempDir() =>
        Path.Combine(AppContext.BaseDirectory, "tmp", $"erbai-tests-{Guid.NewGuid():N}");

    private static string WriteConfig(string dir, string json)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "config.json");
        File.WriteAllText(path, json);
        return path;
    }

    private static async Task<AppConfig> LoadFrom(string path)
    {
        var store = new ConfigStore(path);
        return await store.LoadAsync();
    }

    [Fact]
    public async Task FirstLoad_CreatesFile_WithDefaults()
    {
        var dir = TempDir();
        try
        {
            var path = Path.Combine(dir, "config.json");
            var store = new ConfigStore(path);
            var config = await store.LoadAsync();

            Assert.True(File.Exists(path));
            Assert.Equal("127.0.0.1", config.Ui.Host);
            Assert.Equal(19830, config.Ui.Port);
            Assert.Equal(20, config.Queue.MaxSize);
            Assert.Equal("lxmusic", config.Player.Key);
            Assert.Equal(new[] { "kugou", "netease", "qqmusic" }, config.Providers.Enabled);
            Assert.Equal("1,4,5,7", (string)config.DouyinGrabber.AppSettings["pushFilter"]);
            // 2026-09：overlay token 已移除（决策 #6 修订）——不再生成/落盘 token

            // 默认配置已落盘：重新加载值不变
            var reloaded = await LoadFrom(path);
            Assert.Equal(config.Ui.Port, reloaded.Ui.Port);
            Assert.Equal(config.Player.Key, reloaded.Player.Key);
        }
        finally
        {
            if (Directory.Exists(dir)) { Directory.Delete(dir, recursive: true); }
        }
    }

    [Fact]
    public async Task Load_ValidFile_IsAccepted()
    {
        var dir = TempDir();
        try
        {
            var path = WriteConfig(dir, """{ "ui": { "host": "localhost", "port": 20000 }, "queue": { "max_size": 50, "display_order": "DESC" } }""");
            var config = await LoadFrom(path);

            Assert.Equal("127.0.0.1", config.Ui.Host); // localhost 归一化
            Assert.Equal(20000, config.Ui.Port);
            Assert.Equal(50, config.Queue.MaxSize);
            Assert.Equal("desc", config.Queue.DisplayOrder); // 枚举归一化
        }
        finally
        {
            if (Directory.Exists(dir)) { Directory.Delete(dir, recursive: true); }
        }
    }

    [Fact]
    public async Task Load_InvalidPort_Throws()
    {
        var dir = TempDir();
        try
        {
            var path = WriteConfig(dir, """{ "ui": { "port": 0 } }""");
            await Assert.ThrowsAsync<ConfigException>(() => LoadFrom(path));
        }
        finally
        {
            if (Directory.Exists(dir)) { Directory.Delete(dir, recursive: true); }
        }
    }

    [Fact]
    public async Task Load_InvalidHost_Throws()
    {
        var dir = TempDir();
        try
        {
            var path = WriteConfig(dir, """{ "ui": { "host": "192.168.1.10" } }""");
            await Assert.ThrowsAsync<ConfigException>(() => LoadFrom(path));
        }
        finally
        {
            if (Directory.Exists(dir)) { Directory.Delete(dir, recursive: true); }
        }
    }

    [Fact]
    public async Task Load_CorruptJson_ThrowsWithActionableMessage()
    {
        var dir = TempDir();
        try
        {
            var path = WriteConfig(dir, "{ not json !!!");
            var ex = await Assert.ThrowsAsync<ConfigException>(() => LoadFrom(path));
            Assert.Contains("配置文件格式错误", ex.Message);
        }
        finally
        {
            if (Directory.Exists(dir)) { Directory.Delete(dir, recursive: true); }
        }
    }

    [Fact]
    public async Task Load_InvalidQueueLimits_Throw()
    {
        var dir = TempDir();
        try
        {
            await Assert.ThrowsAsync<ConfigException>(() =>
                LoadFrom(WriteConfig(dir, """{ "queue": { "max_size": 1000 } }""")));
            await Assert.ThrowsAsync<ConfigException>(() =>
                LoadFrom(WriteConfig(dir, """{ "queue": { "display_limit": 0 } }""")));
            await Assert.ThrowsAsync<ConfigException>(() =>
                LoadFrom(WriteConfig(dir, """{ "queue": { "max_per_user": 101 } }""")));
            await Assert.ThrowsAsync<ConfigException>(() =>
                LoadFrom(WriteConfig(dir, """{ "queue": { "playback_start_timeout": 0 } }""")));
            await Assert.ThrowsAsync<ConfigException>(() =>
                LoadFrom(WriteConfig(dir, """{ "queue": { "display_order": "sideways" } }""")));
        }
        finally
        {
            if (Directory.Exists(dir)) { Directory.Delete(dir, recursive: true); }
        }
    }

    [Fact]
    public async Task Load_UnknownProvider_Throws()
    {
        var dir = TempDir();
        try
        {
            var path = WriteConfig(dir, """{ "providers": { "enabled": ["kugou", "spotify"] } }""");
            await Assert.ThrowsAsync<ConfigException>(() => LoadFrom(path));
        }
        finally
        {
            if (Directory.Exists(dir)) { Directory.Delete(dir, recursive: true); }
        }
    }

    [Fact]
    public async Task Load_EmptyProviders_Throws()
    {
        var dir = TempDir();
        try
        {
            var path = WriteConfig(dir, """{ "providers": { "enabled": [] } }""");
            await Assert.ThrowsAsync<ConfigException>(() => LoadFrom(path));
        }
        finally
        {
            if (Directory.Exists(dir)) { Directory.Delete(dir, recursive: true); }
        }
    }

    [Fact]
    public async Task Load_UnknownPlayerKey_Throws()
    {
        var dir = TempDir();
        try
        {
            var path = WriteConfig(dir, """{ "player": { "key": "spotify" } }""");
            await Assert.ThrowsAsync<ConfigException>(() => LoadFrom(path));
        }
        finally
        {
            if (Directory.Exists(dir)) { Directory.Delete(dir, recursive: true); }
        }
    }

    [Fact]
    public async Task Load_GrabberUnknownAppSetting_Throws()
    {
        var dir = TempDir();
        try
        {
            var path = WriteConfig(dir, """{ "douyin_grabber": { "app_settings": { "logFilter": "x" } } }""");
            var ex = await Assert.ThrowsAsync<ConfigException>(() => LoadFrom(path));
            Assert.Contains("unsupported douyin_grabber.app_settings", ex.Message);
        }
        finally
        {
            if (Directory.Exists(dir)) { Directory.Delete(dir, recursive: true); }
        }
    }

    [Fact]
    public async Task Load_GrabberWrongType_Throws()
    {
        var dir = TempDir();
        try
        {
            // sysProxy 是布尔键，给整数应报错
            await Assert.ThrowsAsync<ConfigException>(() =>
                LoadFrom(WriteConfig(dir, """{ "douyin_grabber": { "app_settings": { "sysProxy": 1 } } }""")));
            // proxyPort 是端口键，范围 1-65535
            await Assert.ThrowsAsync<ConfigException>(() =>
                LoadFrom(WriteConfig(dir, """{ "douyin_grabber": { "app_settings": { "proxyPort": 70000 } } }""")));
            // pollingInterval 限 1000-60000
            await Assert.ThrowsAsync<ConfigException>(() =>
                LoadFrom(WriteConfig(dir, """{ "douyin_grabber": { "app_settings": { "pollingInterval": 500 } } }""")));
        }
        finally
        {
            if (Directory.Exists(dir)) { Directory.Delete(dir, recursive: true); }
        }
    }

    [Fact]
    public async Task Persist_ValidationFailure_LeavesMemoryAndDiskUntouched()
    {
        var dir = TempDir();
        try
        {
            var path = Path.Combine(dir, "config.json");
            var store = new ConfigStore(path);
            var original = await store.LoadAsync();

            var invalid = original with { Ui = original.Ui with { Port = 99999 } };
            await Assert.ThrowsAsync<ConfigException>(() => store.PersistAsync(invalid));

            // 内存不变
            Assert.Equal(original.Ui.Port, store.Settings.Ui.Port);
            // 磁盘不变（仍是首次创建的合法内容）
            var onDisk = await LoadFrom(path);
            Assert.Equal(original.Ui.Port, onDisk.Ui.Port);
        }
        finally
        {
            if (Directory.Exists(dir)) { Directory.Delete(dir, recursive: true); }
        }
    }

    [Fact]
    public async Task Persist_Success_UpdatesMemoryAndDisk()
    {
        var dir = TempDir();
        try
        {
            var path = Path.Combine(dir, "config.json");
            var store = new ConfigStore(path);
            var original = await store.LoadAsync();

            var updated = original with
            {
                Queue = original.Queue with { MaxSize = 66 },
                Permissions = original.Permissions with { LevelUnknownPolicy = "ALLOW" },
            };
            await store.PersistAsync(updated);

            Assert.Equal(66, store.Settings.Queue.MaxSize);
            Assert.Equal("allow", store.Settings.Permissions.LevelUnknownPolicy);

            var reloaded = await LoadFrom(path);
            Assert.Equal(66, reloaded.Queue.MaxSize);
        }
        finally
        {
            if (Directory.Exists(dir)) { Directory.Delete(dir, recursive: true); }
        }
    }

    [Fact]
    public void DefaultConfig_PassesValidation()
    {
        var validated = ConfigValidator.Validate(AppConfig.CreateDefault());
        Assert.Equal("127.0.0.1", validated.Ui.Host);
    }

    [Fact]
    public void DuplicateScope_And_AdminToken_AreRemoved()
    {
        // quirk #2 / 决策 #6：这两个键不允许出现在配置树中
        var json = JsonSerializer.Serialize(AppConfig.CreateDefault(), new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        Assert.DoesNotContain("duplicate_scope", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("admin_token", json, StringComparison.OrdinalIgnoreCase);
    }
}
