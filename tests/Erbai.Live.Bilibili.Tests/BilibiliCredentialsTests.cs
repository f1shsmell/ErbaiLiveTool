using System.Text;
using Erbai.Live.Bilibili.Login;
using Erbai.Live.Bilibili.Protocol;

namespace Erbai.Live.Bilibili.Tests;

/// <summary>凭据信封（v2 / dpapi|plain / 原子写 / 明文迁移）。</summary>
public class BilibiliCredentialsTests
{
    private static string TempPath() =>
        Path.Combine(AppContext.BaseDirectory, "tmp", $"cred-{Guid.NewGuid():N}", "bilibili_credentials.json");

    private static BilibiliCookies Sample() => new()
    {
        Sessdata = "sd-测试-值",
        BiliJct = "jc",
        DedeUserId = "10086",
        RefreshToken = "rt",
    };

    [Fact]
    public async Task WriteRead_Roundtrip()
    {
        var path = TempPath();
        var creds = new BilibiliCredentials(path);

        await creds.WriteAsync(Sample());

        var loaded = await creds.ReadAsync();
        Assert.NotNull(loaded);
        Assert.Equal(Sample().Sessdata, loaded!.Sessdata);
        Assert.Equal("jc", loaded.BiliJct);
        Assert.Equal("10086", loaded.DedeUserId);
        Assert.Equal("rt", loaded.RefreshToken);
    }

    [Fact]
    public async Task Write_ProducesVersionedEnvelope()
    {
        var path = TempPath();
        await new BilibiliCredentials(path).WriteAsync(Sample());

        var raw = await File.ReadAllTextAsync(path);
        Assert.Contains("\"v\": 2", raw, StringComparison.Ordinal);
        if (BilibiliCredentials.DpapiAvailable)
        {
            Assert.Contains("\"cipher\": \"dpapi\"", raw, StringComparison.Ordinal);
            Assert.DoesNotContain("sd-测试-值", raw, StringComparison.Ordinal); // 明文绝不落盘
        }
        else
        {
            Assert.Contains("\"cipher\": \"plain\"", raw, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Read_MissingFile_ReturnsNull()
    {
        var creds = new BilibiliCredentials(TempPath());

        Assert.Null(await creds.ReadAsync());
    }

    [Fact]
    public async Task Read_CorruptFile_ReturnsNull()
    {
        var path = TempPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "{{{not-json");

        Assert.Null(await new BilibiliCredentials(path).ReadAsync());
    }

    [Fact]
    public async Task Read_WrongVersion_ReturnsNull()
    {
        var path = TempPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, """{"v":1,"payload":"e30="}""");

        Assert.Null(await new BilibiliCredentials(path).ReadAsync());
    }

    [Fact]
    public async Task Read_TamperedDpapiPayload_ReturnsNull()
    {
        var path = TempPath();
        var creds = new BilibiliCredentials(path);
        await creds.WriteAsync(Sample());

        // 篡改 payload 使 DPAPI 解密失败 → 返回 null，绝不降级为明文
        var raw = await File.ReadAllTextAsync(path);
        var tampered = raw.Replace("\"payload\": \"", "\"payload\": \"x", StringComparison.Ordinal);
        await File.WriteAllTextAsync(path, tampered);

        var loaded = await creds.ReadAsync();
        if (BilibiliCredentials.DpapiAvailable)
        {
            Assert.Null(loaded); // dpapi 解密失败必须拒绝
        }
    }

    [Fact]
    public async Task Read_LegacyPlaintext_MigratesToEnvelope()
    {
        var path = TempPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // 旧版明文 JSON（无 v 字段）
        await File.WriteAllTextAsync(path, """{"sessdata":"legacy-sd","bili_jct":"legacy-jc","dedeuserid":"1","refresh_token":"legacy-rt"}""");

        var loaded = await new BilibiliCredentials(path).ReadAsync();

        Assert.NotNull(loaded);
        Assert.Equal("legacy-sd", loaded!.Sessdata);

        // 已迁移为信封格式
        var raw = await File.ReadAllTextAsync(path);
        Assert.Contains("\"v\": 2", raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Delete_RemovesFile()
    {
        var path = TempPath();
        var creds = new BilibiliCredentials(path);
        Assert.False(creds.Delete());

        await creds.WriteAsync(Sample());
        Assert.True(creds.Delete());
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task Write_OverwritesAtomically()
    {
        var path = TempPath();
        var creds = new BilibiliCredentials(path);
        await creds.WriteAsync(Sample());
        await creds.WriteAsync(Sample() with { Sessdata = "sd-v2" });

        var loaded = await creds.ReadAsync();
        Assert.Equal("sd-v2", loaded!.Sessdata);
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!)); // 无 .tmp 残留
    }

    [Fact]
    public void Dpapi_AvailableOnWindows()
    {
        Assert.Equal(OperatingSystem.IsWindows(), BilibiliCredentials.DpapiAvailable);
    }
}
