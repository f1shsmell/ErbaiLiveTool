<#
.SYNOPSIS
    构建 ErbaiLiveTool 安装包与绿色版 zip（决策 #14 修订：FDD + Inno Setup 安装器；绿色版沿用原 zip 分发形态，方便测试）。
.DESCRIPTION
    产出两种发布形态（同一份 publish 产物）：
      1) dotnet publish 三个发布工程（App 多文件 FDD / Connector 多文件 / Grabber 单文件 FDD）
         —— App 多文件（2026-08-28，#17 悬浮窗的 lib_manual 经典托管 WebView2 与单文件
         bundle 互操作不兼容，见 docs/02 #14 修订），产物统一汇入 publish\（脚本会先清空该目录，
         保证可复现）；
      2) 删除 *.pdb 并断言发布清单后，调 ISCC 编译 installer\ErbaiLiveTool.iss
         → 安装版 Setup.exe（正式分发，≤ 120MB 断言）；
      3) 把 publish\ 打包为绿色版 zip（测试/快速拷贝用，与安装版同源同版本）。
    版本号自动取自 ErbaiLiveTool.exe 的 FileVersion（与主程序/诊断页一致）,归一为 3 段。
.PARAMETER Configuration
    Build/publish 配置，默认 Release。
.PARAMETER PublishDir
    汇入目录，默认仓库根 publish\。
.PARAMETER IsccPath
    ISCC.exe 路径；留空自动探测（INNO_SETUP_HOME / Program Files (x86) / Program Files / C:\Dev\InnoSetup6）。
.PARAMETER SkipPublish
    跳过 dotnet publish，直接使用现有 publish\ 目录编译安装包。
.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\build-installer.ps1
.NOTES
    NuGet restore 遇 NU1301 时需先挂代理 `set HTTPS_PROXY=http://127.0.0.1:7890`（仓库约定，脚本不强设）。
    本机 shell 无管理员权限时安装包的实际安装/卸载为手动验收项（UAC）。
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    # 默认值留空、body 内兜底：Windows PowerShell 5.1 的 advanced script（[CmdletBinding()]）
    # 经 -File 执行时，param 默认值表达式求值发生在脚本上下文建立前，$PSScriptRoot 为空
    #（实测报「Join-Path 参数为空字符串」）；自动变量只能信任 body 内的求值时机。
    [string]$PublishDir = '',
    [string]$IsccPath = '',
    [switch]$SkipPublish
)

$ErrorActionPreference = 'Stop'
if (-not $PublishDir) { $PublishDir = Join-Path $PSScriptRoot '..\publish' }
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path

function Resolve-Iscc {
    param([string]$Hint)
    if ($Hint) {
        if (Test-Path $Hint) { return (Resolve-Path $Hint).Path }
        throw "指定的 ISCC.exe 不存在：$Hint"
    }
    $candidates = @()
    if ($env:INNO_SETUP_HOME) { $candidates += (Join-Path $env:INNO_SETUP_HOME 'ISCC.exe') }
    $candidates += @(
        "$env:ProgramFiles(x86)\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
        'C:\Dev\InnoSetup6\ISCC.exe'
    )
    foreach ($c in $candidates) {
        if ($c -and (Test-Path $c)) { return (Resolve-Path $c).Path }
    }
    throw '未找到 ISCC.exe。请安装 Inno Setup 6 或通过 -IsccPath / INNO_SETUP_HOME 指定。'
}

$iscc = Resolve-Iscc -Hint $IsccPath
Write-Host "[installer] ISCC: $iscc"

$publishDir = [System.IO.Path]::GetFullPath($PublishDir)
if (-not $SkipPublish) {
    # 清空旧产物，保证可复现（publish 目录无用户数据；绿色版 zip 由本脚本末尾统一打包）
    if (Test-Path $publishDir) { Remove-Item -Recurse -Force $publishDir }
    New-Item -ItemType Directory -Force -Path $publishDir | Out-Null

    Write-Host "[installer] publish App（FDD 多文件 + WinAppSDK 自带，#17 悬浮窗兼容）..."
    & dotnet publish (Join-Path $repoRoot 'src\Erbai.App') -c $Configuration -r win-x64 -p:Platform=x64 -o $publishDir
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish App 失败' }

    $tmp = Join-Path $env:TEMP 'erbai-publish-staging'
    if (Test-Path $tmp) { Remove-Item -Recurse -Force $tmp }
    New-Item -ItemType Directory -Force -Path $tmp | Out-Null

    Write-Host "[installer] publish Connector（多文件，仅 lxmusic 原生后端）..."
    & dotnet publish (Join-Path $repoRoot 'src\Erbai.Connector') -c $Configuration -r win-x64 -o (Join-Path $tmp 'connector')
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish Connector 失败' }

    Write-Host "[installer] publish Grabber（单文件 FDD）..."
    & dotnet publish (Join-Path $repoRoot 'src\Erbai.Live.Douyin.Grabber') -c $Configuration -r win-x64 -o (Join-Path $tmp 'grabber')
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish Grabber 失败' }

    Write-Host '[installer] 汇入 Connector / Grabber 产物...'
    Copy-Item (Join-Path $tmp 'connector\*') -Destination $publishDir -Recurse -Force
    New-Item -ItemType Directory -Force -Path (Join-Path $publishDir 'DouyinBarrageGrab') | Out-Null
    Copy-Item (Join-Path $tmp 'grabber\*') -Destination (Join-Path $publishDir 'DouyinBarrageGrab') -Recurse -Force
    Remove-Item -Recurse -Force $tmp
}
else {
    Write-Host '[installer] -SkipPublish：使用现有 publish 目录'
}

