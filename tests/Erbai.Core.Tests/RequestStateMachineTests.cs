using Erbai.Contracts.Requests;

namespace Erbai.Core.Tests;

/// <summary>请求状态流转表（语义钉住）。</summary>
public class RequestStateMachineTests
{
    [Theory]
    [InlineData(RequestStatus.Received, RequestStatus.Queued)]
    [InlineData(RequestStatus.Received, RequestStatus.Rejected)]
    [InlineData(RequestStatus.Queued, RequestStatus.Searching)]
    [InlineData(RequestStatus.Queued, RequestStatus.Skipped)]
    [InlineData(RequestStatus.Queued, RequestStatus.Cancelled)]
    [InlineData(RequestStatus.Searching, RequestStatus.Ready)]
    [InlineData(RequestStatus.Searching, RequestStatus.Dispatched)]
    [InlineData(RequestStatus.Searching, RequestStatus.Skipped)]
    [InlineData(RequestStatus.Searching, RequestStatus.Failed)]
    [InlineData(RequestStatus.Searching, RequestStatus.Cancelled)]
    [InlineData(RequestStatus.Searching, RequestStatus.Queued)]
    [InlineData(RequestStatus.Ready, RequestStatus.Dispatched)]
    [InlineData(RequestStatus.Ready, RequestStatus.Skipped)]
    [InlineData(RequestStatus.Ready, RequestStatus.Failed)]
    [InlineData(RequestStatus.Ready, RequestStatus.Cancelled)]
    [InlineData(RequestStatus.Ready, RequestStatus.Queued)]
    [InlineData(RequestStatus.Dispatched, RequestStatus.Completed)]
    [InlineData(RequestStatus.Dispatched, RequestStatus.Skipped)]
    [InlineData(RequestStatus.Dispatched, RequestStatus.Cancelled)]
    [InlineData(RequestStatus.Dispatched, RequestStatus.Queued)]
    [InlineData(RequestStatus.Dispatched, RequestStatus.Failed)]
    [InlineData(RequestStatus.Dispatched, RequestStatus.RecoveryRequired)]
    public void AllowedTransitions_AreLegal(RequestStatus from, RequestStatus to)
    {
        Assert.True(RequestStateMachine.CanTransition(from, to), $"{from} -> {to} 应合法");
    }

    [Theory]
    [InlineData(RequestStatus.Received, RequestStatus.Completed)]
    [InlineData(RequestStatus.Received, RequestStatus.Dispatched)]
    [InlineData(RequestStatus.Queued, RequestStatus.Dispatched)]
    [InlineData(RequestStatus.Queued, RequestStatus.Completed)]
    [InlineData(RequestStatus.Searching, RequestStatus.Completed)]
    [InlineData(RequestStatus.Ready, RequestStatus.Completed)]
    [InlineData(RequestStatus.Dispatched, RequestStatus.Searching)]
    [InlineData(RequestStatus.Dispatched, RequestStatus.Ready)]
    [InlineData(RequestStatus.Completed, RequestStatus.Queued)]
    [InlineData(RequestStatus.Skipped, RequestStatus.Queued)]
    [InlineData(RequestStatus.Cancelled, RequestStatus.Queued)]
    [InlineData(RequestStatus.Failed, RequestStatus.Queued)]
    [InlineData(RequestStatus.Rejected, RequestStatus.Queued)]
    [InlineData(RequestStatus.RecoveryRequired, RequestStatus.Queued)]
    public void IllegalTransitions_AreRejected(RequestStatus from, RequestStatus to)
    {
        Assert.False(RequestStateMachine.CanTransition(from, to), $"{from} -> {to} 应非法");
    }

    [Theory]
    [InlineData(RequestStatus.Received)]
    [InlineData(RequestStatus.Rejected)]
    [InlineData(RequestStatus.Queued)]
    [InlineData(RequestStatus.Searching)]
    [InlineData(RequestStatus.Ready)]
    [InlineData(RequestStatus.Dispatched)]
    [InlineData(RequestStatus.Completed)]
    [InlineData(RequestStatus.Skipped)]
    [InlineData(RequestStatus.Cancelled)]
    [InlineData(RequestStatus.Failed)]
    [InlineData(RequestStatus.RecoveryRequired)]
    public void SameStatus_AlwaysAllowed(RequestStatus status)
    {
        Assert.True(RequestStateMachine.CanTransition(status, status));
    }

    [Fact]
    public void ActiveStatuses_AreQueuedSearchingReadyDispatched()
    {
        Assert.Equal(
            new[] { RequestStatus.Queued, RequestStatus.Searching, RequestStatus.Ready, RequestStatus.Dispatched },
            RequestStatuses.Active.OrderBy(s => s));
    }

    [Fact]
    public void StatusSets_ReceivedIsTransient_NotInEitherSet()
    {
        // 旧版：received 是过渡初始态，既不在 ACTIVE 也不在 TERMINAL
        var active = RequestStatuses.Active;
        var terminal = RequestStatuses.Terminal;
        var all = Enum.GetValues<RequestStatus>();
        Assert.Equal(all.Length - 1, active.Count + terminal.Count);
        Assert.DoesNotContain(active, s => terminal.Contains(s));
        Assert.All(all.Where(s => s != RequestStatus.Received),
            s => Assert.True(RequestStatuses.IsActive(s) || RequestStatuses.IsTerminal(s)));
        Assert.False(RequestStatuses.IsActive(RequestStatus.Received));
        Assert.False(RequestStatuses.IsTerminal(RequestStatus.Received));
    }
}
