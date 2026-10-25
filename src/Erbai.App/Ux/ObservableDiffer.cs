using System.Collections.ObjectModel;

namespace Erbai.App.Ux;

/// <summary>
/// ObservableCollection 差量更新（2026-10 精简轮）：按键对齐——已有项原位更新、
/// 消失项移除、按目标顺序 Move/Append。保 ListView 选中与滚动位置。
/// 原为 QueuePage/QueueUpPage 各自复制一份的 45 行算法（逐字节相同）。
/// </summary>
public static class ObservableDiffer
{
    public static void Apply<TItem, TDesired>(
        ObservableCollection<TItem> collection,
        IReadOnlyList<TDesired> desired,
        Func<TDesired, long> keyOfDesired,
        Func<TItem, long> keyOfItem,
        Func<TDesired, TItem> create,
        Action<TItem, TDesired>? update = null)
        where TItem : class
    {
        var existing = new Dictionary<long, TItem>(collection.Count);
        foreach (var item in collection)
        {
            existing[keyOfItem(item)] = item;
        }

        var planned = new List<TItem>(desired.Count);
        var keep = new HashSet<TItem>(desired.Count);
        foreach (var item in desired)
        {
            if (existing.Remove(keyOfDesired(item), out var found))
            {
                update?.Invoke(found, item);
            }
            else
            {
                found = create(item);
            }

            keep.Add(found);
            planned.Add(found);
        }

        for (var i = collection.Count - 1; i >= 0; i--)
        {
            if (!keep.Contains(collection[i]))
            {
                collection.RemoveAt(i);
            }
        }

        for (var i = 0; i < planned.Count; i++)
        {
            if (i < collection.Count)
            {
                if (!ReferenceEquals(collection[i], planned[i]))
                {
                    collection.Move(collection.IndexOf(planned[i]), i);
                }
            }
            else
            {
                collection.Add(planned[i]);
            }
        }
    }
}
