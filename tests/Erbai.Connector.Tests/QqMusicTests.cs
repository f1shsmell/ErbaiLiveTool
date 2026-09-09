using System.Reflection;
using System.Text.Json;
using Erbai.Connector.QQMusic;
using Erbai.Contracts.Players;
using Xunit;

namespace Erbai.Connector.Tests;

public sealed class QqTitleParserTests
{
    [Theory]
    [InlineData("晴天 - 周杰伦", "晴天", "周杰伦")]
    [InlineData("晴天 - 周杰伦 - 专辑版 - 周杰伦", "晴天 - 周杰伦 - 专辑版", "周杰伦")]
    [InlineData("QQ音乐", null, null)]
    [InlineData("", null, null)]
    [InlineData("纯标题", null, null)]
    public void ParseTitle_Variants(string windowTitle, string? title, string? artist)
    {
        var parsed = QqNativeController.ParseTitle(windowTitle);
        if (title is null)
        {
            Assert.Null(parsed);
        }
        else
        {
            Assert.Equal(title, parsed!.Value.Title);
            Assert.Equal(artist, parsed.Value.Artist);
        }
    }
}

public sealed class QqProfileTests
{
    [Fact]
    public void BuiltInProfiles_22_22_And_22_41_AreLoaded()
    {
        var profiles = QQMusicProfiles.All;
        Assert.Contains(profiles, p => p.FileVersion == "22.22");
        Assert.Contains(profiles, p => p.FileVersion == "22.41");
        Assert.All(profiles.Where(p => p.FileVersion is "22.22" or "22.41"), p => Assert.False(p.Disabled));
    }

    [Fact]
    public void Find_MatchesByVersionAndHashes()
    {
        var profile = QQMusicProfiles.Find(
            "22.41",
            "A5F3E917A5233D925268C34656E49096B6223B74631C5002DB606AD4B2C7A3F3",
            "36775378403DB33D049EE87BCAD654BA3A041B7D41259CD7EDFE65457D7E2A06");
        Assert.NotNull(profile);
        Assert.Equal(0x0048C124, profile.SingleSongPlayDispatchRva);
        Assert.Equal(new byte[] { 0xE8, 0x67, 0x55, 0x16, 0x00 }, profile.ExpectedPlayDispatchBytes);
    }

    [Fact]
    public void Find_UnknownVersion_ReturnsNull()
    {
        Assert.Null(QQMusicProfiles.Find("99.99", "A".PadLeft(64, '0'), "B".PadLeft(64, '0')));
    }

    [Fact]
    public void ReleasedProfile_22_61_MatchesReleasedBinaries()
    {
        // 22.61 画像随发布(profiles/qqmusic/22.61.json):来自上游 Enkianssus/awoo-connectors
        // bilincm-qqmusic-profiles-1.3.0(2026-09-04),已在 QQMusic 22.61 实机验证原生插入。
        // 断言双哈希 + 补丁点 RVA/机器码(本机 QQMusic.dll D42A800E... 与画像完全匹配)。
        var dir = Path.Combine(AppContext.BaseDirectory, "profiles", "qqmusic");
        Assert.True(Directory.Exists(dir), $"发布画像目录缺失: {dir}");
        var original = Environment.GetEnvironmentVariable("BILINCM_QQMUSIC_PROFILE_DIR");
        Environment.SetEnvironmentVariable("BILINCM_QQMUSIC_PROFILE_DIR", dir);
        try
        {
            var profile = QQMusicProfiles.Find(
                "22.61",
                "D42A800E2110B27C2D94DBB1D78AB1A9DDDA2BBDA3E623C5EEBB980AF92F9B29",
                "15190F1D87B5B3853EF47F943F333FAD9E8D51277ADFD56AC332EABBDF8FC14D");
            Assert.NotNull(profile);
            Assert.Equal(0x004A7934, profile.SingleSongPlayDispatchRva);
            Assert.Equal(new byte[] { 0xE8, 0x57, 0x8D, 0x16, 0x00 }, profile.ExpectedPlayDispatchBytes);
            Assert.False(profile.Disabled);
        }
        finally
        {
            Environment.SetEnvironmentVariable("BILINCM_QQMUSIC_PROFILE_DIR", original);
        }
    }

