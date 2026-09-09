using Erbai.Contracts.Abstractions;
using Erbai.Contracts.Configuration;
using Erbai.Contracts.Logging;
using Erbai.Contracts.QueueUp;

namespace Erbai.Modules.QueueUp;

/// <summary>
/// 排队队列核心（docs/01 §3.7 最小版）：FIFO 队列 + 弹幕命令（排队/取消排队/完成）
/// + 礼物插队规则引擎。单一事实源 = 内存有序队列，写库持久化（重启按 created_at
/// 恢复——插队位置不跨重启，最小版限制，交接说明注明）。
/// 事件：queueup.added/updated/completed/cancelled/rejected（附完整快照）。
/// </summary>
public sealed class QueueUpService : IQueueUpService
{
    private readonly IStorageEngine _storage;
    private readonly IEventBus _bus;
    private readonly ILogBus _logs;

    /// <summary>配置读取函数（每次读现值，容量/资格门槛/规则热生效）。</summary>
    private readonly Func<AppConfig> _config;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<QueueUpEntry> _queue = []; // 活动队列，位置 0 = 队首
    private readonly HashSet<string> _eligible = []; // 会话内资格集（不持久化，重启清空）

    public QueueUpService(IStorageEngine storage, IEventBus bus, ILogBus logs, Func<AppConfig> config)
    {
        _storage = storage;
        _bus = bus;
        _logs = logs;
        _config = config;
    }