# 删除调试符号（不进安装包）
Get-ChildItem -Path $publishDir -Recurse -Filter '*.pdb' | Remove-Item -Force

# 产物断言：发布清单完整（9 应用文件 + 4 内置插件文件（StageA M6 迁移，组合根依赖）
# + 无调试符号），保证可复现与打包一致性。
# 注：bridge\AwooNcmCefBridge.dll 与 profiles\qqmusic\*.json 已随「连接器插件化」移除
#（四平台不再内置、不再分发），此处刻意不再断言它们存在——若哪天又出现，属打包回归。
$required = @(
    'ErbaiLiveTool.exe',
    'Erbai.Connector.exe', 'Erbai.Connector.dll', 'Erbai.Connector.deps.json', 'Erbai.Connector.runtimeconfig.json',
    'Erbai.Contracts.dll',
    'DouyinBarrageGrab\WssBarrageServer.exe',
    'Plugins\queueup\config.ini', 'Plugins\queueup\Erbai.Modules.QueueUp.dll',
    'Plugins\giftfx\config.ini', 'Plugins\giftfx\Erbai.Modules.GiftFx.dll'
)
foreach ($rel in $required) {
    if (-not (Test-Path (Join-Path $publishDir $rel))) { throw "发布清单缺失：$rel" }
}
$strayPdb = Get-ChildItem -Path $publishDir -Recurse -Filter '*.pdb'
if ($strayPdb) { throw "发布产物仍含 $($strayPdb.Count) 个 pdb（应已在删 pdb 步骤移除）" }
Write-Host "[installer] 发布清单断言通过（$($required.Count) 个必选文件，无 pdb）"

# 版本号：取自主程序 FileVersion（4 段归一为 3 段；获取失败回退 0.1.0）
$exePath = Join-Path $publishDir 'ErbaiLiveTool.exe'
if (-not (Test-Path $exePath)) { throw "未找到发布产物 $exePath（请先 publish 或去掉 -SkipPublish）" }
$vi = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($exePath)
$ver4 = if ($vi.FileVersion) { $vi.FileVersion } else { '0.1.0.0' }
$ver = $ver4
while (($ver -match '\.0$') -and (($ver -split '\.').Count -gt 3)) { $ver = $ver.Substring(0, $ver.LastIndexOf('.')) }
Write-Host "[installer] 版本：$ver ($ver4)"

# 编译安装器（ISCC 无 /LOG 参数，编译输出在 PowerShell 层重定向到日志文件）
$iss  = Join-Path $repoRoot 'installer\ErbaiLiveTool.iss'
$outDir = Join-Path $repoRoot 'installer\Output'
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Force -Path $outDir | Out-Null }
$logFile = Join-Path $outDir "iscc-$ver.log"
$isccOut = (& $iscc $iss "/DSrcDir=$publishDir" "/DAppVersion=$ver" "/DAppVersionNum=$ver4" "/DOutputDir=$outDir" 2>&1 | Out-String)
$isccOut | Set-Content -Path $logFile -Encoding UTF8
Write-Host $isccOut.TrimEnd()
if ($LASTEXITCODE -ne 0) { throw "ISCC 编译失败（详见 $logFile）" }

$setup = Join-Path $outDir "ErbaiLiveTool_Setup_v$ver.exe"
if (-not (Test-Path $setup)) { throw "未生成安装包 $setup" }
$sizeMB = [Math]::Round((Get-Item $setup).Length / 1MB, 1)
Write-Host "[installer] 安装包: $setup ($sizeMB MB)"
$limit = 120
if ($sizeMB -gt $limit) { throw "安装包 $sizeMB MB 超出硬指标 $limit MB" }
Write-Host "[installer] 体积达标（≤ $limit MB）。"

# 绿色版（zip，仅应用文件，方便测试/快速拷贝，与安装版同源同版本；不再单独同步桌面分发目录）
$zip = Join-Path $outDir "ErbaiLiveTool-$ver.zip"
if (Test-Path $zip) { Remove-Item -Force $zip }
Compress-Archive -Path (Get-ChildItem -Path $publishDir -Force | Select-Object -ExpandProperty FullName) -DestinationPath $zip -CompressionLevel Optimal
if (-not (Test-Path $zip)) { throw "绿色版 zip 生成失败 $zip" }
$zipMB = [Math]::Round((Get-Item $zip).Length / 1MB, 1)
Write-Host "[installer] 绿色版: $zip ($zipMB MB，测试用)"

Write-Host "[installer] 发布产物就绪：安装版 Setup.exe（正式分发）+ 绿色版 zip（方便测试）。"
Write-Host "[installer] 手动验收（需管理员终端）：运行安装包 → 组件部署 → 启动 → Plugins 目录就绪可装插件。"