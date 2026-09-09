# vendor/

Awoo 播放器连接器（`Awoo.Connector.*.exe`，NDJSON-stdio 协议，协议规范见
`docs/04-协议与接口契约.md` §1.4）。

**角色（2026-08-23 决策 #15 起）**：连接器接入上游（`Erbai.Connector`），本目录的
vendor exe **不再随应用复用/发布**，仅作两项用途：① 读配套源码理解机制与数值
（偏移地址/管道名/profile 签名——功能性事实，不受版权保护）；② 新旧连接器并行
黑盒对照（同一命令序列比对响应，参照阶段 4 Grabber 对比法）。

## 来源与许可

- 上游：`https://github.com/Enkianssus/awoo-connectors`（Awoo MusicBot 独立版本化
  原生播放器连接器，即旧 BiliNCM-Connectors 更名续作，**无 license**，不得复制其
  代码表达；机制与数值可参考，见 02-决策记录 #15）。
- 每个连接器独立版本化，标签形如 `<player>-v<版本>`；此处引用各平台**最新 release**
  的 `*-framework-dependent.zip`（2026-08-29 刷新）：

  | 连接器 | 版本（release tag） | 目标播放器版本 | 架构 |
  | --- | --- | --- | --- |
  | Folia | 1.1.3（`folia-v1.1.3`） | Stage API 基线（无播放器版本号） | win-x86 |
  | Kugou | 20.1.41.1（`kugou-v20.1.41.1`） | 酷狗 20.1.41.27870 | win-x86 |
  | Netease | 3.1.38.205386.1（`netease-v3.1.38.205386.1`） | 网易云 3.1.38.205386 | win-x64 |
  | QQMusic | 22.60.1（`qqmusic-v22.60.1`） | QQ 音乐 22.60 | win-x86 |

- **打包形态**：上游 v2 起只发布**框架依赖（framework-dependent）**包——每连接器
  一个子目录，内含 exe + dll + `*.deps.json` + `*.runtimeconfig.json` +
  `Microsoft.Windows.SDK.NET.dll` + `WinRT.Runtime.dll`（共约 26MB），**不带 .NET
  运行时**。运行需本机 .NET 8 运行时：Netease 需 x64，Kugou/QQMusic/Folia 需 x86
  （当前开发机未装 x86 runtime，做黑盒对照前需先装 `.NET Desktop Runtime 8 (x86)`；
  亦可 `--old-exe` 指向别处已装好运行时的副本）。
- `netease/bridge/AwooNcmCefBridge.dll`：网易云 CEF 注入桥，与 exe 同源、版本锁定于
  网易云 3.1.38.x。注意它**已与 `src/Erbai.Connector/bridge/` 内置的桥不同**
  （旧桥锁定 3.1.37.x，SHA256 见下）；vendor 仅作机制参考，**不要**直接覆盖应用内置
  桥——应用内置桥要跟着用户实际安装的网易云客户端版本走。
  - vendor（3.1.38 版）：`197FE512820000A829B55C829183CCC906B211BD21C7F0450FB4D1646DD3BE63`
  - 应用内置（3.1.37 版）：`1F905210F41A8CBECCA556839AD6AC7DDD180A164D82C624F6F9A151ACC0F9D3`
- `qqmusic/profiles/qqmusic/`：QQ 音乐版本签名 profile JSON（22.22 / 22.41 / 22.51 /
  22.52 / 22.60 五个），与 22.60.1 exe 同源（连接器可参考其数值）。22.22/22.41/
  22.51 与上一版相同，22.52/22.60 为新增。

## 刷新方法

```powershell
# 以最新 release 为准，逐平台下载并解压到 vendor/<player>/：
#   https://github.com/Enkianssus/awoo-connectors/releases/download/<tag>/awoo-connector-<player>-<ver>-win-<arch>-framework-dependent.zip
# 下载同名的 .sha256 sidecar 校验（Ed25519 .sig 供上游 Awoo MusicBot 客户端验证）。
# 连接器可参考的机制变更点（上游 README）：
# - netease 3.1.38：限流搜索响应识别（不再误报空结果）+ 兼容搜索端点回退 + Unicode 元数据规范化比较；
# - kugou 20.1.41：桌面 ticker 临时改间距/加本地化标题/回退旧 KuGou.ini 时保持当前曲目身份稳定；
# - qqmusic 22.60：AddSongs(mode=0) 精确单曲插入 + guarded-next（同 22.52 的
#   (纯音乐)/(Inst.)/(Instrumental) 别名处理）。
```

- `tools/CompareConnectors` 默认旧连接器路径已随目录结构调整为
  `vendor/<player>/Awoo.Connector.<player>.exe`。
