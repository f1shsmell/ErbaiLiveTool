namespace Erbai.Contracts.Players;

/// <summary>
/// 播放器命令集（七命令，语义直接平移旧 PlayerPort v2；对齐 AwooMusicBot connector 协议）。
/// </summary>
public enum PlayerCommand
{
    /// <summary>播放选中曲目（替换当前播放）。</summary>
    PlaySelected,

    /// <summary>将曲目插入队首（下一首）。</summary>
    InsertNext,

    /// <summary>强制接管队列（next 对账守卫升级路径）。</summary>
    ArmNextGuard,

    /// <summary>打断当前播放并播放指定曲目。</summary>
    InterruptSelected,

    /// <summary>切下一首。</summary>
    Next,

    /// <summary>暂停。</summary>
    Pause,

    /// <summary>恢复播放。</summary>
    Resume,
}
