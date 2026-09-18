using Erbai.Connectors.Management.Catalog;

namespace Erbai.Connectors.Management.Tests;

/// <summary>
/// 清单解析与校验（<see cref="ConnectorCatalogClient.Parse"/>）的行为锁定。
/// </summary>
/// <remarks>
/// 全部用例都以 <b>2026-09-18 抓取的真实上游清单</b>为基准做定点变异——
/// 比手写一个"像清单"的 JSON 更能反映真实字段形态（含顶层多余字段、4 个平台各自的
/// rid 与版本号位数差异）。
/// </remarks>
public class ConnectorCatalogValidatorTests
{
    /// <summary>真实 v2 清单原文（https://app.enkianss.us/connectors/v2/catalog.json）。</summary>
    private const string RealCatalog = """
        {
          "schemaVersion": 2,
          "generatedAt": "2026-09-09T15:57:05.670Z",
          "repository": "Enkianssus/awoo-connectors",
          "publicKeyId": "bilincm-connectors-2026-01",
          "connectors": {
            "netease": {
              "id": "netease",
              "name": "网易云音乐",
              "channel": "stable",
              "version": "3.1.38.205386.1",
              "protocolVersion": 1,
              "minimumCoreVersion": "1.1.10",
              "playerVersionPolicy": "3.1.*",
              "testedPlayerVersion": "3.1.38.205386",
              "publishedAt": "2026-08-23T15:48:53.446Z",
              "package": {
                "deployment": "framework-dependent",
                "runtime": "win-x64",
                "runtimeChannel": "8.0",
                "asset": "awoo-connector-netease-3.1.38.205386.1-win-x64-framework-dependent.zip",
                "size": 6893678,
                "sha256": "6cf83940fc93c69f9af4b97717095e2ef26c117ac4a6e57939f9bab5f4f22cfb",
                "signature": "vF1Zd+RDePv18cazRL3qYuuvLq/Qf4hCFHaT7kZAHpFFe0DjeIXTSddwxNta1bOiLnCjgkvrnpRxKudWr7QZDw==",
                "downloadUrl": "https://app.enkianss.us/connectors/v2/download/netease/3.1.38.205386.1/awoo-connector-netease-3.1.38.205386.1-win-x64-framework-dependent.zip"
              }
            },
            "kugou": {
              "id": "kugou",
              "name": "酷狗音乐",
              "channel": "stable",
              "version": "20.1.41.1",
              "protocolVersion": 1,
              "minimumCoreVersion": "1.1.10",
              "playerVersionPolicy": "20.*",
              "testedPlayerVersion": "20.1.41.27870",
              "publishedAt": "2026-08-22T15:10:30.134Z",
              "package": {
                "deployment": "framework-dependent",
                "runtime": "win-x86",
                "runtimeChannel": "8.0",
                "asset": "awoo-connector-kugou-20.1.41.1-win-x86-framework-dependent.zip",
                "size": 6783403,
                "sha256": "bc8dc38a104b586c6a1d2aeb6acddf3c501380562872c0cd6eb16af6f6b1ceba",
                "signature": "jnkAyNYediK1wit0sBmQGr1S8Wpm2tOuse6FD3I4KD+8YMa9X03nUiUkSm9HpUoO7VfagV/Yt2X28qIfMyCHBQ==",
                "downloadUrl": "https://app.enkianss.us/connectors/v2/download/kugou/20.1.41.1/awoo-connector-kugou-20.1.41.1-win-x86-framework-dependent.zip"
              }
            },
            "qqmusic": {
              "id": "qqmusic",
              "name": "QQ音乐",
              "channel": "stable",
              "version": "22.61.2",
              "protocolVersion": 1,
              "minimumCoreVersion": "1.2.1",
              "playerVersionPolicy": "22.*",
              "testedPlayerVersion": "22.61",
              "publishedAt": "2026-09-09T15:57:05.670Z",
              "package": {
                "deployment": "framework-dependent",
                "runtime": "win-x86",
                "runtimeChannel": "8.0",
                "asset": "awoo-connector-qqmusic-22.61.2-win-x86-framework-dependent.zip",
                "size": 7231685,
                "sha256": "bc17c2b512895f2c75892268ca0ea83266bca4e0dcc4dcf0a5e82dbe38d4eb56",
                "signature": "nBm1BcibEFn4qJ+4DKN3SELVB6dSSJFm9zOhU9/2jAXAuB2KwMrUsPxX+hHrlowJ48Lnn5jePGEvZ49enMZKAg==",
                "downloadUrl": "https://app.enkianss.us/connectors/v2/download/qqmusic/22.61.2/awoo-connector-qqmusic-22.61.2-win-x86-framework-dependent.zip"
              }
            },
            "folia": {
              "id": "folia",
              "name": "Folia",
              "channel": "stable",
              "version": "1.1.3",
              "protocolVersion": 1,
              "minimumCoreVersion": "1.1.10",
              "playerVersionPolicy": "Stage API",
              "testedPlayerVersion": "Stage API",
              "publishedAt": "2026-08-22T15:10:30.134Z",
              "package": {
                "deployment": "framework-dependent",
                "runtime": "win-x86",
                "runtimeChannel": "8.0",
                "asset": "awoo-connector-folia-1.1.3-win-x86-framework-dependent.zip",
                "size": 6714787,
                "sha256": "036470b9fc10b60ad9bd6a45ea98ed9923bc4dd9e5c4e7db59d110f9c599fb1c",
                "signature": "LFPNcvgwlNiB/IniIzjX2uxORFgNwge/4D7lu1j41SiomgsBETqF67q2xf4LJjvNkLZsYFNbjCct6pxw/Y0kBw==",
                "downloadUrl": "https://app.enkianss.us/connectors/v2/download/folia/1.1.3/awoo-connector-folia-1.1.3-win-x86-framework-dependent.zip"
              }
            }
          }
        }
        """;