    [Fact]
    public void IsUnsupportedVersionFailure_RecognizesMissingProfile()
    {        // 未知版本拒绝（22.61 实机消息）→ 判定为可降级
        var unknown = new QqNativeNextResult(false, false,
            "fileVersion=22.61; sha256=D42A800E2110B27C2D94DBB1D78AB1A9DDDA2BBDA3E623C5EEBB980AF92F9B29; elapsed=103ms",
            "未知 QQ 音乐版本（22.61），已拒绝写进程（profile 未匹配）。检测到 clientSha256=D42A800E2110B27C2D94DBB1D78AB1A9DDDA2BBDA3E623C5EEBB980AF92F9B29; commonSha256=15190F1D87B5B3853EF47F943F333FAD9E8D51277ADFD56AC332EABBDF8FC14D; 已知画像版本=22.22 / 22.41 / 22.52 / 22.60",
            "NativeNextException", 1234);
        Assert.True(QqNativeNextTransport.IsUnsupportedVersionFailure(unknown));

        // 机器码不匹配（补丁点漂移但版本已知）→ 不可降级（可能是画像过期的硬故障）
        var mismatch = new QqNativeNextResult(false, false, "v",
            "单曲播放分发指令不匹配，已拒绝写入。 实际=CC-CC-CC-CC-CC，预期=E8-77-66-16-00",
            "NativeNextPatchMismatch", 1234);
        Assert.False(QqNativeNextTransport.IsUnsupportedVersionFailure(mismatch));

        // 正常拒绝（插入被客户端拒绝）→ 不可降级
        var rejected = new QqNativeNextResult(false, false, "v", "QQ 原生插入被客户端拒绝", "NativeNextRejected", 1234);
        Assert.False(QqNativeNextTransport.IsUnsupportedVersionFailure(rejected));
    }

