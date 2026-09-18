using System.Globalization;
using System.Text.RegularExpressions;

namespace Erbai.Connectors.Management.Versioning;

/// <summary>
/// 连接器版本推进的性质。决定这次更新能不能自动应用。
/// </summary>
/// <remarks>
/// 分类口径整体照搬参考实现 <c>electron/connector-version-policy.ts</c>。之所以不能简单地
/// "数字大就更新"，是因为上游的版本号不是单一递增序列，而是
/// <c>&lt;播放器版本分支&gt;.&lt;补丁&gt;</c>——换了分支意味着连接器是针对<b>另一个播放器版本</b>
/// 适配的，盲目替换会让用户原本能用的播放器失效。
/// </remarks>
public enum ConnectorUpdateKind
{
    /// <summary>没有可应用的更新（含"远端版本比本地旧"和"版本号无法解析"）。</summary>
    None = 0,

    /// <summary>本地未安装，属于首次安装。</summary>
    Install,

    /// <summary>同一播放器分支内的补丁推进——可自动应用。</summary>
    Patch,

    /// <summary>换到了另一个播放器版本分支——必须用户确认。</summary>
    Player,

    /// <summary>主版本推进——必须用户确认。</summary>
    Major,
}

/// <summary>
/// 连接器版本号解析与更新性质分类。
/// </summary>
/// <remarks>
/// <para>
/// 版本号形态实测（2026-09-18 抓取上游 v2 清单）：
/// <list type="table">
///   <item><term>netease</term><description><c>3.1.38.205386.1</c>（5 段）</description></item>
///   <item><term>kugou</term><description><c>20.1.41.1</c>（4 段）</description></item>
///   <item><term>qqmusic</term><description><c>22.61.2</c>（3 段，首段 ≥ 10）</description></item>
///   <item><term>folia</term><description><c>1.1.3</c>（3 段，走 legacy 语义）</description></item>
/// </list>
/// </para>
/// <para>
/// 因此存在<b>两条互不相同的语义</b>：
/// <list type="number">
///   <item><description>
///   <b>player-scoped</b>（段数 ≥ 4，或 qqmusic 且 3 段且首段 ≥ 10）：最后一段是分支内补丁，
///   前面所有段构成"播放器分支"。分支长度因此因平台而异——netease 4 段分支、kugou 3 段分支、
///   qqmusic 2 段分支。
///   </description></item>
///   <item><description>
///   <b>legacy 3 段</b>：<c>[主版本, 播放器, 补丁]</c>，主版本变化记作 <see cref="ConnectorUpdateKind.Major"/>。
///   </description></item>
/// </list>
/// </para>
/// <para>
/// qqmusic 的特例是必须的：它 3 段却已经是 player-scoped 语义（首段是播放器大版本 22），
/// 而 folia 同为 3 段却仍是 legacy 语义。两者只能靠"首段 ≥ 10"区分——这不是我们的设计，
/// 是上游既成事实，所以这里逐字沿用参考实现的判据，不"优化"。
/// </para>
/// </remarks>
public static class ConnectorVersionPolicy
{
    /// <summary>版本号合法形态：3–5 段十进制数字。</summary>
    private static readonly Regex VersionPattern =
        new(@"^\d+(?:\.\d+){2,4}$", RegexOptions.CultureInvariant);

    /// <summary>
    /// JavaScript <c>Number.MAX_SAFE_INTEGER</c>。参考实现用 <c>Number.isSafeInteger</c>
    /// 拒绝越界段值，这里用同一个上界以保持判定完全一致。
    /// </summary>
    private const long MaxSafeInteger = 9_007_199_254_740_991L;

    /// <summary>qqmusic 走 player-scoped 语义时首段的下界。</summary>
    private const long QqMusicPlayerScopedMajor = 10;

    /// <summary>
    /// 分类本次版本推进的性质。
    /// </summary>
    /// <param name="currentVersion">本地已安装版本；未安装传 <see langword="null"/> 或空串。</param>
    /// <param name="latestVersion">清单里的最新版本。</param>
    /// <param name="playerKey">平台 key；只有 <c>qqmusic</c> 会影响判定。</param>
    public static ConnectorUpdateKind Classify(
        string? currentVersion,
        string? latestVersion,
        string? playerKey = null)
    {
        if (!TryParse(latestVersion, out long[] latestParts))
        {
            return ConnectorUpdateKind.None;
        }

        if (!TryParse(currentVersion, out long[] currentParts))
        {
            // 本地没有版本号（未安装）→ 安装；有但解析不了 → 什么都不做，
            // 而不是"当成未安装"去覆盖一个我们看不懂的已装版本。
            return string.IsNullOrWhiteSpace(currentVersion)
                ? ConnectorUpdateKind.Install
                : ConnectorUpdateKind.None;
        }

        bool qqPlayerScoped = string.Equals(playerKey, "qqmusic", StringComparison.Ordinal)
            && latestParts.Length == 3
            && latestParts[0] >= QqMusicPlayerScopedMajor;

        if (latestParts.Length >= 4 || qqPlayerScoped)
        {
            return ClassifyPlayerScoped(currentParts, latestParts);
        }

        return ClassifyLegacyThreePart(currentParts, latestParts);
    }

