# QqMusicProfileGen —— QQ 音乐版本画像半自动生成器

把「QQ 音乐每次升级后最贵的一步——重新定位会漂移的函数/数据 RVA」半自动化：
从旧版 DLL 提取**归一化的指令序列签名**（对绝对地址/相对分支不敏感、对寄存器与栈偏移敏感），
在新版 DLL 里搜索定位，输出候选画像 json。

> 安全模型不变（`docs/04` §1.5.3）：工具产出只是**候选画像**，必须经实机哈希校验 + 点歌
> 端到端验证后，才人工确认写入发布画像。**绝不**放宽「哈希/机器码校验不过就安全拒绝」。
> 本工具只负责「发现 RVA」，不做运行时注入、不碰运行中的播放器。

## 用法

```powershell
dotnet run --project tools/QqMusicProfileGen -- `
  --old-dll      <旧版 QQMusic.dll> `
  --old-common   <旧版 QQMusicCommon.dll> `
  --old-profile  <旧版 profile.json> `
  --new-dll      <新版 QQMusic.dll> `
  --new-common   <新版 QQMusicCommon.dll> `
  [--out <新 profile 候选.json>]
```

输入里「旧版」= 升级前还在用的版本（签名从它提取）；「新版」= 刚升级到、要适配的版本。
`--old-profile` 用仓库里已有的旧版 json（如 `vendor/qqmusic/profiles/qqmusic/22.60.json`）。

## 定位结果含义

| 置信度 | 含义 |
|---|---|
| `auto` | 签名在新版**唯一命中**，RVA 可直接采用（仍需实机验证） |
| `ambiguous` | 多个候选（报告里列出前 4 个），需人工从候选里挑 |
| `manual` | 未定位到 / 不可靠，需人工用 IDA 定位 |

字段定位能力（自回归验证，22.60 旧=新）：

| 字段 | 定位方式 | 结果 |
|---|---|---|
| `getCatManagerRva` / `getQqUinExRva` | 函数签名（在 QQMusicCommon.dll，跨版本基本不变） | auto |
| `songItemConstructorRva` 等 6 个函数入口 | 32 条归一化指令签名 | 7/8 唯一命中 |
| `singleSongPlayDispatchRva` | 所在函数入口签名 + patch 点前后文（12 前 4 后） | auto（校验首字节应为 `E8`） |
| `hiddenCategoryIdRva`（**数据地址**） | `mov r32,[moffs32]` 引用反推 | **manual**（相邻全局变量易混淆） |

## 完整适配流程（QQ 音乐升级后）

1. **备份旧版二进制**：升级前把旧的 `QQMusic.dll` / `QQMusicCommon.dll` 存一份（签名要从它提取）。
   新版通常在 `C:\Program Files (x86)\Tencent\QQMusic\QQMusic<版本>\`。
2. **跑工具**，得到 `fileVersion` + 双 SHA256（必准）+ 各 RVA 定位结果。
3. **人工核对 `ambiguous` / `manual` 字段**：
   - `songItemConstructorRva` 若给出多候选，用 IDA 对比旧版构造器语义挑正确的；
   - `hiddenCategoryIdRva`（数据地址）在 `SingleSongPlayDispatch` 函数内找
     `mov r32,[abs]` 后接 `cmp r,-1` 的那条，读其绝对地址。
4. **校验 `expectedPlayDispatchBytes`**：新位置首字节必须是 `E8`（相对 call），
   且 5 字节机器码要与连接器里 `WriteCodeBytes` 回读一致。
5. **实机端到端验证**（关键，工具不可替代）：确认 SHA256 命中 → 真实点歌插队 → 观察 `Next` 生效。
6. **人工确认后写入发布画像**：
   - 复制候选 json 到 `src/Erbai.Connector/profiles/qqmusic/<version>.json`（csproj 通配自动发布）；
   - 同步一份到 `vendor/qqmusic/profiles/qqmusic/`（本地参考镜像，`vendor/*` 不入 git）；
   - 在 `tools/build-installer.ps1` 的 `$required` 清单补上该 json 行。

## 已知局限

- **数据地址（`hiddenCategoryIdRva`）不可靠自动定位**：绝对地址归一化后，相邻全局变量
  （如 `0x10C5D1C8` vs `0x10C5D1D0`）无法区分，工具只给候选，必须人工核对。
- **同构函数可能多候选**：`songItemConstructorRva` 这类与「拷贝/移动构造器」同构的函数，
  32 条签名仍可能命中 2 个，属正常 `ambiguous`。
- **跨版本实跑未验证**：目前只在 22.60 上做过自回归（旧=新，验证链路正确性）；
  真实「旧版本 → 新版本」的漂移适配需下次实际升级时首次实战，如遇签名漂移过大
  （`manual` 数量异常多），把旧版签名长度/上下文窗口调大重试。

## 为什么不能「运行时签名搜索」代替逐版本画像

QQ 音乐没有 lxmusic 那种官方三通道，注入是唯一路径。若改成运行时签名自动重定位，
等于允许「未验证版本」也注入，直接放弃「哈希/机器码校验不过就安全拒绝」的底线——
22.52 崩溃（`AddSongs` 第4参数签名变化）正是靠这条底线才没有扩大崩溃面。所以：
**逐版本哈希锁定不可省，能省的是「RVA 发现」这步**，本工具就是干这个的。
