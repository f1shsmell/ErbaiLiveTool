using Erbai.Connectors.Management.Versioning;

namespace Erbai.Connectors.Management.Tests;

/// <summary>
/// <see cref="ConnectorVersionPolicy"/> 的分类口径。
/// </summary>
/// <remarks>
/// 版本号取自 2026-09-18 实抓的上游 v2 清单：netease <c>3.1.38.205386.1</c>、
/// kugou <c>20.1.41.1</c>、qqmusic <c>22.61.2</c>、folia <c>1.1.3</c>。
/// 这四个形态覆盖了两条互不相同的语义路径，是本文件所有用例的取材来源——
/// 不编造版本号，否则测的是我自己想象的口径。
/// </remarks>
public class ConnectorVersionPolicyTests
{
    // ---------------------------------------------------------------------------------
    // netease：5 段，分支 = 前 4 段，末段是分支内补丁
    // ---------------------------------------------------------------------------------

    [Theory]
    [InlineData("3.1.38.205386.1", "3.1.38.205386.2", ConnectorUpdateKind.Patch)]
    [InlineData("3.1.38.205386.1", "3.1.38.205386.1", ConnectorUpdateKind.None)]
    [InlineData("3.1.38.205386.2", "3.1.38.205386.1", ConnectorUpdateKind.None)]
    [InlineData("3.1.38.205386.1", "3.1.38.205387.1", ConnectorUpdateKind.Player)]
    [InlineData("3.1.38.205387.1", "3.1.38.205386.1", ConnectorUpdateKind.None)]
    [InlineData("3.1.38.205386.1", "3.2.0.0.1", ConnectorUpdateKind.Player)]
    public void Classify_NetEaseBranchSemantics(
        string current,
        string latest,
        ConnectorUpdateKind expected)
    {
        Assert.Equal(expected, ConnectorVersionPolicy.Classify(current, latest, "netease"));
    }

    /// <summary>
    /// player-scoped 路径<b>永不</b>返回 <see cref="ConnectorUpdateKind.Major"/>：
    /// 对 netease 来说首段只是分支的第一个分量，不是"主版本"。看着像大版本跳跃，
    /// 实际仍按"换播放器分支"处理。这条反直觉但必须钉住，否则以后很容易被"优化"错。
    /// </summary>
    [Theory]
    [InlineData("3.1.38.205386.1", "4.0.0.0.1")]
    [InlineData("20.1.41.1", "21.0.0.1")]
    public void Classify_PlayerScopedNeverReportsMajor(string current, string latest)
    {
        Assert.Equal(ConnectorUpdateKind.Player, ConnectorVersionPolicy.Classify(current, latest));
        Assert.NotEqual(ConnectorUpdateKind.Major, ConnectorVersionPolicy.Classify(current, latest));
    }

    // ---------------------------------------------------------------------------------
    // kugou：4 段，分支 = 前 3 段
    // ---------------------------------------------------------------------------------

    [Theory]
    [InlineData("20.1.41.1", "20.1.41.2", ConnectorUpdateKind.Patch)]
    [InlineData("20.1.41.1", "20.1.42.1", ConnectorUpdateKind.Player)]
    [InlineData("20.1.41.1", "20.1.41.1", ConnectorUpdateKind.None)]
    public void Classify_KugouBranchSemantics(
        string current,
        string latest,
        ConnectorUpdateKind expected)
    {
        Assert.Equal(expected, ConnectorVersionPolicy.Classify(current, latest, "kugou"));
    }

    // ---------------------------------------------------------------------------------
    // qqmusic：3 段但首段 ≥ 10 → 走 player-scoped，分支 = 前 2 段
    // ---------------------------------------------------------------------------------

    [Theory]
    [InlineData("22.61.2", "22.61.3", ConnectorUpdateKind.Patch)]
    [InlineData("22.61.2", "22.62.1", ConnectorUpdateKind.Player)]
    [InlineData("22.61.2", "23.0.1", ConnectorUpdateKind.Player)]
    [InlineData("22.61.2", "22.61.2", ConnectorUpdateKind.None)]
    [InlineData("22.61.2", "9.0.1", ConnectorUpdateKind.None)]
    public void Classify_QQMusicUsesPlayerScopedDespiteThreeParts(
        string current,
        string latest,
        ConnectorUpdateKind expected)
    {
        Assert.Equal(expected, ConnectorVersionPolicy.Classify(current, latest, "qqmusic"));
    }

