using System.Text.Json;
using System.Text.Json.Serialization;

namespace Erbai.Contracts.Players;

/// <summary>
/// 连接器 NDJSON-stdio 协议模型（决策 #15，规范 docs/04 §1.4，v1.1 实测
/// 差异：字符串 id / camelCase 字段 / search 裸数组 / 命令枚举 camelCase）。
/// 两端（Erbai.Connector 宿主 + Erbai.Player.Connectors 客户端）共享本模型，
/// 协议演进内部化。
/// </summary>
public static class ConnectorProtocol
{
    /// <summary>快照事件流能力名（ping 的 features 面）。</summary>
    public const string FeatureSnapshotEvents = "snapshot-events-v1";

    /// <summary>队列可编程能力名（InsertNext / ArmNextGuard / InterruptSelected）。</summary>
    public const string FeatureQueueProgrammable = "queue-programmable-v1";

    /// <summary>
    /// 当前协议版本。第三方连接器的兼容契约就是这一项（决策 D4）：上游 v2 清单里的
    /// <c>minimumCoreVersion</c> 是发布方自家应用（AwooMusicBot）的版本口径，与本应用
    /// 版本号不可比，只能作展示提示，不能当门控。
    /// </summary>
    public const int ProtocolVersion = 1;

    /// <summary>execute 命令的线格式枚举（camelCase，04 §1.4 映射表）。</summary>
    public static readonly IReadOnlyDictionary<PlayerCommand, string> CommandNames =
        new Dictionary<PlayerCommand, string>
        {
            [PlayerCommand.PlaySelected] = "playSelected",
            [PlayerCommand.InsertNext] = "insertNext",
            [PlayerCommand.ArmNextGuard] = "armNextGuard",
            [PlayerCommand.InterruptSelected] = "interruptSelected",
            [PlayerCommand.Next] = "next",
            [PlayerCommand.Pause] = "pause",
            [PlayerCommand.Resume] = "resume",
        };

    public static string CommandName(PlayerCommand command) => CommandNames[command];

    /// <summary>解析 execute 命令名（未知命令返回 null，宿主回 unsupported）。</summary>
    public static PlayerCommand? ParseCommand(string? name)
    {
        foreach (var (command, wire) in CommandNames)
        {
            if (string.Equals(wire, name, StringComparison.OrdinalIgnoreCase))
            {
                return command;
            }
        }

        return null;
    }

    /// <summary>快照 next 可观察性的线格式值（camelCase，v1.1：nextSource）。</summary>
    public static string NextSource(NextObservation observation) => observation switch
    {
        NextObservation.Legacy => "legacy",
        NextObservation.Unknown => "unknown",
        NextObservation.Track => "track",
        NextObservation.Empty => "empty",
        _ => "unknown",
    };

    public static NextObservation ParseNextSource(string? value) => value?.ToLowerInvariant() switch
    {
        "legacy" => NextObservation.Legacy,
        "track" => NextObservation.Track,
        "empty" => NextObservation.Empty,
        _ => NextObservation.Unknown,
    };

    /// <summary>
    /// 解析 ping 响应为能力面（docs/04 §1.4）。同时兼容两种来源并合并：
    /// ① 上游规范形状——能力在 <c>result</c> 内（<c>features</c> 字符串数组 +
    /// <c>capabilities</c> 布尔对象，见上游 Awoo.Connector.*.exe 实测响应）；
    /// ② 本仓库旧宿主形状——能力在响应<b>顶层</b> <c>protocolCapabilities</c> 字符串数组。
    /// 合并后 Features 去重；旧宿主不产出 capabilities 对象，其布尔位保持缺省 false。
    /// </summary>
    public static ConnectorPingInfo ParsePing(
        JsonElement? result,
        IReadOnlyList<string>? legacyCapabilities = null)
    {
        var features = new List<string>();
        var capabilities = new ConnectorCapabilitySet();
        var protocolVersion = 0;
        var eventProtocolVersion = 0;
        string? connectorId = null;
        string? connectorVersion = null;

        if (result is { ValueKind: JsonValueKind.Object } obj)
        {
            protocolVersion = GetInt(obj, "protocolVersion") ?? 0;
            eventProtocolVersion = GetInt(obj, "eventProtocolVersion") ?? 0;
            connectorId = GetString(obj, "connectorId");
            connectorVersion = GetString(obj, "connectorVersion");

            if (obj.TryGetProperty("features", out var featuresElement)
                && featuresElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in featuresElement.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        var value = item.GetString();
                        if (!string.IsNullOrEmpty(value))
                        {
                            features.Add(value);
                        }
                    }
                }
            }

