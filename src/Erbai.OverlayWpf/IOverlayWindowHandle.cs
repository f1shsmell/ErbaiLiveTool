using Erbai.Contracts.Configuration;

namespace Erbai.OverlayWpf;

/// <summary>
/// 悬浮窗治理句柄（决策 #17）：<c>OverlayWindowManager</c> 统一管理 WinForms 自绘
/// （点歌/排队）与 WPF（弹幕，bililive_dm 同款）两类运行时。
/// </summary>
public interface IOverlayWindowHandle : IDisposable
{
    void SetTopmost(bool flag);

    void SetClickThrough(bool flag);

    void ApplyStyle(OverlayWindowStyleConfig style);

    /// <summary>
    /// 尺寸热切换（2026-09 改语义）：宽高全自适应——点歌/排队按内容实测宽高，
    /// 弹幕窗用内置默认宽 + 高度随活跃行数伸缩。参数保留以兼容调用方，宽高值被忽略。
    /// </summary>
    void ApplySize(int width, int height, bool autoHeight);

    /// <summary>
    /// 当前窗口<b>右下角</b>屏幕坐标（2026-09 位置持久化）；窗口未就绪返回 null。
    /// 取右下角而非左上角：宽高自适应时窗口向左上生长，右下角锚定才不会随内容漂移。
    /// </summary>
    (int Right, int Bottom)? AnchorBottomRight { get; }

    /// <summary>关闭窗口并结束其线程（幂等）。</summary>
    void Close();
}
