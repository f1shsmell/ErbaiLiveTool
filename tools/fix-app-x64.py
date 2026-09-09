"""修正 sln：Erbai.App 的 x64 解决方案配置必须映射到工程的 x64 配置（WinUI3 自包含需要）。"""
P = "ErbaiLiveTool.sln"
s = open(P, encoding="utf-8-sig").read()
g = "{1EE358FE-C54B-4FB9-BB96-05F8242E1F25}"
pairs = [
    (f"{g}.Debug|x64.ActiveCfg = Debug|Any CPU", f"{g}.Debug|x64.ActiveCfg = Debug|x64"),
    (f"{g}.Debug|x64.Build.0 = Debug|Any CPU", f"{g}.Debug|x64.Build.0 = Debug|x64"),
    (f"{g}.Release|x64.ActiveCfg = Release|Any CPU", f"{g}.Release|x64.ActiveCfg = Release|x64"),
    (f"{g}.Release|x64.Build.0 = Release|Any CPU", f"{g}.Release|x64.Build.0 = Release|x64"),
]
n = 0
for old, new in pairs:
    if old in s:
        s = s.replace(old, new)
        n += 1
open(P, "w", encoding="utf-8").write(s)
print(f"fixed {n}/4 lines")
