using System.Text.Json;
using Erbai.Contracts.Players;
using Erbai.Player.Connectors;

namespace Erbai.Connector.Tests;

/// <summary>
/// 第三方（上游 awoo-connectors）连接器的协议兼容性。
///
/// 背景：本仓库连接器与上游 <c>Awoo.Connector.*.exe</c> 的<b>请求面完全兼容</b>
/// （实测 ping/probe/search 均正常回包），但<b>能力协商面形状不同</b>：
/// <list type="bullet">
///   <item>上游：能力在 <c>result</c> 内——<c>capabilities</c>（布尔对象）+
///     <c>features</c>（字符串数组，含 <c>snapshot-events-v1</c>）；</item>
///   <item>本仓库旧宿主：能力在响应<b>顶层</b> <c>protocolCapabilities</c>（字符串数组），
///     且 <c>features</c> 为空、<c>capabilities</c> 为字符串数组。</item>
/// </list>
/// 若只读旧形状，对接上游时会算出 <see cref="PlayerCapabilities.None"/>：
/// 无快照事件流，且插播对账守卫被整体旁路（PlaybackStateMachine 判据）。
/// 这些用例锁定「两种形状都能正确归一」。
/// </summary>
public class ThirdPartyConnectorProtocolTests
{
    /// <summary>上游 netease 连接器 ping 的真实响应（2026-09-18 实测抓取，逐字）。</summary>
    private const string UpstreamNeteasePingResult = """
    {
      "protocolVersion": 1,
      "eventProtocolVersion": 1,
      "connectorId": "netease",
      "connectorVersion": "3.1.38.205386.1",
      "capabilities": {
        "search": true,
        "playSelected": true,
        "previous": true,
        "pause": true,
        "resume": true,
        "toggle": false,
        "next": true,
        "insertNext": true,
        "insertNextLevel": "进程内 CEF 插入并验证 + 错歌暂停接管守卫"
      },
      "features": ["snapshot-events-v1"]
    }
    """;

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    // ---------- ConnectorProtocol.ParsePing：上游规范形状 ----------

    [Fact]
    public void ParsePing_UpstreamShape_ReadsCapabilitiesObjectAndFeatures()
    {
        var ping = ConnectorProtocol.ParsePing(Parse(UpstreamNeteasePingResult));

        Assert.Equal(1, ping.ProtocolVersion);
        Assert.Equal(1, ping.EventProtocolVersion);
        Assert.Equal("netease", ping.ConnectorId);
        Assert.Equal("3.1.38.205386.1", ping.ConnectorVersion);
        Assert.True(ping.HasFeature(ConnectorProtocol.FeatureSnapshotEvents));
        Assert.True(ping.Capabilities.Search);
        Assert.True(ping.Capabilities.InsertNext);
        Assert.True(ping.Capabilities.Pause);
        Assert.True(ping.Capabilities.Resume);
        Assert.False(ping.Capabilities.Toggle);
        Assert.Equal("进程内 CEF 插入并验证 + 错歌暂停接管守卫", ping.Capabilities.InsertNextLevel);
    }

    [Fact]
    public void DeriveCapabilities_UpstreamShape_YieldsSnapshotEventsAndQueueProgrammable()
    {
        var ping = ConnectorProtocol.ParsePing(Parse(UpstreamNeteasePingResult));

        var caps = ConnectorPlayerPlugin.DeriveCapabilities(ping);

        // 回归：此前只读顶层 protocolCapabilities，上游连接器会得到 None
        // （无快照事件流 + 插播对账守卫被旁路）
        Assert.NotEqual(PlayerCapabilities.None, caps);
        Assert.True(caps.HasFlag(PlayerCapabilities.SnapshotEvents));
        Assert.True(caps.HasFlag(PlayerCapabilities.QueueProgrammable));
        Assert.True(caps.HasFlag(PlayerCapabilities.Search));
        Assert.True(caps.HasFlag(PlayerCapabilities.PauseResume));
    }

    // ---------- 本仓库旧宿主形状（向后兼容） ----------

    [Fact]
    public void ParsePing_LegacyShape_MergesTopLevelCapabilitiesIntoFeatures()
    {
        var ping = ConnectorProtocol.ParsePing(null, [ConnectorProtocol.FeatureSnapshotEvents]);

        Assert.True(ping.HasFeature(ConnectorProtocol.FeatureSnapshotEvents));
        // 旧宿主不产出 capabilities 对象 → 布尔位保持缺省 false
        Assert.False(ping.Capabilities.InsertNext);
        Assert.Null(ping.ConnectorId);
    }

