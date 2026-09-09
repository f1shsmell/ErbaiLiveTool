# Stage B · Inno Setup 安装器配方（决策 #14 修订）

把发布形态从「zip 绿色分发」转为 **FDD + Inno Setup 安装器**：框架依赖（不内置
.NET 运行时）、WinAppSDK 随应用自带、前置组件缺失时在线拉取静默安装；安装目录
`%LOCALAPPDATA%\Programs\ErbaiLiveTool`（用户级）；随安装器创建插件目录
`Plugins\` 与说明模板（Stage A 遗留 ④；config.ini 样例以文本形式并入 README，不附带样例目录——无 DLL 的样例目录会在插件页显示「加载失败」并被误操作）。

## 文件清单

| 文件 | 说明 |
|---|---|
| `ErbaiLiveTool.iss` | 安装器脚本（参考 FufuLauncher `setup.iss` 配方，见 docs/05 §1） |
| `ChineseSimplified.isl` | 官方社区中文语言文件（Inno Setup 6.5.0+，维护者 Zhenghan Yang（Kira）） |
| `plugins-template/` | 安装到 `{app}\Plugins\` 的插件目录说明（`README.txt`，内含 config.ini 文本样例与用法、故障排查；不附带样例目录） |
| `Output/` | 编译产物（不入库，见 .gitignore）：安装版 Setup.exe + 绿色版 zip（测试用）+ 编译日志 |

## 安装器行为

1. **前置检测**（NextButtonClick / wpReady 页）逐项进行，缺失才拉取：
   - .NET 8 Desktop Runtime —— 检测 `dotnet\shared\Microsoft.WindowsDesktop.App\8.0.*` /
     注册表 `InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App`；下载
     `aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe`，`/install /passive /norestart`；
   - **ASP.NET Core Runtime 8.0**（`Erbai.Web` 的 `Microsoft.AspNetCore.App` 框架依赖，
     FufuLauncher 无内嵌 Kestrel 故其脚本没有此项，属本仓库必要增补）——检测
     `Microsoft.AspNetCore.App\8.0.*` / 对应注册表；下载
     `aka.ms/dotnet/8.0/aspnetcore-runtime-win-x64.exe`，参数同上；
   - VC++ v14 Redist —— 检测 `HKLM\...\VisualStudio\14.0\VC\Runtimes\x64` `Installed=1`；
     下载 `aka.ms/vs/17/release/vc_redist.x64.exe`，`/install /passive /norestart`；
   - WebView2 —— 检测 EdgeUpdate Clients `{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}` `pv`；
     需要时下载 `go.microsoft.com/fwlink/?linkid=2124701`，`/silent /install`；
   - 组件部署失败**不中断主程序安装**（弹消息提示稍后手动补装，参考 FufuLauncher 语义）。
2. **不部署** WindowsAppRuntime（`WindowsAppSDKSelfContained=true`，运行时随应用自带，
   与 FufuLauncher 默认形态不同）。
3. **插件目录**：`[Dirs]` 创建 `{app}\Plugins` + 模板；卸载仅删模板与空目录，
   **保留用户自装插件**；config.json / song_request.db / logs 不在安装清单，
   卸载**不删除**（用户数据保留；重装覆盖同样不覆盖，与绿色版分发「保留不覆盖」惯例一致）。
4. 会话须知约束：安装包 ≤120MB（#3 修订指标）；x64-only；整体提权
   （`PrivilegesRequired=admin`，与 App 的 `requireAdministrator` 一致）。
5. **绿色版**：`tools/build-installer.ps1` 末尾把同一份 publish 打包为
   `ErbaiLiveTool-<ver>.zip`（测试/快速拷贝用；桌面分发目录已退役 2026-08-27）。

## 构建

```powershell
powershell -ExecutionPolicy Bypass -File tools\build-installer.ps1
```

脚本步骤：① `dotnet publish` App（`-r win-x64 -p:Platform=x64`）/ Connector / Grabber
并汇入 `publish\`，删 `*.pdb` 并断言发布清单（9 必选文件、无 pdb）；② `ISCC` 编译安装版
Setup.exe 并断言 ≤120MB；③ 打包绿色版 zip（同一份 publish）。
版本号自动取自 `ErbaiLiveTool.exe` FileVersion（3 段）。遇 NuGet 劫持先
`set HTTPS_PROXY=http://127.0.0.1:7890`。ISCC 未装时以 `-IsccPath` 指定或安装
Inno Setup 6（本机开发可用 `C:\Dev\InnoSetup6`）。

## 手动验收清单（本 shell 无管理员权限，UAC 场景需用户执行）

1. 运行 `installer\Output\ErbaiLiveTool_Setup_v0.1.0.exe`（UAC 确认）；
2. 首次安装（本机若已装 .NET 8 Desktop/ASP.NET Core/VC++/WebView2 会跳过部署，
   观察自定义中文消息「正在获取必要组件 …」消失并进入主安装）；
3. 安装完成自动启动 → 主界面出现（requireAdministrator 提权正常）；
4. 检查 `%LOCALAPPDATA%\Programs\ErbaiLiveTool\`：全部发布文件 + `Plugins\` 目录
   （含 `README.txt`，无 hello-sample 样例目录）；
5. 启动日志无「插件加载失败 …」行（内置 queueup/giftfx 正常加载）；
6. 放一个真实插件目录到 `Plugins\` → 重启 → 插件页/日志确认加载；
7. 卸载后：`config.json` / `song_request.db` / `logs` 保留；`Plugins` 用户自装内容保留；
8. 体积：Setup.exe ≤ 120MB（构建脚本已断言）。