    /// <summary>
    /// 同样是 3 段，folia 却走 legacy 语义——所以 <c>[主版本, 播放器, 补丁]</c> 里的
    /// <c>1.1.3 → 2.0.0</c> 是 <see cref="ConnectorUpdateKind.Major"/>，
    /// 而 qqmusic 的 <c>22.61.2 → 23.0.1</c> 是 <see cref="ConnectorUpdateKind.Player"/>。
    /// 唯一的区分依据是"首段 ≥ 10"，这是上游既成事实。
    /// </summary>
    [Fact]
    public void Classify_SameThreePartShapeClassifiesDifferentlyByPlayerKey()
    {
        Assert.Equal(
            ConnectorUpdateKind.Major,
            ConnectorVersionPolicy.Classify("1.1.3", "2.0.0", "folia"));

        Assert.Equal(
            ConnectorUpdateKind.Player,
            ConnectorVersionPolicy.Classify("22.61.2", "23.0.1", "qqmusic"));
    }

    /// <summary>不带 playerKey 时，3 段一律按 legacy 处理。</summary>
    [Fact]
    public void Classify_WithoutPlayerKeyTreatsThreePartsAsLegacy()
    {
        Assert.Equal(ConnectorUpdateKind.Major, ConnectorVersionPolicy.Classify("22.61.2", "23.0.1"));
    }

    // ---------------------------------------------------------------------------------
    // folia：3 段 legacy
    // ---------------------------------------------------------------------------------

    [Theory]
    [InlineData("1.1.3", "1.1.4", ConnectorUpdateKind.Patch)]
    [InlineData("1.1.3", "1.2.0", ConnectorUpdateKind.Player)]
    [InlineData("1.1.3", "2.0.0", ConnectorUpdateKind.Major)]
    [InlineData("1.1.3", "1.1.3", ConnectorUpdateKind.None)]
    [InlineData("2.0.0", "1.9.9", ConnectorUpdateKind.None)]
    [InlineData("1.2.0", "1.1.9", ConnectorUpdateKind.None)]
    public void Classify_FoliaLegacySemantics(
        string current,
        string latest,
        ConnectorUpdateKind expected)
    {
        Assert.Equal(expected, ConnectorVersionPolicy.Classify(current, latest, "folia"));
    }

    // ---------------------------------------------------------------------------------
    // 安装 / 无法解析
    // ---------------------------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Classify_MissingCurrentVersionIsAnInstall(string? current)
    {
        Assert.Equal(
            ConnectorUpdateKind.Install,
            ConnectorVersionPolicy.Classify(current, "3.1.38.205386.1", "netease"));
    }

