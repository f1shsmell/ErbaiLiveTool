using Erbai.Contracts.Abstractions;
using Erbai.Contracts.Configuration;
using Erbai.Contracts.Logging;
using Erbai.Contracts.QueueUp;

namespace Erbai.Modules.QueueUp;

/// <summary>
/// 排队队列核心：FIFO 队列 + 弹幕命令（排队/取消排队/完成）
/// + 礼物插队规则引擎。单一事实源 = 内存有序队列，写库持久化（重启按 created_at
/// 恢复——插队位置不跨重启，最小版限制，交接说明注明）。
/// 事件：queueup.added/updated/completed/cancelled/rejected（附完整快照）。
///
/// 锁模型（2026-10 修订）：<see cref="_gate"/>（信号量）串行化所有变更（含存储
/// I/O，跨 await 持有）；<see cref="_dataLock"/>（轻量锁）只保护 _queue/_eligible
/// 的内存读写、绝不跨 await。同步只读成员（Count/Snapshot/HasEligibility）因此
/// 只等内存锁、不等 I/O——旧实现全部走阻塞 _gate.Wait()，UI 线程调用时会冻住
/// 直到写方的存储 I/O 完成，且构成"持锁等 UI"的死锁隐患。
/// </summary>
public sealed class QueueUpService : IQueueUpService
{
    private readonly IStorageEngine _storage;
    private readonly IEventBus _bus;
    private readonly ILogBus _logs;

    /// <summary>配置读取函数（每次读现值，容量/资格门槛/规则热生效）。</summary>
    private readonly Func<AppConfig> _config;

    /// <summary>变更串行化闸门：同一时刻至多一个变更（含存储 I/O）。</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>内存数据锁：保护 _queue/_eligible；持 _gate 者短暂持有，绝不跨 await。</summary>
    private readonly object _dataLock = new();

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
            lock (_dataLock)
            {
                return _queue.Count;
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
            lock (_dataLock)
            {
                _queue.Clear();
                _queue.AddRange(restored);
                _eligible.Clear();
            }
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
        lock (_dataLock)
        {
            return BuildSnapshotLocked();
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
            QueueUpEntry? existing;
            lock (_dataLock)
            {
                var index = _queue.FindIndex(e => e.UserId == userId);
                existing = index >= 0 ? _queue[index] : null;
            }

            if (existing is not null)
            {
                if (source == QueueUpSource.Danmaku)
                {
                    var updated = await _storage.UpdateQueueUpEntryContentAsync(existing.Id, content, ct);
                    if (updated is null)
                    {
                        return new QueueUpOutcome(false, existing, "stale");
                    }

                    lock (_dataLock)
                    {
                        ReplaceByIdLocked(updated);
                        PublishLocked("queueup.updated", updated, null);
                    }

                    return new QueueUpOutcome(true, updated);
                }

                if (insertPosition is int giftPosition)
                {
                    // 礼物插队：移除后按目标名次（1 起，越界收敛）重新插入（纯内存，无 I/O）
                    lock (_dataLock)
                    {
                        _queue.RemoveAll(e => e.Id == existing.Id);
                        var target = Math.Clamp(giftPosition - 1, 0, _queue.Count);
                        _queue.Insert(target, existing);
                        PublishLocked("queueup.updated", existing, null);
                    }

                    return new QueueUpOutcome(true, existing);
                }

                // 无名次参数的礼物入队（理论不可达：规则 action=insert_at 必带 Position）：
                // 已在队 → 幂等返回，不产生第二条
                return new QueueUpOutcome(true, existing);
            }

            // 资格门槛（EligibilityGate 开）→ 无资格拒绝（仅新入队）
            bool eligible;
            lock (_dataLock)
            {
                eligible = !settings.QueueUp.EligibilityGate || _eligible.Contains(userId);
            }

            if (!eligible)
            {
                lock (_dataLock)
                {
                    return RejectLocked("not_eligible", entry: null);
                }
            }

            // 队满
            int count;
            lock (_dataLock)
            {
                count = _queue.Count;
            }

            if (count >= settings.QueueUp.MaxEntries)
            {
                lock (_dataLock)
                {
                    return RejectLocked("queue_full", entry: null);
                }
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
            lock (_dataLock)
            {
                var insertIndex = insertPosition is int p
                    ? Math.Clamp(p - 1, 0, _queue.Count)
                    : _queue.Count;
                _queue.Insert(insertIndex, stored);
                PublishLocked("queueup.added", stored, null);
            }

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
            var entry = FindLocked(e => e.UserId == userId);
            return entry is null
                ? new QueueUpOutcome(false)
                : await RemoveEntryAsync(entry, QueueUpStatus.Cancelled, ct);
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
            var entry = FindLocked(e => e.Id == entryId);
            return entry is null
                ? new QueueUpOutcome(false)
                : await RemoveEntryAsync(entry, QueueUpStatus.Cancelled, ct);
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
            var head = FindLocked(_ => true);
            return head is null
                ? new QueueUpOutcome(false, null, "empty")
                : await RemoveEntryAsync(head, QueueUpStatus.Completed, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>授予入队资格（规则 action=grant_eligibility；会话内有效）。</summary>
    public bool GrantEligibility(string userId)
    {
        lock (_dataLock)
        {
            return _eligible.Add(userId);
        }
    }

    /// <summary>是否有入队资格（EligibilityGate 关闭时恒 true，调用方不必查询）。</summary>
    public bool HasEligibility(string userId)
    {
        lock (_dataLock)
        {
            return _eligible.Contains(userId);
        }
    }

    // ---- 内部 ----

    /// <summary>持 _gate 下按条件查内存队列（_dataLock 内读取）。</summary>
    private QueueUpEntry? FindLocked(Func<QueueUpEntry, bool> predicate)
    {
        lock (_dataLock)
        {
            var entry = _queue.FirstOrDefault(predicate);
            return entry;
        }
    }

    /// <summary>
    /// 出队（取消/完成）：存储 CAS 状态 → 内存移除 → 发事件。调用方必须持 _gate；
    /// 存储调用不持 _dataLock（跨 await），落锁仅覆盖内存变更与发布。
    /// </summary>
    private async Task<QueueUpOutcome> RemoveEntryAsync(QueueUpEntry entry, QueueUpStatus status, CancellationToken ct)
    {
        var stored = await _storage.UpdateQueueUpEntryStatusAsync(entry.Id, status, ct);
        lock (_dataLock)
        {
            _queue.RemoveAll(e => e.Id == entry.Id);
            if (stored is null)
            {
                // 并发下已被他人出队（存储 CAS 拒绝）→ 内存移除并保持静默
                return new QueueUpOutcome(false, entry, "stale");
            }

            PublishLocked(status == QueueUpStatus.Completed ? "queueup.completed" : "queueup.cancelled", stored, null);
            return new QueueUpOutcome(true, stored);
        }
    }

    /// <summary>按 Id 替换内存条目（持 _gate 单写者保证存在）。</summary>
    private void ReplaceByIdLocked(QueueUpEntry updated)
    {
        var index = _queue.FindIndex(e => e.Id == updated.Id);
        if (index >= 0)
        {
            _queue[index] = updated;
        }
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
