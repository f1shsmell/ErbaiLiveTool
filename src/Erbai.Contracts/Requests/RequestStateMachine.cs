namespace Erbai.Contracts.Requests;

/// <summary>
/// 点歌请求状态流转表。
/// 存储层每次状态更新都校验它。
/// </summary>
public static class RequestStateMachine
{
    /// <summary>合法流转表（key 的键为源状态；同状态重复写入恒允许）。</summary>
    public static readonly IReadOnlyDictionary<RequestStatus, IReadOnlySet<RequestStatus>> AllowedTransitions =
        new Dictionary<RequestStatus, IReadOnlySet<RequestStatus>>
        {
            [RequestStatus.Received] = new HashSet<RequestStatus>
            {
                RequestStatus.Queued,
                RequestStatus.Rejected,
            },
            [RequestStatus.Queued] = new HashSet<RequestStatus>
            {
                RequestStatus.Searching,
                RequestStatus.Skipped,
                RequestStatus.Cancelled,
            },
            [RequestStatus.Searching] = new HashSet<RequestStatus>
            {
                RequestStatus.Ready,
                RequestStatus.Dispatched,
                RequestStatus.Skipped,
                RequestStatus.Failed,
                RequestStatus.Cancelled,
                RequestStatus.Queued,
            },
            [RequestStatus.Ready] = new HashSet<RequestStatus>
            {
                RequestStatus.Dispatched,
                RequestStatus.Skipped,
                RequestStatus.Failed,
                RequestStatus.Cancelled,
                RequestStatus.Queued,
            },
            [RequestStatus.Dispatched] = new HashSet<RequestStatus>
            {
                RequestStatus.Completed,
                RequestStatus.Skipped,
                RequestStatus.Cancelled,
                RequestStatus.Queued,
                RequestStatus.Failed,
                RequestStatus.RecoveryRequired,
            },
        };

    /// <summary>请求是否可从 <paramref name="oldStatus"/> 流转到 <paramref name="newStatus"/>；同状态恒允许。</summary>
    public static bool CanTransition(RequestStatus oldStatus, RequestStatus newStatus)
    {
        if (oldStatus == newStatus)
        {
            return true;
        }

        return AllowedTransitions.TryGetValue(oldStatus, out var allowed) && allowed.Contains(newStatus);
    }
}