    /// <summary>
    /// 本地有版本号但解析不出来时，<b>不能</b>当成"未安装"去覆盖——那会把一个我们看不懂的
    /// 已装版本直接冲掉。宁可什么都不做。
    /// </summary>
    [Theory]
    [InlineData("garbage")]
    [InlineData("1.0")]
    [InlineData("1.0.0.0.0.0")]
    [InlineData("v1.0.0")]
    [InlineData("1.0.0-beta")]
    public void Classify_UnparsableCurrentVersionIsNotAnInstall(string current)
    {
        Assert.Equal(
            ConnectorUpdateKind.None,
            ConnectorVersionPolicy.Classify(current, "3.1.38.205386.1", "netease"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("1.0")]
    [InlineData("1.0.0.0.0.0")]
    [InlineData("1.0.0.9007199254740992")]
    public void Classify_UnparsableLatestVersionYieldsNone(string? latest)
    {
        Assert.Equal(
            ConnectorUpdateKind.None,
            ConnectorVersionPolicy.Classify("1.0.0", latest, "netease"));
    }

    /// <summary>上界与 JS 的 <c>Number.MAX_SAFE_INTEGER</c> 对齐，边界值本身合法。</summary>
    /// <remarks>
    /// 注意正则先要求 3–5 段，所以单段的大数字根本走不到安全整数判定——
    /// 这里必须用 3 段形态才能真的测到那个上界。
    /// </remarks>
    [Theory]
    [InlineData("1.0.9007199254740991", true)]
    [InlineData("1.0.9007199254740992", false)]
    [InlineData("9007199254740991", false)]
    [InlineData("01.02.03", true)]
    [InlineData("3.1.38.205386.1", true)]
    public void TryParse_MatchesJavaScriptSafeIntegerBounds(string value, bool expected)
    {
        Assert.Equal(expected, ConnectorVersionPolicy.TryParse(value, out _));
    }

    /// <summary>从上游早期 1.x.y 命名迁移过来时按补丁处理，否则用户永远升不上去。</summary>
    /// <remarks>
    /// 判据是"首段为 1 <b>且</b>段数为 3 或与远端不同"。因此 <c>1.9.9.9.9</c> 这种
    /// 段数恰好与远端相同的 1 开头版本<b>不</b>算旧命名，会按普通分支比较走。
    /// 这是参考实现的既有口径，照搬不"优化"。
    /// </remarks>
    [Theory]
    [InlineData("1.2.3")]
    [InlineData("1.2.3.4")]
    public void Classify_MigratingFromLegacyOneDotNamingIsAPatch(string current)
    {
        Assert.Equal(
            ConnectorUpdateKind.Patch,
            ConnectorVersionPolicy.Classify(current, "3.1.38.205386.1", "netease"));
    }

    /// <summary>段数与远端相同、且首段不是 1 的旧命名 → 按普通分支比较（这里是换分支）。</summary>
    [Fact]
    public void Classify_LegacyLookingVersionWithMatchingSegmentCountIsNotTreatedAsLegacy()
    {
        Assert.Equal(
            ConnectorUpdateKind.Player,
            ConnectorVersionPolicy.Classify("1.9.9.9.9", "3.1.38.205386.1", "netease"));
    }

    /// <summary>
    /// 段数不同、又不是可识别的旧命名 → 两个版本不可比，不做任何事。
    /// 这里 <c>2.2.3</c> 的首段不是 1，所以走不到"旧命名"分支。
    /// </summary>
    [Fact]
    public void Classify_DifferentSegmentCountIsNotComparable()
    {
        Assert.Equal(
            ConnectorUpdateKind.None,
            ConnectorVersionPolicy.Classify("2.2.3", "3.1.38.205386.1", "netease"));
    }

    // ---------------------------------------------------------------------------------
    // 自动 / 手动边界（决策 D5）
    // ---------------------------------------------------------------------------------

    [Theory]
    [InlineData(null, "3.1.38.205386.1", "netease", true)]
    [InlineData("3.1.38.205386.1", "3.1.38.205386.2", "netease", true)]
    [InlineData("3.1.38.205386.1", "3.1.38.205387.1", "netease", false)]
    [InlineData("1.1.3", "1.2.0", "folia", false)]
    [InlineData("1.1.3", "2.0.0", "folia", false)]
    [InlineData("3.1.38.205386.1", "3.1.38.205386.1", "netease", false)]
    public void CanAutoUpdate_OnlyAllowsInstallAndPatch(
        string? current,
        string latest,
        string playerKey,
        bool expected)
    {
        Assert.Equal(expected, ConnectorVersionPolicy.CanAutoUpdate(current, latest, playerKey));
    }

    [Theory]
    [InlineData(null, "3.1.38.205386.1", "netease", false)]
    [InlineData("3.1.38.205386.1", "3.1.38.205386.2", "netease", false)]
    [InlineData("3.1.38.205386.1", "3.1.38.205387.1", "netease", true)]
    [InlineData("1.1.3", "1.2.0", "folia", true)]
    [InlineData("1.1.3", "2.0.0", "folia", true)]
    [InlineData("1.1.3", "1.1.3", "folia", false)]
    public void RequiresManualUpdate_CoversPlayerAndMajor(
        string? current,
        string latest,
        string playerKey,
        bool expected)
    {
        Assert.Equal(expected, ConnectorVersionPolicy.RequiresManualUpdate(current, latest, playerKey));
    }

    /// <summary>自动与手动必须互斥且覆盖全部"有更新"的情形。</summary>
    [Theory]
    [InlineData(null, "3.1.38.205386.1", "netease")]
    [InlineData("3.1.38.205386.1", "3.1.38.205386.2", "netease")]
    [InlineData("3.1.38.205386.1", "3.1.38.205387.1", "netease")]
    [InlineData("1.1.3", "1.2.0", "folia")]
    [InlineData("1.1.3", "2.0.0", "folia")]
    public void AutoAndManualAreMutuallyExclusive(string? current, string latest, string playerKey)
    {
        bool auto = ConnectorVersionPolicy.CanAutoUpdate(current, latest, playerKey);
        bool manual = ConnectorVersionPolicy.RequiresManualUpdate(current, latest, playerKey);

        Assert.NotEqual(auto, manual);
        Assert.NotEqual(ConnectorUpdateKind.None, ConnectorVersionPolicy.Classify(current, latest, playerKey));
    }

    /// <summary>没有更新时两者都必须为假。</summary>
    [Theory]
    [InlineData("3.1.38.205386.1", "3.1.38.205386.1")]
    [InlineData("3.1.38.205387.1", "3.1.38.205386.1")]
    [InlineData("garbage", "3.1.38.205386.1")]
    [InlineData("1.1.3", null)]
    public void NoUpdateMeansNeitherAutoNorManual(string? current, string? latest)
    {
        Assert.False(ConnectorVersionPolicy.CanAutoUpdate(current, latest, "netease"));
        Assert.False(ConnectorVersionPolicy.RequiresManualUpdate(current, latest, "netease"));
    }
}
