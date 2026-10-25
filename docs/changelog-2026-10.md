# 2026-10 变更记录（稳定性 / 交互 / 美化 / 布局 / 长稳 / 精简）

> 六轮迭代的完整变更明细。产品当前状态见仓库根 [README](../README.md)。

## 必要性审查与精简轮

**必要性审查结论**（三路审计：依赖图 / 死代码 grep 验证 / 重复逻辑）：

- 15 个 src 项目依赖图无孤儿、职责边界（契约/宿主/平台/模块/连接器进程）均成立，
  全部保留；测试与工具按"是否仍可运行、是否被文档引用"取舍。
- 审计确认的死代码已全部移除（约 20 处，每处均有全仓零引用证据）：
  未接线的 `SongRequestModule`、孤立 `SongIdentity`、空壳 `ApiConfig` 与
  `AppConfig.Api`、无消费者的 `OverlayConfig` + `/api/v1/overlay/config|lyric`
  端点、悬浮窗弃用宽高字段、抖音抓包器六个从未读取的配置字段（mode/autoStart/
  autoStop/adoptExisting/startOnLaunch/configPath）、Ui 四个死字段（enabled/host/
  openBrowserOnStart/overlayEnabled）、`Runtime.AutoStartPlatforms`、
  `Player.AutoStart/ConnectorDir`、从未产生的 `RecoveryRequired` 状态、从未设置的
  `LiveCapabilities.Subscribe`、从未被消费的 `PlayerCapabilities.Search/PauseResume`、
  B站登录 `ClearAsync`/心跳人气解析、`ConnectionState.Connecting` 占位、
  `DeleteNonAdminUsersAsync`、`LogLevel.Fatal`、vendored 抓包器 9 个死扩展方法、
  悬浮窗页残留的 `?token=` JS。
- vendored 抓包器 protobuf DTO 经审计保留（反序列化契约，非死代码）。

**结构精简**

- 删除 tools/ 下三个一次性/失效工具（CompareGrabbers 验收已完成、两个真网冒烟
  依赖外部环境且其一 csproj 引用绝对路径已损坏）与五个一次性脚本；保留
  ConnectorSmoke/ProbeConnector（vendor/README 文档引用）与构建脚本。
- **切断依赖方向错误**：抖音平台插件此前引用点歌模块只为复用用户持久化——
  抖音用户合并语义移回 `DouyinUserSync` 自持（平台自持，点歌模块不再被平台依赖）。
- 平台插件生命周期基类 `LivePluginBase`（事件通道/启停/Dispose，两插件逐字节
  相同的脚手架收编；监听循环刻意不合并——两者是不同状态机）。
- 组合根 runner 桥接循环收敛、`QueuePage/QueueUpPage/OverviewPage` 订阅泵
  （`Ux.ViewPump`）与差量渲染（`Ux.ObservableDiffer`）收敛、
  `ConnectorClient.FailAllPending` 收敛。
- `Erbai.App.csproj` 补 Erbai.Connector 的构建顺序引用（原文件复制在单项目构建
  时静默 no-op）。

**给本机环境的备忘**：360 杀毒（360rp.exe）实时扫描会锁住新落盘的 DLL/引用程序集
（MSB3021/CS2012/"没有可用测试"），与代码无关——建议将本仓库目录加入 360 白名单。
构建层已加 Copy 重试兜底，测试工程对抖音测试关闭引用程序集产出。

## 2026-10 长稳轮（挂机数小时场景）

**抖音抓包器死亡自愈**

- 直播中途抓包器进程意外死亡时，插件每 5s 空转重连、弹幕静默丢失直到下播——
  现在连续 3 次连不上本地 WS 端口即回调宿主重拉 Grabber（`DouyinLivePlugin.Reviver`，
  组合根接 `DouyinGrabberHost.StartAsync` 幂等语义：端口活着即复用、死了走完整拉起链）；
  两次拉起间隔至少 30s 防风暴；回调异常被隔离，下一失败窗口再试。

**插件热切换修复（LoadFromStream）**

- `Assembly.LoadFrom` 会对插件 DLL 保持内存映射，`ALC.Unload` 后映射异步释放——
  快速「热禁用 → 热启用」同一插件时新的加载撞上旧映射报 0x80070020（文件被占用）。
  改为读入字节后 `LoadFromStream`，不留任何文件句柄，卸载只剩纯内存回收。
  （此问题同时治愈了 PluginLoader 测试在本机杀软环境下的长期间歇性失败。）

