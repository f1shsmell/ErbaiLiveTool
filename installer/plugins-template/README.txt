【插件目录】Plugins/

本目录由安装器创建，用于放第三方插件（目录式 DLL 插件，决策 #4/#16）。
卸载本程序会保留本目录（不删你装的插件），仅删除安装器自带的本文档。

用法：一个插件 = 本目录下一个子目录，内含 config.ini 元数据 + 插件 DLL：
  1. 每插件一子目录（目录名任意，建议英文），目录内必须有 config.ini；
  2. config.ini 的 [General] 段必需键 Name / File / Version，可选
     Description / Developer / ContractVersion / Enabled；
  3. [General] File 指向目录内的主程序集 DLL（纯文件名）；
  4. 插件 DLL 请引用随宿主发布的 Erbai.Contracts.dll（契约层），不要自带副本；
  5. 禁用两种形态：[General] Enabled=false，或把 DLL 改名为 xxx.dll.disabled；
  6. 应用启动时自动加载本目录插件；加载失败不影响其他插件与主程序，
     原因见 logs\erbai-*.log 的「插件加载失败 [目录]」行。
  7. 插件页（设置 → 插件）可启停插件；「配置项编辑 / 热启用」后续版本支持。

config.ini 样例（字段参考，不要把它作为一个插件目录放进本目录）：
--------------------------------------------------------------------------------
[General]
Name = 示例插件
Description = 插件描述
Developer = 开发者名
File = MyPlugin.dll
Version = 1.0.0
ContractVersion = 1.0.0
Enabled = true

; 自定义配置项（任意数量）：每个 section = 一个配置项，含 Name / Type / Value 三元组
[ToggleKey]
Name = 重载快捷键
Type = key
Value = 118
--------------------------------------------------------------------------------

常见故障（完整见仓库文档 docs/07-插件开发指南.md）：
  File 与实际 DLL 名不一致 → 「未找到插件 DLL xxx.dll」
  ContractVersion 主版本与宿主不一致或更高 → 契约版本拒绝
  插件类没有 public 无参构造函数 → 实例化失败