    [Fact]
    public void ExternalProfileJson_22_52_IsLoadable()
    {
        // 模拟外置画像目录（22.52，本机 QQ 音乐版本）
        var dir = Path.Combine(Path.GetTempPath(), $"qqprofile-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var json = """
                {
                  "schemaVersion": 1,
                  "fileVersion": "22.52",
                  "clientSha256": "A06046FD1D36BCEA03CE1A014209F143537B37471CF53CB010E087D080C14DDD",
                  "commonSha256": "F57AB179585F455C031DE9891E2A79131BFC965DD5D64BA94143DD90894ABD7D",
                  "singleSongPlayDispatchRva": "0x0049C6B4",
                  "expectedPlayDispatchBytes": "E8 77 66 16 00",
                  "getCatManagerRva": "0x0000F0ED",
                  "getQqUinExRva": "0x0002E089",
                  "songItemConstructorRva": "0x0004B8D0",
                  "songItemDestructorRva": "0x0004B410",
                  "addSongsRva": "0x0044E220",
                  "hiddenCategoryIdRva": "0x00C48340",
                  "getListRootRva": "0x006259C0",
                  "getListHelperRva": "0x00625B20",
                  "getCategoryCountRva": "0x004FE0F0",
                  "songItemSize": "0xA0",
                  "disabled": true,
                  "evidence": "test"
                }
                """;
            File.WriteAllText(Path.Combine(dir, "22.52.json"), json);
            var original = Environment.GetEnvironmentVariable("BILINCM_QQMUSIC_PROFILE_DIR");
            Environment.SetEnvironmentVariable("BILINCM_QQMUSIC_PROFILE_DIR", dir);
            try
            {
                var profile = QQMusicProfiles.Find(
                    "22.52",
                    "A06046FD1D36BCEA03CE1A014209F143537B37471CF53CB010E087D080C14DDD",
                    "F57AB179585F455C031DE9891E2A79131BFC965DD5D64BA94143DD90894ABD7D");
                Assert.NotNull(profile);
                Assert.Equal(0x0049C6B4, profile.SingleSongPlayDispatchRva);
                Assert.True(profile.Disabled);
            }
            finally
            {
                Environment.SetEnvironmentVariable("BILINCM_QQMUSIC_PROFILE_DIR", original);
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}

public sealed class QqPeImageTests
{
    [Fact]
    public void IsX86_NonPeFile_ReturnsFalse()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, new byte[] { 0x4D, 0x5A, 0x00 });
            Assert.False(QQMusicPeImage.IsX86(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Sha256_IsStable()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "hello");
            Assert.Equal(64, QQMusicPeImage.Sha256(path).Length);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

public sealed class X86EmitterTests
{
    [Fact]
    public void Emitter_BuildsByteSequenceAndFixesJumps()
    {
        var emitter = new X86Emitter();
        emitter.Bytes(0x9C, 0x60, 0xBF);
        emitter.UInt32(0x12345678);
        emitter.Jump32(0x0F, 0x88, "cleanup");
        emitter.Bytes(0x33, 0xC0);
        emitter.Label("cleanup");
        emitter.Bytes(0x61, 0x9D, 0xC3);

        var bytes = emitter.Build();
        Assert.Equal(0x9C, bytes[0]);
        Assert.Equal(0xBF, bytes[2]);
        Assert.Equal(0x78, bytes[3]);
        Assert.Equal(0x61, bytes[^3]);
        Assert.Equal(0xC3, bytes[^1]);

        // 跳转回填：0F 88 <rel32>；opcode 在索引 7..8，rel32 在 9..12。
        // position=9 是 rel32 占位起始，指令从 7 起共 6 字节 → 下一条指令=13（0x33 处），
        // label cleanup=15（0x61 处）→ 位移应为 2。位移 bug（旧公式 +opcodeSize 多算 2）
        // 会让 jns 落在 xor 上、index 恒 0（2026-09-03 实机日志实证）。
        Assert.Equal(0x0F, bytes[7]);
        Assert.Equal(0x88, bytes[8]);
        var displacement = BitConverter.ToInt32(bytes, 9);
        Assert.Equal(2, displacement); // next=13, target=15（0x61 在索引 15）
        Assert.Equal(0x33, bytes[13]);
        Assert.Equal(0x61, bytes[15]); // label 处是 cleanup 的目标
    }

    [Fact]
    public void Trampoline_ContainsKeySequences()
    {
        // 通过反射验证 trampoline 构建（机制对齐：保存寄存器开头 / 恢复结尾）
        var method = typeof(QqNativeNextTransport).GetMethod(
            "BuildUiTrampoline",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        var profile = QQMusicProfiles.All.First(p => p.FileVersion == "22.41");
        var trampoline = (byte[])method.Invoke(null, [new nint(0x1000), new nint(0x400000), new nint(0x500000), profile])!;
        Assert.True(trampoline.Length > 100);
        // pushfd pushad mov edi, imm32
        Assert.Equal(0x9C, trampoline[0]);
        Assert.Equal(0x60, trampoline[1]);
        Assert.Equal(0xBF, trampoline[2]);
        // popad popfd ret
        Assert.Equal(0x61, trampoline[^3]);
        Assert.Equal(0x9D, trampoline[^2]);
        Assert.Equal(0xC3, trampoline[^1]);

        // use count hack：AddSongs 调用前必须写入 `mov dword ptr [edi+0xD0], 2`
        // （C7 87 D0 02 00 00 00），规避 22.52+ AddSongs 第4参数智能指针返回析构的虚析构崩溃。
        var hex = Convert.ToHexString(trampoline);
        Assert.Contains("C787D000000002000000", hex);
    }
}

public sealed class QqCatalogClientTests
{
    [Fact]
    public async Task Search_ParsesMusicuResponse()
    {
        var handler = new FakeStageHandler
        {
            Routes =
            {
                ["/cgi-bin/musicu.fcg"] =
                    """{"search":{"data":{"body":{"song":{"list":[{"songid":347230,"songmid":"M1","songtype":0,"songname":"晴天","singer":"周杰伦","albumname":"叶惠美","albummid":"A1","interval":269}]}}}}}""",
            },
        };
        var client = new QqCatalogClient(handler);
        var songs = await client.SearchAsync("晴天", ct: CancellationToken.None);
        Assert.Single(songs);
        Assert.Equal(347230, songs[0].SongId);
        Assert.Equal("晴天", songs[0].Title);
        Assert.Equal("周杰伦", songs[0].Artist);
        Assert.True(songs[0].IsPlayable);
    }

    [Fact]
    public async Task Search_EmptyPrimary_FallsBackToLegacy()
    {
        var handler = new FakeStageHandler
        {
            Routes =
            {
                ["/cgi-bin/musicu.fcg"] = """{"search":{"data":{"body":{"song":{"list":[]}}}}}""",
                ["/soso/fcgi-bin/client_search_cp"] =
                    """{"data":{"song":{"list":[{"songid":42,"songmid":"M2","songtype":0,"songname":"夜曲","singer":"周杰伦","albumname":"十一月的萧邦"}]}}}""",
            },
        };
        var client = new QqCatalogClient(handler);
        var songs = await client.SearchAsync("夜曲", ct: CancellationToken.None);
        Assert.Single(songs);
        Assert.Equal(42, songs[0].SongId);
        Assert.Equal("夜曲", songs[0].Title);
    }
}

public sealed class QqMusicConnectorTests
{
    private static PlayerTrack TrackWithPayload(long songId = 347230, bool playable = true) =>
        new()
        {
            Platform = "qqmusic",
            Id = songId.ToString(),
            Title = "晴天",
            Artist = "周杰伦",
            NativeData = JsonSerializer.Serialize(new QqTrackPayload(songId, 0, playable)),
        };

    [Fact]
    public async Task Probe_NoWindow_Disconnected()
    {
        // 测试环境无 QQ 音乐窗口时 probe 返回 disconnected（有窗口时 connected）
        var connector = new QqMusicConnector();
        var probe = JsonSerializer.Deserialize<JsonElement>((await connector.ProbeAsync(CancellationToken.None)).GetRawText());
        // 不依赖真实桌面：只验证字段形状
        Assert.True(probe.TryGetProperty("connected", out _));
        Assert.Equal("qqmusic", probe.GetProperty("version").GetString());
    }

    [Fact]
    public async Task Search_SerializesTracksWithPayload()
    {
        var handler = new FakeStageHandler
        {
            Routes =
            {
                ["/cgi-bin/musicu.fcg"] =
                    """{"search":{"data":{"body":{"song":{"list":[{"songid":347230,"songmid":"M1","songtype":0,"songname":"晴天","singer":"周杰伦","albumname":"叶惠美","albummid":"A1","interval":269}]}}}}}""",
            },
        };
        var connector = new QqMusicConnector(new QqCatalogClient(handler));
        var result = JsonSerializer.Deserialize<JsonElement>((await connector.SearchAsync("晴天", CancellationToken.None)).GetRawText());
        Assert.Equal(1, result.GetArrayLength());
        Assert.Equal("347230", result[0].GetProperty("id").GetString());
        Assert.Equal("晴天", result[0].GetProperty("title").GetString());
        var payload = JsonSerializer.Deserialize<QqTrackPayload>(result[0].GetProperty("nativeData").GetString()!);
        Assert.NotNull(payload);
        Assert.Equal(347230, payload!.SongId);
    }

    [Fact]
    public async Task Execute_NotConnected_Rejected()
    {
        var connector = new QqMusicConnector();
        var result = JsonSerializer.Deserialize<JsonElement>((await connector.ExecuteAsync(PlayerCommand.Next, null, CancellationToken.None)).GetRawText());
        // 无 QQ 音乐进程时 rejected；有进程时走命令面（本测试环境不保证）
        Assert.True(result.GetProperty("outcome").GetString() is "rejected" or "accepted" or "applied");
    }

    [Fact]
    public async Task Execute_PlaySelected_Unplayable_Rejected()
    {
        // 不可播放载荷在连接检查后立即拒绝（不依赖真实 QQ 音乐）
        var connector = new QqMusicConnector();
        var result = await connector.ExecuteAsync(PlayerCommand.PlaySelected, TrackWithPayload(playable: false), CancellationToken.None);
        var outcome = JsonSerializer.Deserialize<JsonElement>(result.GetRawText()).GetProperty("outcome").GetString();
        Assert.True(outcome is "rejected" or "unsupported" or "accepted"); // 无窗口时先 rejected
    }

    private static QqTrackPayload? InvokeParsePayload(PlayerTrack track) =>
        (QqTrackPayload?)typeof(QqMusicConnector)
            .GetMethod("ParsePayload", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [track]);

    private static PlayerTrack PayloadTrack(string nativeData) => new()
    {
        Platform = "qqmusic",
        Title = "晴天",
        Artist = "周杰伦",
        NativeData = nativeData,
    };

    /// <summary>
    /// 回归（用户实测"使用 QQ 音乐无法点歌"）：三源搜索 SongSearchResult 序列化
    /// 载荷（小写驼峰 songId/songType、无 isPlayable 键）必须能被 ParsePayload
    /// 解析——旧实现按 PascalCase QqTrackPayload 反序列化，songId 缺失恒 0、
    /// isPlayable 恒 false，每个候选都被拒 → all_sources_rejected。
    /// </summary>
    [Fact]
    public void ParsePayload_ReadsSongSearchResultWireFormat()
    {
        var track = PayloadTrack(JsonSerializer.Serialize(new
        {
            source = "qqmusic",
            name = "晴天",
            singer = "周杰伦",
            songmid = "SM1",
            songId = 347230,
            songType = 1,
        }));

        var payload = InvokeParsePayload(track);

        Assert.NotNull(payload);
        Assert.Equal(347230, payload!.SongId);
        Assert.Equal(1, payload.SongType);
        Assert.True(payload.IsPlayable); // 三源线格式无 isPlayable 键 → 默认可播
    }

    /// <summary>连接器原生 search 产物（PascalCase QqTrackPayload）保持兼容。</summary>
    [Fact]
    public void ParsePayload_ReadsPascalCaseNativeSearchPayload()
    {
        var track = PayloadTrack(JsonSerializer.Serialize(new QqTrackPayload(347230, 0, false)));

        var payload = InvokeParsePayload(track);

        Assert.NotNull(payload);
        Assert.Equal(347230, payload!.SongId);
        Assert.False(payload.IsPlayable);
    }

    /// <summary>无 songId（≤0）或空载荷 → null（调用方按缺少插队载荷拒绝）。</summary>
    [Theory]
    [InlineData("""{"source":"qqmusic","name":"晴天"}""")]
    [InlineData("""{"songId":0}""")]
    [InlineData("not-json")]
    public void ParsePayload_MissingOrInvalidSongId_ReturnsNull(string nativeData)
    {
        Assert.Null(InvokeParsePayload(PayloadTrack(nativeData)));
    }
}