    public int Count
    {
        get
        {
            _gate.Wait();
            try
            {
                return _queue.Count;
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    /// <summary>从存储恢复 queued 条目（created_at 升序 FIFO；插队位置不跨重启）。</summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        var restored = await _storage.ListQueueUpEntriesAsync(QueueUpStatus.Queued, ct);
        await _gate.WaitAsync(ct);
        try
        {
            _queue.Clear();
            _queue.AddRange(restored);
            _eligible.Clear();
        }
        finally
        {
            _gate.Release();
        }

        if (restored.Count > 0)
        {
            _logs.Log(LogLevel.Information, $"[排队] 启动恢复 {restored.Count} 条排队条目");
        }
    }

    /// <summary>当前快照（position 1 起动态编号）。</summary>
    public QueueUpSnapshot Snapshot()
    {
        _gate.Wait();
        try
        {
            return BuildSnapshotLocked();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 入队（弹幕「排队 [内容]」或礼物插队）。已在队中 → 更新自己的内容
    /// （用户已确认语义，不发新条目）；资格门槛开启且无资格 → not_eligible；
    /// 队满 → queue_full。insertPosition（1 起）仅礼物插队使用，越界收敛。
    /// </summary>
    public async Task<QueueUpOutcome> EnqueueAsync(
        string userId,
        string nickname,
        string content,
        QueueUpSource source = QueueUpSource.Danmaku,
        int? insertPosition = null,
        CancellationToken ct = default)
    {
        var settings = _config();
        if (!settings.QueueUp.Enabled)
        {
            return new QueueUpOutcome(false, null, "disabled");
        }

        await _gate.WaitAsync(ct);
        try
        {
            // 已在队：弹幕 → 更新自己的内容（重复排队语义，用户已确认）；
            // 礼物插队 → 移动位置（插队语义,绝不为同一用户产生第二条条目——
            // 否则看板重复显示、取消只删第一条、完成后残留幽灵条目）。
            // 注意：已在队路径不收资格门槛限制（准入只针对新入队；重启后
            // 资格集被清空但队列从存储恢复——已入队用户必须仍能更新内容/插队，
            // 审计 T2-2）。
            var existingIndex = _queue.FindIndex(e => e.UserId == userId);
            if (existingIndex >= 0)
            {
                var existing = _queue[existingIndex];
                if (source == QueueUpSource.Danmaku)
                {
                    var updated = await _storage.UpdateQueueUpEntryContentAsync(existing.Id, content, ct);
                    if (updated is null)
                    {
                        return new QueueUpOutcome(false, existing, "stale");
                    }

                    _queue[existingIndex] = updated;
                    PublishLocked("queueup.updated", updated, null);
                    return new QueueUpOutcome(true, updated);
                }

                if (insertPosition is int giftPosition)
                {
                    // 礼物插队：移除后按目标名次（1 起，越界收敛）重新插入
                    _queue.RemoveAt(existingIndex);
                    var target = Math.Clamp(giftPosition - 1, 0, _queue.Count);
                    _queue.Insert(target, existing);
                    PublishLocked("queueup.updated", existing, null);
                    return new QueueUpOutcome(true, existing);
                }

                // 无名次参数的礼物入队（理论不可达：规则 action=insert_at 必带 Position）：
                // 已在队 → 幂等返回，不产生第二条
                return new QueueUpOutcome(true, existing);
            }

            // 资格门槛（EligibilityGate 开）→ 无资格拒绝（仅新入队）
            if (settings.QueueUp.EligibilityGate && !_eligible.Contains(userId))
            {
                return RejectLocked("not_eligible", entry: null);
            }

            // 队满
            if (_queue.Count >= settings.QueueUp.MaxEntries)
            {
                return RejectLocked("queue_full", entry: null);
            }

            var now = DateTimeOffset.UtcNow;
            var draft = new QueueUpEntry
            {
                UserId = userId,
                Nickname = nickname,
                Content = content,
                Source = source,
                Status = QueueUpStatus.Queued,
                CreatedAt = now,
            };
            var stored = await _storage.InsertQueueUpEntryAsync(draft, ct);
            var insertIndex = insertPosition is int p
                ? Math.Clamp(p - 1, 0, _queue.Count)
                : _queue.Count;
            _queue.Insert(insertIndex, stored);
            PublishLocked("queueup.added", stored, null);
            return new QueueUpOutcome(true, stored);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>用户取消自己的条目（弹幕「取消排队」）；不在队中静默（返回 false 无事件）。</summary>
    public async Task<QueueUpOutcome> CancelByUserAsync(string userId, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var index = _queue.FindIndex(e => e.UserId == userId);
            return index < 0
                ? new QueueUpOutcome(false)
                : await RemoveLockedAsync(index, QueueUpStatus.Cancelled, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>按条目 Id 取消（UI 页面操作）；条目不存在或已出队返回 false。</summary>
    public async Task<QueueUpOutcome> CancelEntryAsync(long entryId, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var index = _queue.FindIndex(e => e.Id == entryId);
            return index < 0
                ? new QueueUpOutcome(false)
                : await RemoveLockedAsync(index, QueueUpStatus.Cancelled, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>完成队首（「完成」命令 / UI 按钮）；空队返回 false。</summary>
    public async Task<QueueUpOutcome> CompleteNextAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            return _queue.Count == 0
                ? new QueueUpOutcome(false, null, "empty")
                : await RemoveLockedAsync(0, QueueUpStatus.Completed, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>授予入队资格（规则 action=grant_eligibility；会话内有效）。</summary>
    public bool GrantEligibility(string userId)
    {
        _gate.Wait();
        try
        {
            return _eligible.Add(userId);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>是否有入队资格（EligibilityGate 关闭时恒 true，调用方不必查询）。</summary>
    public bool HasEligibility(string userId)
    {
        _gate.Wait();
        try
        {
            return _eligible.Contains(userId);
        }
        finally
        {
            _gate.Release();
        }
    }

    // ---- 内部 ----

    private async Task<QueueUpOutcome> RemoveLockedAsync(int index, QueueUpStatus status, CancellationToken ct)
    {
        var entry = _queue[index];
        var stored = await _storage.UpdateQueueUpEntryStatusAsync(entry.Id, status, ct);
        if (stored is null)
        {
            // 并发下已被他人出队（存储 CAS 拒绝）→ 移除内存并保持静默
            _queue.RemoveAt(index);
            return new QueueUpOutcome(false, entry, "stale");
        }

        _queue.RemoveAt(index);
        PublishLocked(status == QueueUpStatus.Completed ? "queueup.completed" : "queueup.cancelled", stored, null);
        return new QueueUpOutcome(true, stored);
    }

    private QueueUpOutcome RejectLocked(string reason, QueueUpEntry? entry)
    {
        PublishLocked("queueup.rejected", entry, reason);
        return new QueueUpOutcome(false, entry, reason);
    }

    private void PublishLocked(string eventName, QueueUpEntry? entry, string? reason)
    {
        _bus.Publish(new QueueUpEventEnvelope
        {
            Event = eventName,
            Version = 1,
            Timestamp = DateTimeOffset.UtcNow,
            Data = new QueueUpEventData
            {
                Entry = entry,
                Reason = reason,
                Snapshot = BuildSnapshotLocked(),
            },
        });
    }

    private QueueUpSnapshot BuildSnapshotLocked()
    {
        var items = new QueueUpItem[_queue.Count];
        for (var i = 0; i < _queue.Count; i++)
        {
            items[i] = new QueueUpItem { Entry = _queue[i], Position = i + 1 };
        }

        return new QueueUpSnapshot
        {
            Items = items,
            Total = _queue.Count,
            MaxEntries = _config().QueueUp.MaxEntries,
        };
    }
}