    /// <summary>是否允许自动应用本次更新（决策 D5：只有安装与补丁可以自动）。</summary>
    public static bool CanAutoUpdate(
        string? currentVersion,
        string? latestVersion,
        string? playerKey = null) =>
        Classify(currentVersion, latestVersion, playerKey)
            is ConnectorUpdateKind.Install or ConnectorUpdateKind.Patch;

    /// <summary>是否必须由用户手动确认（换播放器分支或主版本推进）。</summary>
    public static bool RequiresManualUpdate(
        string? currentVersion,
        string? latestVersion,
        string? playerKey = null) =>
        Classify(currentVersion, latestVersion, playerKey)
            is ConnectorUpdateKind.Player or ConnectorUpdateKind.Major;

    /// <summary>
    /// 解析版本号。合法形态为 3–5 段十进制数字，每段不得超过安全整数上界。
    /// </summary>
    public static bool TryParse(string? value, out long[] parts)
    {
        parts = [];

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string normalized = value.Trim();
        if (!VersionPattern.IsMatch(normalized))
        {
            return false;
        }

        string[] segments = normalized.Split('.');
        long[] parsed = new long[segments.Length];

        for (int index = 0; index < segments.Length; index++)
        {
            if (!long.TryParse(
                    segments[index],
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out long part)
                || part > MaxSafeInteger)
            {
                return false;
            }

            parsed[index] = part;
        }

        parts = parsed;
        return true;
    }

    /// <summary>
    /// player-scoped：末段是分支内补丁，前面所有段是播放器分支。
    /// </summary>
    private static ConnectorUpdateKind ClassifyPlayerScoped(long[] currentParts, long[] latestParts)
    {
        // 从上游早期"1.x.y"命名迁移过来时，段数与分支口径都对不上，
        // 只能当作补丁处理——否则用户会卡在永远无法自动升级的旧版上。
        bool currentIsLegacy = currentParts[0] == 1
            && (currentParts.Length == 3 || currentParts.Length != latestParts.Length);

        if (currentIsLegacy)
        {
            return ConnectorUpdateKind.Patch;
        }

        // 段数不同且不是上面那种可识别的旧命名 → 两个版本不可比，不做任何事。
        if (currentParts.Length != latestParts.Length)
        {
            return ConnectorUpdateKind.None;
        }

        int branchOrder = CompareParts(
            latestParts.AsSpan(0, latestParts.Length - 1),
            currentParts.AsSpan(0, currentParts.Length - 1));

        if (branchOrder < 0)
        {
            // 远端分支比本地旧 → 远端是更老的分支，不要"降级"。
            return ConnectorUpdateKind.None;
        }

        if (branchOrder > 0)
        {
            return ConnectorUpdateKind.Player;
        }

        return latestParts[^1] > currentParts[^1]
            ? ConnectorUpdateKind.Patch
            : ConnectorUpdateKind.None;
    }

    /// <summary>legacy 3 段：<c>[主版本, 播放器, 补丁]</c>。</summary>
    private static ConnectorUpdateKind ClassifyLegacyThreePart(long[] currentParts, long[] latestParts)
    {
        if (currentParts.Length != 3)
        {
            return ConnectorUpdateKind.None;
        }

        if (latestParts[0] < currentParts[0])
        {
            return ConnectorUpdateKind.None;
        }

        if (latestParts[0] > currentParts[0])
        {
            return ConnectorUpdateKind.Major;
        }

        if (latestParts[1] < currentParts[1])
        {
            return ConnectorUpdateKind.None;
        }

        if (latestParts[1] > currentParts[1])
        {
            return ConnectorUpdateKind.Player;
        }

        return latestParts[2] > currentParts[2]
            ? ConnectorUpdateKind.Patch
            : ConnectorUpdateKind.None;
    }

    /// <summary>逐段比较，缺位按 0 处理（与参考实现 <c>compareParts</c> 一致）。</summary>
    private static int CompareParts(ReadOnlySpan<long> left, ReadOnlySpan<long> right)
    {
        int length = Math.Max(left.Length, right.Length);

        for (int index = 0; index < length; index++)
        {
            long leftPart = index < left.Length ? left[index] : 0L;
            long rightPart = index < right.Length ? right[index] : 0L;
            long difference = leftPart - rightPart;

            if (difference != 0)
            {
                return difference > 0 ? 1 : -1;
            }
        }

        return 0;
    }
}