    [Fact]
    public void DeriveCapabilities_LegacyShape_YieldsSnapshotEventsOnly()
    {
        var ping = ConnectorProtocol.ParsePing(null, [ConnectorProtocol.FeatureSnapshotEvents]);

        var caps = ConnectorPlayerPlugin.DeriveCapabilities(ping);

        Assert.True(caps.HasFlag(PlayerCapabilities.SnapshotEvents));
        // 旧形状没有 capabilities 对象，不臆造队列可编程能力
        Assert.False(caps.HasFlag(PlayerCapabilities.QueueProgrammable));
    }

    [Fact]
    public void ParsePing_BothShapesPresent_DeduplicatesFeatures()
    {
        var ping = ConnectorProtocol.ParsePing(
            Parse(UpstreamNeteasePingResult),
            [ConnectorProtocol.FeatureSnapshotEvents, ConnectorProtocol.FeatureQueueProgrammable]);

        Assert.Single(ping.Features, f => f == ConnectorProtocol.FeatureSnapshotEvents);
        Assert.Contains(ConnectorProtocol.FeatureQueueProgrammable, ping.Features);
        Assert.Equal(2, ping.Features.Count);
    }

    [Fact]
    public void ParsePing_MalformedInput_DoesNotThrow()
    {
        Assert.NotNull(ConnectorProtocol.ParsePing(null));
        Assert.NotNull(ConnectorProtocol.ParsePing(Parse("[]")));
        Assert.NotNull(ConnectorProtocol.ParsePing(Parse("\"text\"")));
        // capabilities 是数组（旧宿主形状）时不应崩，也不应误判为对象
        var ping = ConnectorProtocol.ParsePing(Parse("""{"capabilities":["snapshot-events-v1"]}"""));
        Assert.False(ping.Capabilities.InsertNext);
    }

    // ---------- 曲目平台键兜底（上游 search/快照不带 platform 字段） ----------

    [Fact]
    public void DeserializeTrack_UpstreamShapeWithoutPlatform_FallsBackToConnectorKey()
    {
        // 上游 netease search 结果的真实形状（无 platform 字段）
        var element = Parse("""
        {"id":"2652820720","title":"晴天(深情版)","artist":"Lucky小爱","album":"晴天(深情版)",
         "nativeData":"","coverUrl":"https://example.invalid/c.jpg","displayName":"晴天 - Lucky小爱"}
        """);

        var track = SnapshotParser.DeserializeTrack(element, "netease");

        Assert.NotNull(track);
        Assert.Equal("netease", track!.Platform);
        Assert.Equal("2652820720", track.Id);
        Assert.Equal("晴天(深情版)", track.Title);
    }

    [Fact]
    public void DeserializeTrack_ExplicitPlatform_WinsOverFallback()
    {
        var element = Parse("""{"platform":"kugou","title":"x"}""");

        var track = SnapshotParser.DeserializeTrack(element, "netease");

        Assert.Equal("kugou", track!.Platform);
    }

    [Fact]
    public void DeserializeTrack_EmptyPlatform_FallsBackToConnectorKey()
    {
        var element = Parse("""{"platform":"","title":"x"}""");

        var track = SnapshotParser.DeserializeTrack(element, "folia");

        Assert.Equal("folia", track!.Platform);
    }

    [Fact]
    public void Parse_UpstreamProbeShape_MapsConnectedAndVersion()
    {
        // 上游 netease probe 的真实形状（2026-09-18 实测抓取）
        var element = Parse("""
        {"connected":false,"player":"网易云音乐","processId":null,"version":"",
         "status":"未连接：没有发现网易云原生播放器窗口","current":null,
         "observedAt":"2026-09-18T14:56:00.1847068+08:00","next":null,
         "nextSource":"","nextObservation":null}
        """);

        var snapshot = SnapshotParser.Parse(element, "netease");

        Assert.False(snapshot.Connected);
        Assert.Null(snapshot.Current);
        // 空字符串 nextSource 归一为 Unknown（不抛、不误判）
        Assert.Equal(NextObservation.Unknown, snapshot.NextObservation);
    }