    [Fact]
    public void Parse_AcceptsRealUpstreamCatalog()
    {
        IReadOnlyList<ConnectorCatalogEntry> entries = ConnectorCatalogClient.Parse(RealCatalog);

        Assert.Equal(4, entries.Count);
        Assert.Equal(
            ["folia", "kugou", "netease", "qqmusic"],
            entries.Select(e => e.Id).OrderBy(id => id, StringComparer.Ordinal));

        ConnectorCatalogEntry netease = entries.Single(e => e.Id == "netease");
        Assert.Equal("3.1.38.205386.1", netease.Version);
        Assert.Equal("win-x64", netease.Package!.Runtime);
        Assert.Equal(6_893_678, netease.Package.Size);

        // minimumCoreVersion 是发布方自家应用的版本口径，只应被"带出来展示"，不参与门控。
        Assert.Equal("1.1.10", netease.MinimumCoreVersion);
        Assert.Equal("1.2.1", entries.Single(e => e.Id == "qqmusic").MinimumCoreVersion);
    }

    /// <summary>
    /// 顶层出现模型里没有的字段（真实清单就有 <c>generatedAt</c> / <c>repository</c>）
    /// 不应导致解析失败——上游加字段是常态。
    /// </summary>
    [Fact]
    public void Parse_ToleratesUnknownFields()
    {
        IReadOnlyList<ConnectorCatalogEntry> entries = ConnectorCatalogClient.Parse(RealCatalog);

        Assert.NotEmpty(entries);
    }