            if (obj.TryGetProperty("capabilities", out var capsElement)
                && capsElement.ValueKind == JsonValueKind.Object)
            {
                capabilities = new ConnectorCapabilitySet
                {
                    Search = GetBool(capsElement, "search"),
                    PlaySelected = GetBool(capsElement, "playSelected"),
                    Previous = GetBool(capsElement, "previous"),
                    Pause = GetBool(capsElement, "pause"),
                    Resume = GetBool(capsElement, "resume"),
                    Toggle = GetBool(capsElement, "toggle"),
                    Next = GetBool(capsElement, "next"),
                    InsertNext = GetBool(capsElement, "insertNext"),
                    InsertNextLevel = GetString(capsElement, "insertNextLevel"),
                };
            }
        }

        // 旧宿主形状合并进 features（去重、保持既有顺序在前）
        if (legacyCapabilities is not null)
        {
            foreach (var capability in legacyCapabilities)
            {
                if (!string.IsNullOrEmpty(capability)
                    && !features.Contains(capability, StringComparer.Ordinal))
                {
                    features.Add(capability);
                }
            }
        }

        return new ConnectorPingInfo
        {
            ProtocolVersion = protocolVersion,
            EventProtocolVersion = eventProtocolVersion,
            ConnectorId = connectorId,
            ConnectorVersion = connectorVersion,
            Features = features,
            Capabilities = capabilities,
        };
    }

    private static int? GetInt(JsonElement element, string property) => ReadInt32(element, property);

    /// <summary>
    /// 安全读 32 位整数：属性缺失、值为 <c>null</c>、或值不是 Number 时返回 <see langword="null"/>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>不要直接写 <c>TryGetProperty(p, out v) &amp;&amp; v.TryGetInt32(out n)</c>。</b>
    /// <see cref="JsonElement.TryGetInt32"/> 的名字有误导性：它<b>只</b>对"是 Number 但不是整数
    /// （如 <c>1.5</c>）或超出 Int32 范围"返回 <see langword="false"/>；元素是 <c>null</c>、
    /// 字符串或布尔时它<b>抛</b> <see cref="InvalidOperationException"/>。而
    /// <c>TryGetProperty</c> 只保证"属性存在"，不保证"值是数字"。
    /// </para>
    /// <para>
    /// 这个坑在 2026-09-18 的 P7 端到端验收中被真实上游触发：kugou 的 ping 回包显式带
    /// <c>"eventProtocolVersion": null</c>（netease 则是整个字段缺席，所以只测 netease 时看不出来），
    /// 导致 <see cref="ParsePing"/> 抛异常、连接器激活整体失败。
    /// 单元测试用合成夹具、夹具里的数字永远合法，因此该缺陷在夹具层面<b>结构性不可见</b>——
    /// 它是被"跑真实上游"发现的，不是被测试发现的。
    /// </para>
    /// <para>
    /// 另需注意 <see cref="JsonElement.TryGetProperty"/> 在<b>容器本身不是对象</b>时
    /// （例如整个 <c>result</c> 就是 <c>null</c>）同样会抛；调用方必须先确认容器
    /// 是 <see cref="JsonValueKind.Object"/>。
    /// </para>
    /// </remarks>
    public static int? ReadInt32(JsonElement element, string property) =>
        element.TryGetProperty(property, out JsonElement value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out int parsed)
            ? parsed
            : null;

    /// <summary>安全读双精度浮点：语义同 <see cref="ReadInt32"/>，并额外拒绝非有限值。</summary>
    /// <remarks>
    /// 拒绝 <see cref="double.IsFinite"/> 为假的值：JSON 里的 <c>1e400</c> 会被
    /// <c>TryGetDouble</c> <b>静默</b>读成 <see cref="double.PositiveInfinity"/>，
    /// 这对 <c>progressSeconds</c> 之类的进度字段没有意义，放行只会把它带进 UI。
    /// 返回 <see langword="null"/> 与"字段缺失"同义。
    /// </remarks>
    public static double? ReadDouble(JsonElement element, string property) =>
        element.TryGetProperty(property, out JsonElement value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetDouble(out double parsed)
        && double.IsFinite(parsed)
            ? parsed
            : null;

    private static bool GetBool(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;

    private static string? GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>协议 JSON 选项：camelCase（v1.1 字段面）。</summary>
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

/// <summary>客户端 → 宿主请求（stdin 每行一个 JSON，utf-8）。</summary>
public sealed record ConnectorRequest
{
    public required string Id { get; init; }

    /// <summary>ping | probe | search | execute | subscribe | shutdown。</summary>
    public required string Action { get; init; }

    /// <summary>目标连接器 id（null = 当前激活平台）。</summary>
    public string? Player { get; init; }

    public string? Query { get; init; }

    /// <summary>execute 命令（camelCase 枚举）。</summary>
    public string? Command { get; init; }

    public PlayerTrack? Track { get; init; }

    public int? EventProtocolVersion { get; init; }
}

/// <summary>宿主 → 客户端响应（stdout 每行一个 JSON）。</summary>
public sealed record ConnectorResponse
{
    public required string Id { get; init; }

    public required bool Ok { get; init; }

    public JsonElement? Result { get; init; }

    public string? Error { get; init; }

    /// <summary>加性能力协商（snapshot-events-v1 等；旧宿主可忽略可选能力）。</summary>
    public IReadOnlyList<string> ProtocolCapabilities { get; init; } = [];
}

/// <summary>宿主主动推送事件（stdout）。</summary>
public sealed record ConnectorEvent
{
    public string Type { get; init; } = "event";

    public string Event { get; init; } = "snapshot";

    public string? Player { get; init; }

    public long Sequence { get; init; }

    public JsonElement? Snapshot { get; init; }
}

/// <summary>
/// ping 的命令级能力面（上游规范形状：<c>result.capabilities</c> 布尔对象）。
/// 上游连接器用它声明支持哪些命令；本仓库旧宿主不产出该对象，
/// 故所有位缺省 false，需要时由 <see cref="FromFeatures"/> 从 features 推导。
/// </summary>
public sealed record ConnectorCapabilitySet
{
    public bool Search { get; init; }

    public bool PlaySelected { get; init; }

    public bool Previous { get; init; }

    public bool Pause { get; init; }

    public bool Resume { get; init; }

    public bool Toggle { get; init; }

    public bool Next { get; init; }

    /// <summary>支持 InsertNext（队列可编程）——对应 PlayerCapabilities.QueueProgrammable。</summary>
    public bool InsertNext { get; init; }

    /// <summary>insertNext 的实现说明（上游诊断字段，仅透传展示，不参与判定）。</summary>
    public string? InsertNextLevel { get; init; }

    /// <summary>
    /// 本仓库宿主（Erbai.Connector）的能力面推导口径：各后端均支持
    /// PlaySelected/Next/Pause/Resume；InsertNext 由 <c>queue-programmable-v1</c>
    /// 特性推导；search/previous/toggle 不支持。
    /// </summary>
    public static ConnectorCapabilitySet FromFeatures(IReadOnlyList<string> features) => new()
    {
        PlaySelected = true,
        Next = true,
        Pause = true,
        Resume = true,
        InsertNext = features.Contains(ConnectorProtocol.FeatureQueueProgrammable, StringComparer.Ordinal),
    };
}

/// <summary>
/// ping 元数据（能力协商面，docs/04 §1.4）。上游连接器把能力放在 ping 的
/// <c>result</c> 内；本仓库旧宿主放在响应顶层 <c>protocolCapabilities</c>。
/// 由 <see cref="ConnectorProtocol.ParsePing"/> 统一归一。
/// </summary>
public sealed record ConnectorPingInfo
{
    public int ProtocolVersion { get; init; }

    public int EventProtocolVersion { get; init; }

    /// <summary>连接器 id（上游 = 平台键，如 "netease"；本仓库 = 后端 Key）。</summary>
    public string? ConnectorId { get; init; }

    public string? ConnectorVersion { get; init; }

    /// <summary>能力名数组（snapshot-events-v1 / queue-programmable-v1 等）。</summary>
    public IReadOnlyList<string> Features { get; init; } = [];

    public ConnectorCapabilitySet Capabilities { get; init; } = new();

    public bool HasFeature(string feature) => Features.Contains(feature, StringComparer.Ordinal);
}