**可观测与数据治理**

- **事件总线丢弃可观测**：慢消费者（页面卡住/消费者停摆）导致的通道溢出丢弃不再
  静默——计数 + 30s/订阅节流上报，组合根接日志告警（"订阅队列溢出，丢弃 N 条"）。
- **历史数据保留清理**：`storage.retention_days`（默认 14，0=永久）——启动即清一次、
  此后每 6 小时清理超期的 `queue_events` 审计行与终态点歌请求（活动请求永不清理；
  仍被事件引用的请求跳过保外键）。长期直播不再无限增长拖慢历史页与备份。
- `queueup_entries` 补 status 索引（既有库下次打开自动补建）。

**窗口与悬浮窗韧性**

- 悬浮窗数据轮询改为**顺序化异步循环**（旧 `Timer`+同步阻塞在服务端变慢时回调
  重叠堆积、占满线程池）。
- 悬浮窗构造等待补 5s 超时兜底（旧 `Task.Result` 无超时，窗线程挂住会永久阻塞）。
- **主窗口位置/尺寸记忆**：退出采集、启动复位，恢复前按屏幕边界钳制——拔掉外接
  显示器后窗口不会落在不存在的区域"消失"（钳制逻辑纯函数化并有单测）。

**测试基建**

- 插件 ALC 测试类互斥串行；种子复制幂等 + 抗杀软扫描重试；csproj 插件复制加重试；
  两处关键断言带诊断消息（失败时直接给出 SetEnabledAsync 返回的失败原因）。

## 2026-10 布局样式轮

- **设置页 SettingsCard 化**：12+ 处手写 `140/*` 标签网格迁移到 CommunityToolkit
  `SettingsCard`（Windows 11 设置观感：左侧标题/描述、右侧控件、悬停反馈），
  小节标题配品牌色图标；全部命名元素与事件原样保留（代码层零改动）。
- 点歌队列页状态列加**状态点**（播放/搜索中=强调橙、等待=绿、其他=中性灰）。
- 空态组件图标收进**品牌章**（与页头组件同一视觉语言）。
- 主要列表（点歌/排队/日志/历史）行增加 6px 圆角与既有悬停反馈配套。
- 管理页 / 显示页 Pivot 分区标题 18 SemiBold（`AppPivotStyle`）。
- 日志页「自动滚动」开关改为行内紧凑布局（原 Header 悬在开关上方错位）。

## 2026-10 UI 美化轮

**品牌识别**

- **品牌强调色**：全局强调色由系统蓝改为品牌橙（`#FF7B54`，与悬浮窗强调色同源）——
  导航选中条、开关、主按钮、进度条、焦点框整体获得应用识别度；`ThemeDictionaries`
  提供 Dark1-3/Light1-3 全套色阶保证深浅主题对比度；高对比度模式不受影响。
- **自绘标题栏**：内容延伸进标题栏（Win10/11 一致生效），顶部品牌条 = 应用 logo +
  名称 + 拖拽区；标题按钮前景色按主题自适应。
- **统一页头组件**（`Ux/PageHeader`）：十个页面的手写标题收敛为「品牌橙图标章 +
  标题 + 副标题」，图标与左侧导航一一对应。
- **概览仪表盘**：B站（粉）/ 抖音（青）品牌章、正在播放左侧强调条。

**布局修正**

- 修复内容延伸后 `NavigationView` 自动衬垫（按系统标题栏 32px 计算）与品牌条
  高度（48px）不一致，导致首个导航项被拖拽区覆盖无法点击的问题：导航视图改为
  显式下移并关闭自动衬垫（`IsTitleBarAutoPaddingEnabled=False`）。

## 2026-10 界面与交互改进

**性能 / 响应性（直播高峰下的 UI 流畅度）**

- 弹幕日志页：实时事件由「每条一次全表重建（≤1000 行）+ 滚动」改为**事件缓冲 +
  200ms 节拍批量刷新**；无筛选时增式追加、有筛选时整表重建；搜索输入 250ms 防抖。
  点赞/进入洪泛下 UI 线程不再被弹幕挤占。
- 概览页：实时事件预览同样的缓冲节拍化；连接状态改为 **3s 低频轮询**——平台断线
  时概览徽章会变化（旧实现只在进页/点击时计算，徽章永远停在"监听中"）。
- 点歌队列页 / 排队队列页：渲染由「清空重建」改为 **INPC 差量更新**（按 id 对齐、
  原位更新、按新顺序移动）——每次快照事件不再丢选中、列表不再跳回顶部。
