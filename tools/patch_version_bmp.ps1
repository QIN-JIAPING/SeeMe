param(
    [string]$Version = '1.0.1',
    [string]$BmpPath = 'D:\SeeMe\tools\installer-assets\welcome.bmp'
)
# 在安装包欢迎位图上原位替换版本号文字（无 Python/Pillow 环境的兜底方案）。
# 版本号字符串长度可变：按新文字实际尺寸清底重绘，位置固定为横幅底部中央 (82, 292)。
# 用法: powershell -NoProfile -ExecutionPolicy Bypass -File patch_version_bmp.ps1 -Version 1.0.1
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

if (-not (Test-Path $BmpPath)) { Write-Error "welcome.bmp not found: $BmpPath"; exit 1 }

$bmp = New-Object System.Drawing.Bitmap($BmpPath)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit

$font = New-Object System.Drawing.Font('Microsoft YaHei', 11, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
$text = 'v' + $Version
$size = $g.MeasureString($text, $font)

$cx = [int]($bmp.Width / 2)   # 164 宽横幅 -> 82
$cy = 292
$boxX = $cx - [int][Math]::Ceiling($size.Width) / 2 - 8
$boxY = $cy - [int][Math]::Ceiling($size.Height) / 2 - 3
$boxW = [int][Math]::Ceiling($size.Width) + 16
$boxH = [int][Math]::Ceiling($size.Height) + 6

# 采样文字左侧纯渐变区背景色（避开右下角光晕），清底后重绘
$sx = [Math]::Max(4, [int]($boxX - 6))
$sy = [Math]::Min($bmp.Height - 1, $cy)
$bg = $bmp.GetPixel($sx, $sy)
$brushBg = New-Object System.Drawing.SolidBrush($bg)
$g.FillRectangle($brushBg, [float]$boxX, [float]$boxY, [float]$boxW, [float]$boxH)

$white = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)
$sf = New-Object System.Drawing.StringFormat
$sf.Alignment = [System.Drawing.StringAlignment]::Center
$sf.LineAlignment = [System.Drawing.StringAlignment]::Center
$rect = New-Object System.Drawing.RectangleF([float]$boxX, [float]$boxY, [float]$boxW, [float]$boxH)
$g.DrawString($text, $font, $white, $rect, $sf)

# 原路径可能仍被 Bitmap 句柄占用，先存临时文件再覆盖
$tmp = [System.IO.Path]::Combine([System.IO.Path]::GetDirectoryName($BmpPath), 'welcome.new.bmp')
$bmp.Save($tmp, [System.Drawing.Imaging.ImageFormat]::Bmp)
$bmp.Dispose()
$g.Dispose()
Move-Item -Force $tmp $BmpPath
Write-Host "welcome.bmp version -> $text"
