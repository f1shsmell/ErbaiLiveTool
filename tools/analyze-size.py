"""按类别统计 publish-loose 的体积构成：未压缩 vs zlib 压缩后（近似单文件压缩）。"""
import os
import zlib
from collections import defaultdict

ROOT = "publish-loose"

def category(name):
    n = name.lower()
    if n.startswith("erbai"): return "① 应用自身代码（Erbai.*）"
    if "sdk.net" in n or n.startswith("microsoft.windows.sdk"): return "② Windows API 投影 (SDK.NET)"
    if any(k in n for k in ("winui", "microsoft.ui.xaml", "microsoft.windowsappruntime")) or n.startswith("microsoft.windowsapp"):
        return "③ WinUI3/WASDK 运行时"
    if any(k in n for k in ("presentationframework", "presentationcore", "windowsforms", "system.windows.forms",
                            "presentation", "windowsbase", "system.xaml", "reachframework", "system.windows.primitives")):
        return "④ WinForms+WPF 桌面框架（悬浮窗壳）"
    if "webview2" in n: return "⑤ WebView2 适配器"
    if n.startswith(("system.", "microsoft.", "netstandard", "windowsbase")): return "⑥ .NET 运行时+BCL（自包含）"
    if n.endswith((".pri", ".xbf", ".json")): return "⑦ 资源文件"
    return "⑧ 其他原生/杂项"

raw = defaultdict(int)
comp = defaultdict(int)
cnt = defaultdict(int)
for dirpath, _, files in os.walk(ROOT):
    for f in files:
        p = os.path.join(dirpath, f)
        data = open(p, "rb").read()
        c = category(f)
        raw[c] += len(data)
        comp[c] += len(zlib.compress(data, 6))
        cnt[c] += 1

MB = 1048576
total_raw = total_comp = 0
print(f"{'类别':<38}{'文件数':>5}{'未压缩':>10}{'zlib压缩后':>11}")
for c in sorted(raw):
    print(f"{c:<38}{cnt[c]:>5}{raw[c]/MB:>8.1f}MB{comp[c]/MB:>9.1f}MB")
    total_raw += raw[c]; total_comp += comp[c]
print("-" * 66)
print(f"{'合计':<38}{sum(cnt.values()):>5}{total_raw/MB:>8.1f}MB{total_comp/MB:>9.1f}MB")
