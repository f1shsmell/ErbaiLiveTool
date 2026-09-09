using Erbai.Contracts.Abstractions;
using Erbai.Contracts.Storage;

namespace Erbai.Modules.SongRequest.Services;

/// <summary>
/// 用户领域服务：保存用户时合并持久化特权（不降级、anchor 不隐式升 admin），
/// 抖音"显式标志"精细语义也在此收敛。
/// </summary>
public sealed class UserService(IStorageEngine store)
{
    /// <summary>
    /// 合并抖音消息携带特权与持久化用户（显式标志才覆盖存储值；普通消息
    /// 不得降级已持久化的 admin/anchor）。返回 (isAdmin, isAnchor)。
    /// </summary>
    public static (bool IsAdmin, bool IsAnchor) MergeDouyinUserFlags(
        User? stored,
        bool messageIsAdmin,
        bool messageIsAnchor,
        bool hasExplicitAdminFlag,
        bool hasExplicitAnchorFlag)
    {
        var effectiveAdmin = messageIsAdmin;
        if (stored is not null && !hasExplicitAdminFlag)
        {
            effectiveAdmin = effectiveAdmin || stored.IsAdmin;
        }

        var effectiveAnchor = messageIsAnchor;
        if (stored is not null && !hasExplicitAnchorFlag)
        {
            effectiveAnchor = effectiveAnchor || stored.IsAnchor;
        }

        return (effectiveAdmin, effectiveAnchor);
    }

    /// <summary>
    /// 保存用户（无"显式标志"语义的调用方，如队列提交）：显式合并存储中的
    /// admin/anchor——admin 只来自消息标志或存储值，普通消息不得清掉已持久化
    /// 的特权。incrementRequestCount 透传存储层（点歌提交路径为 true）。
    /// </summary>
    public async Task SaveWithPrivilegeMergeAsync(User user, bool incrementRequestCount = false, CancellationToken ct = default)
    {
        var stored = await store.GetUserAsync(user.Platform, user.RoomId, user.UserId, ct);
        var effectiveAdmin = user.IsAdmin;
        var effectiveAnchor = user.IsAnchor;
        if (stored is not null)
        {
            effectiveAdmin = effectiveAdmin || stored.IsAdmin;
            effectiveAnchor = effectiveAnchor || stored.IsAnchor;
        }

        await store.SaveUserAsync(user with { IsAdmin = effectiveAdmin, IsAnchor = effectiveAnchor },
            incrementRequestCount, ct);
    }

    /// <summary>抖音用户精细合并保存（弹幕/粉丝团事件路径）。</summary>
    public async Task SaveDouyinUserAsync(
        string roomId,
        string userId,
        string nickname,
        int? fanLevel,
        bool isAdmin,
        bool isAnchor,
        bool hasExplicitAdminFlag = false,
        bool hasExplicitAnchorFlag = false,
        CancellationToken ct = default)
    {
        var stored = await store.GetUserAsync("douyin", roomId, userId, ct);
        var (effectiveAdmin, effectiveAnchor) = MergeDouyinUserFlags(
            stored, isAdmin, isAnchor, hasExplicitAdminFlag, hasExplicitAnchorFlag);
        await store.SaveUserAsync(new User
        {
            Platform = "douyin",
            RoomId = roomId,
            UserId = userId,
            Nickname = nickname,
            IsAdmin = effectiveAdmin,
            IsAnchor = effectiveAnchor,
            FanLevel = fanLevel,
        }, ct: ct);
    }
}
