using Erbai.Contracts.Players;

namespace Erbai.Connector.Shared;

/// <summary>
/// armNextGuard 软件兜底监视器（机制参考 docs/04 §1.5.5，代码表达沿用上游）：
/// 发起后以 50ms 周期轮询当前曲，直到曲目变化：
/// 命中目标 → 成功结束；错歌 → 调用 takeOver 兜底接管；12 小时未切歌 → 过期。
/// 支持重新 Arm（替换旧守卫）与 Cancel。
/// </summary>
public sealed class GuardedNextMonitor : IDisposable
{
    private readonly object _sync = new();
    private CancellationTokenSource? _cancellation;
    private volatile string _status = string.Empty;

    public string Status => _status;

    /// <summary>
    /// 启动守卫。current 为空（当前曲不可识别）时拒绝并返回 false。
    /// </summary>
    public bool Arm(
        PlayerTrack? current,
        PlayerTrack target,
        Func<CancellationToken, Task<PlayerTrack?>> readCurrent,
        Func<PlayerTrack, CancellationToken, Task<string>> takeOver,
        CancellationToken lifetimeToken,
        out string message)
    {
        if (current is null || string.IsNullOrWhiteSpace(current.Title))
        {
            message = "当前歌曲不可识别，无法启动下一首守卫。";
            return false;
        }

        var owner = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken);
        CancellationTokenSource? previous;
        lock (_sync)
        {
            previous = _cancellation;
            _cancellation = owner;
            _status = $"下一首守卫待命：{target.Title}";
            _ = MonitorAsync(owner, current, target, readCurrent, takeOver);
        }

        if (previous is not null)
        {
            previous.Cancel();
            previous.Dispose();
        }

        message = $"下一首守卫已启动：若实际下一首不是 {target.Title}，将执行连接器的安全兜底并切换到目标。";
        return true;
    }

    public void Cancel(string status = "")
    {
        CancellationTokenSource? cancellation;
        lock (_sync)
        {
            cancellation = _cancellation;
            _cancellation = null;
            _status = status;
        }

        if (cancellation is not null)
        {
            cancellation.Cancel();
            cancellation.Dispose();
        }
    }

    public void Dispose() => Cancel();

    private async Task MonitorAsync(
        CancellationTokenSource owner,
        PlayerTrack initial,
        PlayerTrack target,
        Func<CancellationToken, Task<PlayerTrack?>> readCurrent,
        Func<PlayerTrack, CancellationToken, Task<string>> takeOver)
    {
        var token = owner.Token;
        try
        {
            var expiresAt = DateTimeOffset.UtcNow + TimeSpan.FromHours(12);
            while (DateTimeOffset.UtcNow < expiresAt)
            {
                await Task.Delay(50, token).ConfigureAwait(false);
                PlayerTrack? observed;
                try
                {
                    observed = await readCurrent(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception)
                {
                    continue; // 读取失败继续观察
                }

                if (observed is null || SameTrack(observed, initial))
                {
                    continue;
                }

                if (SameTrack(observed, target))
                {
                    SetStatus(owner, $"下一首已正确命中：{target.Title}");
                    return;
                }

                SetStatus(owner, $"检测到错误下一首：{observed.Title}；正在兜底接管");
                string result;
                try
                {
                    result = await takeOver(target, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    result = $"兜底接管失败：{ex.Message}";
                }

                SetStatus(owner, result);
                return;
            }

            SetStatus(owner, "下一首守卫已过期（12 小时未发生切歌）");
        }
        catch (OperationCanceledException)
        {
            // 被替换/取消/应用关闭
        }
        finally
        {
            lock (_sync)
            {
                if (ReferenceEquals(_cancellation, owner))
                {
                    _cancellation = null;
                    owner.Dispose();
                }
            }
        }
    }

    private void SetStatus(CancellationTokenSource owner, string status)
    {
        lock (_sync)
        {
            if (ReferenceEquals(_cancellation, owner))
            {
                _status = status;
            }
        }
    }

    private static bool SameTrack(PlayerTrack left, PlayerTrack right)
    {
        if (!string.IsNullOrWhiteSpace(left.Id) && !string.IsNullOrWhiteSpace(right.Id))
        {
            return string.Equals(left.Id, right.Id, StringComparison.OrdinalIgnoreCase);
        }

        return Normalize(left.Title) == Normalize(right.Title)
            && (string.IsNullOrWhiteSpace(left.Artist)
                || string.IsNullOrWhiteSpace(right.Artist)
                || Normalize(left.Artist) == Normalize(right.Artist));
    }

    private static string Normalize(string value) => value.Trim().ToUpperInvariant();
}
