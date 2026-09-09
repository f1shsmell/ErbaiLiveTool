"""一次性脚本：给 ErbaiLiveTool.sln 增加 Debug/Release|x64 解决方案配置。

App(WinUI3) 映射到 x64，其余工程映射到 Any CPU。
"""
import re

P = "ErbaiLiveTool.sln"
s = open(P, encoding="utf-8-sig").read()

OLD_CFG = ("\tGlobalSection(SolutionConfigurationPlatforms) = preSolution\n"
           "\t\tDebug|Any CPU = Debug|Any CPU\n"
           "\t\tRelease|Any CPU = Release|Any CPU\n")
NEW_CFG = OLD_CFG + "\t\tDebug|x64 = Debug|x64\n\t\tRelease|x64 = Release|x64\n"
if NEW_CFG not in s:
    assert OLD_CFG in s, "solution config section not found"
    s = s.replace(OLD_CFG, NEW_CFG)

app_guid = None
for m in re.finditer(r'Project\("[^"]*"\)\s*=\s*"([^"]+)",\s*"([^"]+)",\s*"\{([^}]+)\}"', s):
    if m.group(1) == "Erbai.App":
        app_guid = m.group(3)
assert app_guid, "Erbai.App project not found"
print("App GUID:", app_guid)

def add_mappings(m):
    guid = m.group(1)
    cfg = "x64" if guid.lower() == app_guid.lower() else "Any CPU"
    return m.group(0) + (
        f"\t\t{guid}.Debug|x64.ActiveCfg = Debug|{cfg}\n"
        f"\t\t{guid}.Debug|x64.Build.0 = Debug|{cfg}\n"
        f"\t\t{guid}.Release|x64.ActiveCfg = Release|{cfg}\n"
        f"\t\t{guid}.Release|x64.Build.0 = Release|{cfg}\n"
    )

s2, n = re.subn(r"(\{[0-9A-Fa-f-]+\})\.Release\|Any CPU\.Build\.0 = Release\|Any CPU\n", add_mappings, s)
assert n > 0, "no project config blocks patched"
open(P, "w", encoding="utf-8").write(s2)
print(f"patched {n} projects")