    // ---------- 非数字字段的容错：真实上游会显式发 null ----------
    //
    // 这一组补的是 P7 端到端验收暴露出的缺陷：
    // JsonElement.TryGetInt32 / TryGetDouble 在元素**不是 Number**（null / String / True）时
    // 抛 InvalidOperationException，而不是返回 false；只有"是 Number 但非整数（1.5）
    // 或超出目标类型范围"才返回 false。于是 `TryGetProperty(p, out v) && v.TryGetInt32(out n)`
    // 这个看起来安全的写法，在"属性存在但值为 null"时会直接打崩调用方。
    //
    // 为什么原来测不出来：夹具里的数字永远合法。netease 的 ping 恰好**整个字段缺席**
    // （缺属性时 TryGetProperty 返回 false，&& 短路后安全），而 kugou 显式发
    // `"eventProtocolVersion": null`——只有真实上游能造出这个形状。

    /// <summary>上游 kugou 连接器 ping 的真实响应（2026-09-18 实测抓取，逐字）。</summary>
    /// <remarks>
    /// 与 netease 的两处差异正是缺陷的触发条件：<c>eventProtocolVersion</c> 是 <c>null</c>
    /// （netease 是整个字段缺席）、<c>features</c> 为空数组且 <c>pause</c>/<c>resume</c> 为 false。
    /// </remarks>
    private const string UpstreamKugouPingResult = """
    {
      "protocolVersion": 1,
      "eventProtocolVersion": null,
      "connectorId": "kugou",
      "connectorVersion": "20.1.41.1",
      "capabilities": {
        "search": true,
        "playSelected": true,
        "previous": true,
        "pause": false,
        "resume": false,
        "toggle": true,
        "next": true,
        "insertNext": true,
        "insertNextLevel": "原生插入 + 上一首重置锚点的有界兜底"
      },
      "features": []
    }
    """;

    [Fact]
    public void ParsePing_EventProtocolVersionNull_TreatedAsZero_NotThrow()
    {
        var ping = ConnectorProtocol.ParsePing(Parse(UpstreamKugouPingResult));

        Assert.Equal(1, ping.ProtocolVersion);
        // null 归一为 0（"未声明"），关键是**不得抛异常**
        Assert.Equal(0, ping.EventProtocolVersion);
        Assert.Equal("kugou", ping.ConnectorId);
        // kugou 不声明 snapshot-events-v1，但 insertNext 仍应被读到
        Assert.False(ping.HasFeature(ConnectorProtocol.FeatureSnapshotEvents));
        Assert.True(ping.Capabilities.InsertNext);
        Assert.False(ping.Capabilities.Pause);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"12\"")]
    [InlineData("true")]
    [InlineData("1.5")]
    [InlineData("99999999999")]
    public void ReadInt32_NonIntegerOrNonNumber_ReturnsNull(string json)
    {
        var element = Parse($$"""{"v":{{json}}}""");

        Assert.Null(ConnectorProtocol.ReadInt32(element, "v"));
    }

    [Fact]
    public void ReadInt32_Integer_ReturnsValue_AndMissingPropertyReturnsNull()
    {
        var element = Parse("""{"v":42}""");

        Assert.Equal(42, ConnectorProtocol.ReadInt32(element, "v"));
        Assert.Null(ConnectorProtocol.ReadInt32(element, "absent"));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"3.5\"")]
    [InlineData("true")]
    [InlineData("1e400")] // TryGetDouble 会静默读成 +∞，必须被拒
    public void ReadDouble_NonNumberOrNonFinite_ReturnsNull(string json)
    {
        var element = Parse($$"""{"v":{{json}}}""");

        Assert.Null(ConnectorProtocol.ReadDouble(element, "v"));
    }

    [Fact]
    public void Parse_ProgressSecondsNull_DoesNotThrow()
    {
        var element = Parse("""{"connected":true,"progressSeconds":null,"version":"1.0"}""");

        var snapshot = SnapshotParser.Parse(element, "kugou");

        Assert.True(snapshot.Connected);
        Assert.Null(snapshot.ProgressSeconds);
    }

    [Fact]
    public void DeserializeTrack_DurationSecondsNull_DoesNotThrow()
    {
        var element = Parse("""{"id":"1","title":"t","durationSeconds":null}""");

        var track = SnapshotParser.DeserializeTrack(element, "kugou");

        Assert.Null(track!.DurationSeconds);
    }
}
