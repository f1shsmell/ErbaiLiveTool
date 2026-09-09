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