    /// <summary>
    /// 上游新增平台时不应让整份清单失效：不认识的 key 跳过，认识的照常可用。
    /// 这不削弱安全性——不认识的 key 永远不会被安装。
    /// </summary>
    [Fact]
    public void Parse_IgnoresUnknownConnectorKey()
    {
        const string json = """
            {
              "schemaVersion": 2,
              "publicKeyId": "bilincm-connectors-2026-01",
              "connectors": {
                "spotify": { "id": "spotify", "version": "1.0.0", "protocolVersion": 1 },
                "netease": {
                  "id": "netease",
                  "version": "3.1.38.205386.1",
                  "protocolVersion": 1,
                  "package": {
                    "deployment": "framework-dependent",
                    "runtime": "win-x64",
                    "asset": "awoo-connector-netease-3.1.38.205386.1-win-x64-framework-dependent.zip",
                    "size": 6893678,
                    "sha256": "6cf83940fc93c69f9af4b97717095e2ef26c117ac4a6e57939f9bab5f4f22cfb",
                    "signature": "vF1Zd+RDePv18cazRL3qYuuvLq/Qf4hCFHaT7kZAHpFFe0DjeIXTSddwxNta1bOiLnCjgkvrnpRxKudWr7QZDw==",
                    "downloadUrl": "https://app.enkianss.us/connectors/v2/download/netease/3.1.38.205386.1/awoo-connector-netease-3.1.38.205386.1-win-x64-framework-dependent.zip"
                  }
                }
              }
            }
            """;

        IReadOnlyList<ConnectorCatalogEntry> entries = ConnectorCatalogClient.Parse(json);

        Assert.Single(entries);
        Assert.Equal("netease", entries[0].Id);
    }

