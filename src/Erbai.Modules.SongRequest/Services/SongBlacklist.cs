using Erbai.Contracts.Abstractions;
using Erbai.Contracts.Logging;

namespace Erbai.Modules.SongRequest.Services;

/// <summary>
/// 歌曲黑名单：
/// 精确歌名 + `*关键词` 子串规则，casefold 匹配；持久化于 banned_songs 表，
/// 管理界面热编辑——先写盘成功再更新内存，失败回滚内存。
/// </summary>
public sealed class SongBlacklist
{
    private readonly IStorageEngine _store;
    private readonly ILogBus _logs;
    private readonly object _lock = new();
    private HashSet<string> _exact;
    private HashSet<string> _keywords;

    public SongBlacklist(IStorageEngine store, ILogBus logs)
    {
        _store = store;
        _logs = logs;
        _exact = new HashSet<string>(StringComparer.Ordinal);
        _keywords = new HashSet<string>(StringComparer.Ordinal);
    }

    public async Task LoadAsync(CancellationToken ct = default)
    {
        var rules = await _store.ListBannedSongsAsync(ct);
        var exact = new HashSet<string>(StringComparer.Ordinal);
        var keywords = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rule in rules)
        {
            var normalized = (rule ?? "").Trim().ToLowerInvariant();
            if (normalized.Length == 0)
            {
                continue;
            }

            if (normalized.StartsWith("*", StringComparison.Ordinal))
            {
                var keyword = normalized[1..].Trim();
                if (keyword.Length > 0)
                {
                    keywords.Add(keyword);
                }
            }
            else
            {
                exact.Add(normalized);
            }
        }

        lock (_lock)
        {
            _exact = exact;
            _keywords = keywords;
        }
    }

    /// <summary>精确命中或任一 *关键词 子串命中即黑（casefold）。</summary>
    public bool IsBlacklisted(string songName)
    {
        var normalized = (songName ?? "").Trim().ToLowerInvariant();
        if (normalized.Length == 0)
        {
            return false;
        }

        lock (_lock)
        {
            if (_exact.Contains(normalized))
            {
                return true;
            }

            foreach (var keyword in _keywords)
            {
                if (normalized.Contains(keyword, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>全部规则（精确 + `*关键词`，排序后）。</summary>
    public IReadOnlyList<string> Items()
    {
        lock (_lock)
        {
            return _exact.OrderBy(x => x, StringComparer.Ordinal)
                .Concat(_keywords.OrderBy(x => x, StringComparer.Ordinal).Select(k => $"*{k}"))
                .ToList();
        }
    }

    /// <summary>添加规则（*keyword 子串 / 精确名）；返回是否实际变更。先写盘成功再更新内存。</summary>
    public async Task<bool> AddRuleAsync(string rule, CancellationToken ct = default)
    {
        var normalized = (rule ?? "").Trim().ToLowerInvariant();
        if (normalized.Length == 0)
        {
            return false;
        }

        string? keyword;
        lock (_lock)
        {
            keyword = normalized.StartsWith("*", StringComparison.Ordinal) ? normalized[1..].Trim() : null;
            if (keyword is not null)
            {
                if (keyword.Length == 0 || _keywords.Contains(keyword))
                {
                    return false;
                }
            }
            else if (_exact.Contains(normalized))
            {
                return false;
            }
        }

        // 先持久化成功再更新内存（失败回滚内存 = 不更新）
        var current = Items().ToList();
        current.Add(normalized);
        current.Sort(StringComparer.Ordinal);
        await _store.ReplaceBannedSongsAsync(current, ct);

        lock (_lock)
        {
            if (keyword is not null)
            {
                _keywords.Add(keyword);
            }
            else
            {
                _exact.Add(normalized);
            }
        }

        return true;
    }

    /// <summary>移除规则；返回是否实际存在。先写盘成功再更新内存。</summary>
    public async Task<bool> RemoveRuleAsync(string rule, CancellationToken ct = default)
    {
        var normalized = (rule ?? "").Trim().ToLowerInvariant();
        if (normalized.Length == 0)
        {
            return false;
        }

        lock (_lock)
        {
            var keyword = normalized.StartsWith("*", StringComparison.Ordinal) ? normalized[1..].Trim() : null;
            var exists = keyword is not null ? _keywords.Contains(keyword) : _exact.Contains(normalized);
            if (!exists)
            {
                return false;
            }
        }

        var current = Items().ToList();
        current.RemoveAll(item => string.Equals(item, normalized, StringComparison.Ordinal));
        await _store.ReplaceBannedSongsAsync(current, ct);

        lock (_lock)
        {
            if (normalized.StartsWith("*", StringComparison.Ordinal))
            {
                _keywords.Remove(normalized[1..].Trim());
            }
            else
            {
                _exact.Remove(normalized);
            }
        }

        return true;
    }
}
