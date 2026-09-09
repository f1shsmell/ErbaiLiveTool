namespace Erbai.Contracts.Players;

/// <summary>
/// 命令执行结果的显式语义（对齐 AwooMusicBot connector 协议）。
/// </summary>
public enum PlayerOutcome
{
    /// <summary>已接受（异步生效）。</summary>
    Accepted,

    /// <summary>已应用。</summary>
    Applied,

    /// <summary>已验证。</summary>
    Verified,

    /// <summary>无法确认（视为成功）。</summary>
    Indeterminate,

    /// <summary>被拒绝。</summary>
    Rejected,

    /// <summary>不支持该命令。</summary>
    Unsupported,
}

/// <summary>命令执行结果；<see cref="Outcome"/> ∈ accepted/applied/verified/indeterminate 视为成功。</summary>
public sealed record PlayerOperationResult(PlayerOutcome Outcome, string Message)
{
    /// <summary>成功结果集合：accepted / applied / verified / indeterminate。</summary>
    public static bool IsSuccessful(PlayerOutcome outcome) =>
        outcome is PlayerOutcome.Accepted
            or PlayerOutcome.Applied
            or PlayerOutcome.Verified
            or PlayerOutcome.Indeterminate;

    public bool Successful => IsSuccessful(Outcome);

    public static PlayerOperationResult Success(PlayerOutcome outcome, string message = "") =>
        new(outcome, message);

    public static PlayerOperationResult Failure(string message) =>
        new(PlayerOutcome.Rejected, message);

    public static PlayerOperationResult Unsupported(string message = "") =>
        new(PlayerOutcome.Unsupported, message);
}