    /// <summary>
    /// 清单级问题必须致命：拿到的根本不是本应用该用的清单，逐条跳过没有意义。
    /// </summary>
    [Theory]
    [InlineData("\"schemaVersion\": 2", "\"schemaVersion\": 1", "版本不兼容")]
    [InlineData("\"schemaVersion\": 2", "\"schemaVersion\": 3", "版本不兼容")]
    [InlineData(
        "\"publicKeyId\": \"bilincm-connectors-2026-01\"",
        "\"publicKeyId\": \"attacker-key-2026\"",
        "签名密钥标识不匹配")]
    public void Parse_RejectsCatalogLevelTampering(string from, string to, string expectedMessageFragment)
    {
        string tampered = RealCatalog.Replace(from, to, StringComparison.Ordinal);
        Assert.NotEqual(RealCatalog, tampered);

        ConnectorManagementException ex =
            Assert.Throws<ConnectorManagementException>(() => ConnectorCatalogClient.Parse(tampered));

        Assert.Contains(expectedMessageFragment, ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// 条目级问题只拒绝该条目，其余平台照旧可用。
    /// </summary>
    /// <remarks>
    /// 这是相对早期实现的一次<b>有意放宽</b>：原先任一条目不合法都会让整份清单失败。
    /// 放宽的理由是——清单条目属于非信任元数据（清单本身没有签名，真正的信任锚是每个发布包的
    /// Ed25519 签名），因此一个条目写坏或被人改过，既不能削弱安全性，也不该让其余三个平台
    /// 一起不可用。四个平台由同一上游发布但版本推进不保证同步，这一点是现实风险而非假设。
    /// </remarks>
    [Theory]
    [InlineData("\"id\": \"netease\"", "\"id\": \"netease-x\"", "id 与键名不一致")]
    [InlineData("\"version\": \"3.1.38.205386.1\"", "\"version\": \"3.1\"", "版本号不合法")]
    [InlineData("\"version\": \"3.1.38.205386.1\"", "\"version\": \"3.1.38.205386.1.7.9\"", "版本号不合法")]
    [InlineData("\"protocolVersion\": 1", "\"protocolVersion\": 2", "协议版本不兼容")]
    [InlineData("\"deployment\": \"framework-dependent\"", "\"deployment\": \"weird\"", "部署方式不支持")]
    [InlineData("\"runtime\": \"win-x64\"", "\"runtime\": \"linux-x64\"", "运行时标识不支持")]
    [InlineData("\"size\": 6893678", "\"size\": 0", "归档大小超出允许范围")]
    [InlineData("\"size\": 6893678", "\"size\": 999999999999", "归档大小超出允许范围")]
    [InlineData(
        "\"sha256\": \"6cf83940fc93c69f9af4b97717095e2ef26c117ac4a6e57939f9bab5f4f22cfb\"",
        "\"sha256\": \"6cf83940fc93c69f\"",
        "sha256 字段不合法")]
    [InlineData(
        "\"signature\": \"vF1Zd+RDePv18cazRL3qYuuvLq/Qf4hCFHaT7kZAHpFFe0DjeIXTSddwxNta1bOiLnCjgkvrnpRxKudWr7QZDw==\"",
        "\"signature\": \"c2hvcnQ=\"",
        "64 字节 base64")]
    [InlineData(
        "\"downloadUrl\": \"https://app.enkianss.us/connectors/v2/download/netease/3.1.38.205386.1/awoo-connector-netease-3.1.38.205386.1-win-x64-framework-dependent.zip\"",
        "\"downloadUrl\": \"http://app.enkianss.us/connectors/v2/download/netease/3.1.38.205386.1/awoo-connector-netease-3.1.38.205386.1-win-x64-framework-dependent.zip\"",
        "必须使用 HTTPS")]
    [InlineData(
        "\"downloadUrl\": \"https://app.enkianss.us/connectors/v2/download/netease/3.1.38.205386.1/awoo-connector-netease-3.1.38.205386.1-win-x64-framework-dependent.zip\"",
        "\"downloadUrl\": \"https://evil.example.com/connectors/v2/download/netease/3.1.38.205386.1/awoo-connector-netease-3.1.38.205386.1-win-x64-framework-dependent.zip\"",
        "不在白名单内")]
    [InlineData(
        "\"downloadUrl\": \"https://app.enkianss.us/connectors/v2/download/netease/3.1.38.205386.1/awoo-connector-netease-3.1.38.205386.1-win-x64-framework-dependent.zip\"",
        "\"downloadUrl\": \"https://app.enkianss.us/connectors/v2/download/netease/3.1.38.205386.1/other-payload.zip\"",
        "基名与资产名不一致")]
    [InlineData(
        "\"asset\": \"awoo-connector-netease-3.1.38.205386.1-win-x64-framework-dependent.zip\"",
        "\"asset\": \"awoo-connector-netease-9.9.9-win-x64-framework-dependent.zip\"",
        "资产名不符合规范")]
    [InlineData(
        "\"asset\": \"awoo-connector-netease-3.1.38.205386.1-win-x64-framework-dependent.zip\"",
        "\"asset\": \"awoo-connector-netease-3.1.38.205386.1-win-x86-framework-dependent.zip\"",
        "资产名不符合规范")]
    public void Parse_RejectsTamperedEntryWithoutFailingOtherPlayers(
        string from,
        string to,
        string expectedMessageFragment)
    {
        // 替换必须限定在 netease 条目内部。像 "protocolVersion": 1 这种串四个平台各出现一次，
        // 全局替换会把四条全弄坏，测出来的是"整份失败"而不是"逐条拒绝"。
        string tampered = ReplaceInsideEntry(RealCatalog, "netease", from, to);
        Assert.NotEqual(RealCatalog, tampered);

        ConnectorCatalogSnapshot snapshot = ConnectorCatalogClient.ParseWithRejections(tampered);

        Assert.DoesNotContain(snapshot.Entries, entry => entry.Id == "netease");

        string? reason = snapshot.FindRejection("netease");
        Assert.NotNull(reason);
        Assert.Contains(expectedMessageFragment, reason, StringComparison.Ordinal);

        // 其余三个平台必须照旧可用。
        Assert.Equal(3, snapshot.Entries.Count);
        Assert.Contains(snapshot.Entries, entry => entry.Id == "kugou");
        Assert.Contains(snapshot.Entries, entry => entry.Id == "qqmusic");
        Assert.Contains(snapshot.Entries, entry => entry.Id == "folia");
    }

    /// <summary>只在该条目自身的 JSON 片段内做替换。</summary>
    private static string ReplaceInsideEntry(string catalog, string playerKey, string from, string to)
    {
        int start = catalog.IndexOf($"\"id\": \"{playerKey}\"", StringComparison.Ordinal);
        Assert.True(start >= 0, $"夹具里找不到 {playerKey} 条目。");

        int next = catalog.IndexOf("\"id\": \"", start + 1, StringComparison.Ordinal);
        int end = next < 0 ? catalog.Length : next;

        string replaced = catalog[start..end].Replace(from, to, StringComparison.Ordinal);

        return string.Concat(catalog.AsSpan(0, start), replaced, catalog.AsSpan(end));
    }

    /// <summary>
    /// 全部条目都不合法时才允许整份失败——否则用户会拿到一份"看似正常但什么都装不了"的清单。
    /// </summary>
    [Fact]
    public void Parse_FailsOnlyWhenEverySupportedEntryIsRejected()
    {
        string tampered = RealCatalog.Replace(
            "\"protocolVersion\": 1",
            "\"protocolVersion\": 2",
            StringComparison.Ordinal);

        Assert.NotEqual(RealCatalog, tampered);

        ConnectorManagementException ex = Assert.Throws<ConnectorManagementException>(
            () => ConnectorCatalogClient.Parse(tampered));

        Assert.Contains("协议版本不兼容", ex.Message, StringComparison.Ordinal);
        Assert.Contains("全部不合法", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>另一个平台的条目被拒，也不该影响 netease。</summary>
    [Fact]
    public void Parse_SkipsIncompatibleEntryWithoutFailingTheCatalog()
    {
        string mutated = ReplaceInsideEntry(
            RealCatalog,
            "folia",
            "\"protocolVersion\": 1",
            "\"protocolVersion\": 2");

        ConnectorCatalogSnapshot snapshot = ConnectorCatalogClient.ParseWithRejections(mutated);

        Assert.DoesNotContain(snapshot.Entries, entry => entry.Id == "folia");
        Assert.Contains("协议版本不兼容", snapshot.FindRejection("folia"), StringComparison.Ordinal);
        Assert.Equal(3, snapshot.Entries.Count);
        Assert.Contains(snapshot.Entries, entry => entry.Id == "netease");
    }

    [Fact]
    public void Parse_RejectsMissingPackage()
    {
        const string json = """
            {
              "schemaVersion": 2,
              "publicKeyId": "bilincm-connectors-2026-01",
              "connectors": {
                "netease": { "id": "netease", "version": "3.1.38.205386.1", "protocolVersion": 1 }
              }
            }
            """;

        ConnectorManagementException ex =
            Assert.Throws<ConnectorManagementException>(() => ConnectorCatalogClient.Parse(json));

        Assert.Contains("缺少 package", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RejectsEmptyConnectorMap()
    {
        const string json = """
            {
              "schemaVersion": 2,
              "publicKeyId": "bilincm-connectors-2026-01",
              "connectors": {}
            }
            """;

        Assert.Throws<ConnectorManagementException>(() => ConnectorCatalogClient.Parse(json));
    }

    [Fact]
    public void Parse_RejectsMalformedJson()
    {
        Assert.Throws<ConnectorManagementException>(
            () => ConnectorCatalogClient.Parse("{ not json"));
    }

    /// <summary>真实清单里 4 个平台的 rid 分配必须与实测一致（F6）。</summary>
    [Fact]
    public void Parse_ReflectsRealRuntimeSplit()
    {
        Dictionary<string, string> runtimes = ConnectorCatalogClient.Parse(RealCatalog)
            .ToDictionary(e => e.Id!, e => e.Package!.Runtime!, StringComparer.Ordinal);

        Assert.Equal("win-x64", runtimes["netease"]);
        Assert.Equal("win-x86", runtimes["kugou"]);
        Assert.Equal("win-x86", runtimes["qqmusic"]);
        Assert.Equal("win-x86", runtimes["folia"]);
    }
}
