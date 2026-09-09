namespace Erbai.Contracts.Requests;

/// <summary>
/// 点歌请求生命周期状态（11 态）。
/// </summary>
public enum RequestStatus
{
    Received,
    Rejected,
    Queued,
    Searching,
    Ready,
    Dispatched,
    Completed,
    Skipped,
    Cancelled,
    Failed,
    RecoveryRequired,
}

public static class RequestStatuses
{
    /// <summary>活动状态：queued / searching / ready / dispatched。</summary>
    public static readonly IReadOnlySet<RequestStatus> Active = new HashSet<RequestStatus>
    {
        RequestStatus.Queued,
        RequestStatus.Searching,
        RequestStatus.Ready,
        RequestStatus.Dispatched,
    };

    /// <summary>终态：rejected / completed / skipped / cancelled / failed / recovery_required。</summary>
    public static readonly IReadOnlySet<RequestStatus> Terminal = new HashSet<RequestStatus>
    {
        RequestStatus.Rejected,
        RequestStatus.Completed,
        RequestStatus.Skipped,
        RequestStatus.Cancelled,
        RequestStatus.Failed,
        RequestStatus.RecoveryRequired,
    };

    public static bool IsActive(RequestStatus status) => Active.Contains(status);

    public static bool IsTerminal(RequestStatus status) => Terminal.Contains(status);
}
