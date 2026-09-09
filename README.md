# ErbaiLiveTool（二白直播工具）

## 支持的直播平台

| 平台       | 弹幕获取方式                                                             | 机制            | 说明           |
| -------- | ------------------------------------------------------------------ | ------------- | ------------ |
| 抖音       | [DouyinBarrageGrab](https://github.com/ape-byte/DouyinBarrageGrab) | 基于系统代理抓包      | 记得先开启监听再开始直播 |
| bilibili | [blivedm](https://github.com/xfgryujk/blivedm)                     | 使用WebSocket协议 | 扫码登录         |

## 支持的播放器平台

除了落雪音乐 lxmusic，其他四个平台连接器均来自[Enkianssus](https://github.com/Enkianssus)的 [awoo-connectors](https://github.com/Enkianssus/awoo-connectors)

| 平台           | 连接器                                               | 说明                                 |
| ------------ | ------------------------------------------------- | ---------------------------------- |
| 落雪音乐 lxmusic | 官方三通道（Scheme/HTTP/SSE）                            | 推荐使用                               |
| Folia        | Stage 本地 HTTP/WS（32107，token 环境变量注入）              | 需在 Folia 设置开启 Stage Mode 并配置 token |
| 网易云音乐        | CEF bridge 注入 + 命名管道（版本锁定 3.1.38.205386）          |                                    |
| 酷狗音乐         | 窗口/ini/共享内存 + WM_COPYDATA 插歌 + 签名搜索               |                                    |
| QQ 音乐        | 版本画像 profile + x86 trampoline 补丁（AddSongs mode=0） |                                    |

## 开发环境要求

- Windows 10/11 x64
- .NET SDK 8.0（本仓库 `global.json` 锁 8.0.x）
- NuGet

## 构建

```bash
dotnet build ErbaiLiveTool.sln -c Release -p:Platform=x64
dotnet test                                                  # 单测
dotnet publish src/Erbai.App -c Release -r win-x64 -p:Platform=x64 -o publish
```

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
- **awoo-connectors**：落雪音乐以外的播放器连接器均参考
  [Enkianssus/awoo-connectors](https://github.com/Enkianssus/awoo-connectors)
  的连接器机制与数值（上游无许可证，仅作机制参考、未复制其代码表达；
  vendored 二进制不入库，对照说明见 [`vendor/README.md`](vendor/README.md)）。
- **抖音弹幕抓包**：基于 [ape-byte/DouyinBarrageGrab](https://github.com/ape-byte/DouyinBarrageGrab)
  的系统代理抓包思路；**bilibili 弹幕**：参考 [blivedm](https://github.com/xfgryujk/blivedm)
  协议行为实现参考 blivedm（Apache-2.0，不包含其代码）。

## 许可

本项目基于 MIT License（见 [`LICENSE`](LICENSE)），使用时仍需遵守抖音、Bilibili
及各音乐平台的服务条款。