- 显示与悬浮窗页：样式滑杆持久化 **300ms 防抖**——拖动滑杆不再每个刻度写一次盘
  + 热应用；离开页面时未保存的改动立即落盘。

**视觉 / 主题**

- 日志与事件颜色**主题感知**：深色主题下弹幕/礼物/系统日志改用 Material 亮色调
  （旧浅色系深灰字在深底上几乎不可见）；浅色主题保持原深色调。
- 诊断徽标对比度：WARN 琥珀底改配深色文字（原白字对比度不足）。
- 插件页硬编码 `Red` 改 `SystemFillColorCriticalBrush`（主题感知错误色）。

**细节修正**

- 点歌队列页表头与数据列对齐（表头内边距 + 操作列宽度）。
- 概览「实时事件」卡片新增空态引导（与其他页面一致的 EmptyState 组件）。
- 管理页四个分区补齐空态提示；行内按钮统一小尺寸样式；用户/管理员搜索框回车即搜；
  分区内容包滚动容器（矮窗口不再裁剪）。
- 历史页加载期间显示进度环并禁用刷新/翻页（防双击并发查询互相覆盖）。
- 设置页 / 显示页 / 管理页启用 `NavigationCacheMode`——切页保留滚动与未保存的
  输入状态（管理页数据在每次进入时重新拉取，不会陈旧）。
- 概览页队列/排队空态判定改为看预览列表实际内容（仅剩"正在播放"一行时不再误显空队列）。

**打包行为变更**

- 应用清单由 `requireAdministrator` 改为 **`asInvoker`**：本应用只写自身目录与
  HKCU 注册表（代理开关），无任何需要管理员的操作；官方安装器本就安装到
  `%LOCALAPPDATA%`（用户目录）。**改动后启动不再每次弹 UAC**。若你手动把绿色版
  放进了需要管理员才能写入的目录（如 `C:\Program Files`），请移到用户目录下运行。

## 2026-10 稳定性与体验改进

**连接韧性（B站）**

- 断线重连由固定 1s 改为**指数退避 + ±20% 抖动**（基值 1s、封顶 60s，连接稳定 ≥60s 后
  计数清零）——网络断开/房间关闭时不再每秒打满 init 链（buvid3 → get_info →
  getDanmuInfo + WBI 签名），降低触发风控的风险。
- 收帧循环新增**空闲看门狗**：90s（3 × 心跳间隔）未收到任何帧（含心跳回复）即判定
  半开连接失联并强制重连，不再无限挂起在 `ReceiveAsync` 上。

**点歌模块**

- 新增**点歌频率限制**（防刷屏）：单用户窗口内（默认 10 分钟）最多有效提交 3 次，
  被拒提交不计入窗口、失败提交计入；admin/anchor 在 AdminBypass 开启时豁免。
  配置键 `permissions.rate_limit_enabled / rate_limit_window_seconds / rate_limit_max_requests`
  （校验范围 10–86400 秒 / 1–100 次），检查序位于等级门槛之后、重复点歌之前。
- 修复**空闲歌单热启用缺口**：主播把空闲歌单从关切到开（或热更新列表）现在立即开播，
  不再需要等下一首点歌触发。
- 修复点歌队列 worker 的**孤儿读取器**问题：唤醒信号与点歌请求竞争等待时，被抛弃的
  读取任务可能抢先消费后续请求导致点歌永久停在 queued（E2E 实测复现后修复）。
- 修复 `StartAsync` 并发双启动可能创建双 worker 的竞争（提交门闩串行化 + 双检）。

**排队模块（queueup）**

- 「完成」命令的特权判定与点歌切歌**对齐**：弹幕不带平台标志时回落应用内持久化的
  管理员/主播身份（跨房间合并）——「设置管理员@XX」配置的管理员现在可以完成队首。
- 锁模型修订：同步只读接口（`Count`/`Snapshot`/`HasEligibility`）不再阻塞等待存储 I/O，
  UI 线程调用不会因队列写入中的 I/O 而冻结。

**其他**

- 歌曲黑名单写路径串行化：修复设置页编辑与弹幕拉黑命令并发时互相覆盖丢规则的竞争。
- Overlay WebSocket 会话结束回收发送锁条目，修复慢客户端断连后的内存泄漏。
- 本机手动注入弹幕（概览/队列/历史页输入框）归属**当前配置的平台与房间**，
  不再硬编码 douyin/房间 1——B站直播时手动测试命令同样生效。

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
