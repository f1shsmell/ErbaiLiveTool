# ErbaiLiveTool（二白直播工具）

## 支持的直播平台

| 平台       | 弹幕获取方式                                                             | 机制            | 说明           |
| -------- | ------------------------------------------------------------------ | ------------- | ------------ |
| 抖音       | [DouyinBarrageGrab](https://github.com/ape-byte/DouyinBarrageGrab) | 基于系统代理抓包      | 记得先开启监听再开始直播 |
| bilibili | [blivedm](https://github.com/xfgryujk/blivedm)                     | 使用WebSocket协议 | 扫码登录         |

## 支持的播放器平台

**落雪音乐 lxmusic** 由本仓库原生支持。其余四个平台（网易云 / 酷狗 / QQ 音乐 / Folia）
以**插件**形态接入：使用 [Enkianssus](https://github.com/Enkianssus) 的
[awoo-connectors](https://github.com/Enkianssus/awoo-connectors) 第三方连接器，
由**用户自行下载**（应用内「插件」页按上游清单安装、校验签名并自动维护更新）。
本仓库不内置、也不分发这四个平台的连接器实现与相关二进制。

| 平台           | 连接器                             | 说明                                 |
| ------------ | ------------------------------- | ---------------------------------- |
| 落雪音乐 lxmusic | 本仓库原生（Scheme / HTTP / SSE 三通道）   | 内置，推荐使用                            |
| 网易云音乐        | 第三方插件 `awoo-connectors`（用户自行安装） |                                    |
| 酷狗音乐         | 第三方插件 `awoo-connectors`（用户自行安装） |                                    |
| QQ 音乐        | 第三方插件 `awoo-connectors`（用户自行安装） |                                    |
| Folia        | 第三方插件 `awoo-connectors`（用户自行安装） | 需在 Folia 设置开启 Stage Mode 并配置 token |

未安装插件时，播放器下拉里对应平台会灰显并给出下载指引；lxmusic 随应用发布，无需安装。

## 播放器连接器插件化

本仓库已完成「仅保留 lxmusic 原生支持」的改造，要点：

- 四个插件平台走**清单自动安装**（上游 v2 catalog + Ed25519 签名 + SHA-256 校验 +
  健康检查 + 后台定时维护），而非手工放目录。
- x86 平台（酷狗 / QQ 音乐 / Folia）所需运行时由应用自动安装**私有共享 .NET 运行时**，
  不要求用户预装 x86 .NET。
- 因此本仓库不再内置、也不再分发这四个平台的连接器实现与相关二进制
  （含 `src/Erbai.Connector/bridge/AwooNcmCefBridge.dll`）。

## 开发环境要求

- Windows 10/11 x64
- .NET SDK 8.0（本仓库 `global.json` 锁 8.0.x）
- NuGet

## 构建

```bash
dotnet build ErbaiLiveTool.sln -c Release -p:Platform=x64
dotnet test -c Release -p:Platform=x64                     # 单测（x64 必需，见下）
dotnet publish src/Erbai.App -c Release -r win-x64 -p:Platform=x64 -o publish
```

> `-p:Platform=x64` 不能省：`Erbai.App` 与 `Erbai.App.Tests` 是
> `<Platforms>x64</Platforms>`，缺省平台会报
> `WindowsAppSDKSelfContained requires a supported Windows architecture`。

构建安装包（Stage B，Inno Setup；需已安装 Inno Setup 6）：

```powershell
powershell -ExecutionPolicy Bypass -File tools\build-installer.ps1
# 产物（installer\Output\）：
#   安装版 ErbaiLiveTool_Setup_v0.1.0.exe —— 正式分发（≤120MB 自动断言）
#   绿色版 ErbaiLiveTool-0.1.0.zip      —— 测试/快速拷贝，与安装版同源同版本
```

不想敲命令的话，双击仓库根目录的 `build-installer.cmd` 即可（效果相同）。

发布形态：框架依赖 + WinAppSDK 随应用自带，**一门出双形态**（`installer/` 配方 + `tools/build-installer.ps1`，Stage B）：
**安装版** = Inno Setup 安装器，安装到 `%LOCALAPPDATA%\Programs\ErbaiLiveTool`，自动部署前置组件
（.NET 8 Desktop Runtime / ASP.NET Core Runtime 8.0 / VC++ v14，缺失时在线拉取静默安装）

## 第三方组件

- **抖音直播抓取器**（`src/Erbai.Live.Douyin.Grabber/`）：源自 MIT 开源项目
  （© 2022 一只小白猿），随附其原始 `LICENSE` 与 `免责声明.txt`，未作改动。
- **awoo-connectors**（落雪音乐以外的播放器连接器来源）：网易云 / 酷狗 / QQ 音乐 /
  Folia 四个平台使用 [Enkianssus/awoo-connectors](https://github.com/Enkianssus/awoo-connectors)
  发布的第三方连接器，由用户自行下载安装，**不随本仓库分发**。
  该上游**未附任何许可证**。本仓库此前曾内置这四个平台的连接器实现
  （机制与数值参考上游，**实现代码表达沿用上游**，故该部分不在 MIT 许可范围内）；
  该实现已随「播放器连接器插件化」从**当前代码树**中移除，
  但**仍存在于本仓库的历史提交中**，因此历史版本中该部分同样不受 MIT 覆盖。
  vendored 二进制不入库，对照说明见 [`vendor/README.md`](vendor/README.md)。
- **（已移除）网易云 CEF bridge**（原 `src/Erbai.Connector/bridge/AwooNcmCefBridge.dll`）：
  上游二进制、上游同样未附许可证。已随插件化从当前代码树移除，安装包不再分发；
  但**仍存在于本仓库的历史提交中**。
- **抖音弹幕抓包**：基于 [ape-byte/DouyinBarrageGrab](https://github.com/ape-byte/DouyinBarrageGrab)
  的系统代理抓包思路；**bilibili 弹幕**：协议行为参考
  [blivedm](https://github.com/xfgryujk/blivedm)（Apache-2.0，不包含其代码）。

## 许可

本项目基于 MIT License（见 [`LICENSE`](LICENSE)）。**该许可仅覆盖本仓库原创代码**：
上节「第三方组件」中标注为「上游未附许可证」的部分不在 MIT 许可范围内，
使用者需自行评估并承担相应合规风险。

使用时仍需遵守抖音、Bilibili 及各音乐平台的服务条款。
