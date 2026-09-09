# 生成应用图标：AppLogo.png（原始素材或处理版）→ AppLogo.ico（多尺寸，嵌入 exe / 窗口 / 安装器）。
# 用法：powershell -ExecutionPolicy Bypass -File tools\make-app-icon.ps1 [-Style square|rounded] [-Source <png>] [-OutDir <dir>]
#   -Style square  原图直出（默认保留原样）
#   -Style rounded 圆角版（logo 2026-09-08）：原图本身即为透明背景（无实心黑底），
#                  仅叠加 18% 圆角蒙版，让内容贴边处的直角变圆润。
# 依赖：无（System.Drawing，Windows 自带）。
param(
    [ValidateSet("square", "rounded")]
    [string]$Style = "rounded",
    [string]$Source = "",
    [string]$OutDir = ""
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

if (-not $OutDir) { $OutDir = Join-Path $PSScriptRoot "..\src\Erbai.App\Assets" }
$OutDir = [System.IO.Path]::GetFullPath($OutDir)
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

if (-not $Source) {
    $candidates = Get-ChildItem (Join-Path $PSScriptRoot "..\.reasonix\attachments") -Filter "*.png" |
        Sort-Object LastWriteTime -Descending
    $Source = $candidates[0].FullName
}
$src = [System.Drawing.Image]::FromFile((Resolve-Path $Source))

# 多尺寸：覆盖 16x16（标题栏/任务栏小图标）到 256x256（资源管理器大图）。
$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)

# ---------- 圆角蒙版（radiusRatio 相对边长比例），叠乘到 alpha ----------
function Apply-RoundedCorners([System.Drawing.Bitmap]$bmp, [double]$radiusRatio) {
    $w = $bmp.Width; $h = $bmp.Height
    $r = [int][Math]::Round([Math]::Min($w, $h) * $radiusRatio)
    $rect = New-Object System.Drawing.Rectangle(0, 0, $w, $h)
    $data = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadWrite,
        [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try {
        $stride = $data.Stride
        $raw = New-Object byte[] ($stride * $h)
        [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $raw, 0, $raw.Length)
        $r2 = $r * $r
        for ($y = 0; $y -lt $h; $y++) {
            for ($x = 0; $x -lt $w; $x++) {
                # 找最近角距离：仅当在四角 r×r 方块内才计算
                $cx = if ($x -lt $r) { $r - $x - 1 } elseif ($x -ge $w - $r) { $x - ($w - $r) } else { -1 }
                $cy = if ($y -lt $r) { $r - $y - 1 } elseif ($y -ge $h - $r) { $y - ($h - $r) } else { -1 }
                if ($cx -ge 0 -and $cy -ge 0) {
                    $d2 = $cx * $cx + $cy * $cy
                    if ($d2 -gt $r2) {
                        $i = $y * $stride + $x * 4
                        $raw[$i + 3] = 0                 # 圆外：全透明
                    } elseif ($d2 -gt ($r - 2) * ($r - 2)) {
                        $i = $y * $stride + $x * 4        # 圆边缘 2px：半透明过渡（抗锯齿）
                        $raw[$i + 3] = [byte][Math]::Min($raw[$i + 3], 40)
                    }
                }
            }
        }
        [System.Runtime.InteropServices.Marshal]::Copy($raw, 0, $data.Scan0, $raw.Length)
    } finally {
        $bmp.UnlockBits($data)
    }
    return $bmp
}

$canvas = New-Object System.Drawing.Bitmap($src.Width, $src.Height, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$g = [System.Drawing.Graphics]::FromImage($canvas)
$g.Clear([System.Drawing.Color]::Transparent)
$g.DrawImage($src, 0, 0, $src.Width, $src.Height)
$g.Dispose()

if ($Style -eq "rounded") {
    Write-Host "[rounded] 原图（透明背景）叠加圆角 18%..."
    $null = Apply-RoundedCorners $canvas 0.18
} else {
    Write-Host "[square] 原图直出"
}

# ---------- 多尺寸 DIB 收集 ----------
function Get-DibBytes([System.Drawing.Bitmap]$bmp) {
    $w = $bmp.Width; $h = $bmp.Height
    $rect = New-Object System.Drawing.Rectangle(0, 0, $w, $h)
    $data = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly,
        [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try {
        $stride = $data.Stride
        $raw = New-Object byte[] ($stride * $h)
        [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $raw, 0, $raw.Length)
    } finally {
        $bmp.UnlockBits($data)
    }
    # BITMAPINFOHEADER(40) + 自下而上 BGRA 像素（32bpp 图标无需 AND 掩码，alpha 生效）
    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($ms)
    $bw.Write([int32]40); $bw.Write([int32]$w); $bw.Write([int32]($h * 2))
    $bw.Write([int16]1); $bw.Write([int16]32); $bw.Write([int32]0)
    $bw.Write([int32]0); $bw.Write([int32]0); $bw.Write([int32]0)
    $bw.Write([int32]0); $bw.Write([int32]0)
    for ($y = $h - 1; $y -ge 0; $y--) {
        for ($x = 0; $x -lt $w; $x++) {
            $i = $y * $stride + $x * 4
            $bw.Write([byte]$raw[$i]); $bw.Write([byte]$raw[$i + 1])
            $bw.Write([byte]$raw[$i + 2]); $bw.Write([byte]$raw[$i + 3])
        }
    }
    $bw.Flush()
    return , $ms.ToArray()   # 逗号包裹：阻止 PowerShell 枚举 byte[]（否则退化为 Object[]）
}

$images = @()
foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap($s, $s, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g2 = [System.Drawing.Graphics]::FromImage($bmp)
    $g2.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g2.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g2.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g2.Clear([System.Drawing.Color]::Transparent)
    $g2.DrawImage($canvas, 0, 0, $s, $s)
    $g2.Dispose()
    $images += , @{ Size = $s; Data = Get-DibBytes $bmp }
    $bmp.Dispose()
}

# ---------- 组装 ICO ----------
$count = $images.Count
$headerLen = 6
$entryLen = 16
$offset = $headerLen + $entryLen * $count
$ms = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter($ms)
$bw.Write([int16]0); $bw.Write([int16]1); $bw.Write([int16]$count)
foreach ($im in $images) {
    $s = $im.Size
    $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
    $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
    $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([int16]1); $bw.Write([int16]32)
    $bw.Write([int32]$im.Data.Length)
    $bw.Write([int32]$offset)
    $offset += $im.Data.Length
}
foreach ($im in $images) { $bw.Write($im.Data) }
$bw.Flush()
$icoPath = Join-Path $OutDir "AppLogo.ico"
[System.IO.File]::WriteAllBytes($icoPath, $ms.ToArray())

# 处理后的 PNG 同时输出（AppLogo.png = 当前采用的图标图）
$pngPath = Join-Path $OutDir "AppLogo.png"
$canvas.Save($pngPath, [System.Drawing.Imaging.ImageFormat]::Png)

$src.Dispose(); $canvas.Dispose()
Write-Host "已生成: $icoPath ($([math]::Round((Get-Item $icoPath).Length / 1KB, 1)) KB, $count 尺寸)"
Write-Host "已生成: $pngPath (Style=$Style)"