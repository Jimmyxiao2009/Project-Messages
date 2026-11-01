# tools/Measure-Shot.ps1
#
# 把截图当成数据来读：行墨迹剖面、颜色直方图、指定矩形取色。
# 用途：Computer Use 验收时量化布局度量（行高 / 边距 / 色值 / 强调色焦点数量），
# 而不是靠肉眼。当前会话模型不声明图像输入，这个脚本就是替代的眼睛。
#
# 用法：
#   .\tools\Measure-Shot.ps1 -Path docs\a.png -Info
#   .\tools\Measure-Shot.ps1 -Path docs\a.png -Bands          # 内容行带（y 范围 + 高度）
#   .\tools\Measure-Shot.ps1 -Path docs\a.png -Colors -Top 20 # 颜色直方图
#   .\tools\Measure-Shot.ps1 -Path docs\a.png -At 100,300     # 取某个像素

[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$Path,
    [switch]$Info,
    [switch]$Bands,
    [switch]$Colors,
    [int]$Top = 16,
    [int[]]$At,
    [int]$MinInk = 3
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

if (-not (Test-Path -LiteralPath $Path)) { throw ('找不到截图：' + $Path) }
$bmp = New-Object System.Drawing.Bitmap($Path)
$w = $bmp.Width; $h = $bmp.Height

if ($Info) {
    Write-Host ('尺寸：{0} x {1}' -f $w, $h)
}

# 背景色 = 四角中位数，避免把窗口阴影算进来
function Get-BgPixel([int]$x, [int]$y) { return $bmp.GetPixel($x, $y) }
$corners = @( (Get-BgPixel 2 2), (Get-BgPixel ($w-3) 2), (Get-BgPixel 2 ($h-3)), (Get-BgPixel ($w-3) ($h-3)) )
$bg = $corners[0]

function SameColor([System.Drawing.Color]$a, [System.Drawing.Color]$b, [int]$tol) {
    return ([Math]::Abs($a.R - $b.R) -le $tol -and [Math]::Abs($a.G - $b.G) -le $tol -and [Math]::Abs($a.B - $b.B) -le $tol)
}

# 逐行墨迹：与背景不同的像素数
$ink = New-Object int[] $h
for ($y = 0; $y -lt $h; $y++) {
    $n = 0
    for ($x = 0; $x -lt $w; $x++) {
        if (-not (SameColor $bmp.GetPixel($x, $y) $bg 10)) { $n++ }
    }
    $ink[$y] = $n
}

if ($Bands) {
    Write-Host ('背景色基准：RGB({0},{1},{2})' -f $bg.R, $bg.G, $bg.B)
    Write-Host '内容行带（y起..y止  高度  峰值墨迹）：'
    $y = 0
    while ($y -lt $h) {
        if ($ink[$y] -lt $MinInk) { $y++; continue }
        $start = $y; $peak = 0
        while ($y -lt $h -and $ink[$y] -ge $MinInk) { if ($ink[$y] -gt $peak) { $peak = $ink[$y] }; $y++ }
        $end = $y - 1
        Write-Host ('  {0,5}..{1,-5}  h={2,-5} peak={3}' -f $start, $end, ($end - $start + 1), $peak)
    }
}

if ($Colors) {
    $map = @{}
    for ($y = 0; $y -lt $h; $y++) {
        for ($x = 0; $x -lt $w; $x++) {
            $c = $bmp.GetPixel($x, $y)
            $k = ('{0:X2}{1:X2}{2:X2}' -f $c.R, $c.G, $c.B)
            if ($map.ContainsKey($k)) { $map[$k] = $map[$k] + 1 } else { $map[$k] = 1 }
        }
    }
    Write-Host '颜色直方图（前 ' + $Top + '）：'
    $map.GetEnumerator() | Sort-Object Value -Descending | Select-Object -First $Top | ForEach-Object {
        $r = [Convert]::ToInt32($_.Key.Substring(0,2), 16)
        $g = [Convert]::ToInt32($_.Key.Substring(2,2), 16)
        $b = [Convert]::ToInt32($_.Key.Substring(4,2), 16)
        $lum = [Math]::Round(0.299*$r + 0.587*$g + 0.114*$b, 0)
        Write-Host ('  #{0}  {1,9} px  {2,6}%  亮度={3}' -f $_.Key, $_.Value, [Math]::Round(100.0*$_.Value/($w*$h),2), $lum)
    }
}

if ($At -and $At.Count -ge 2) {
    for ($i = 0; $i + 1 -lt $At.Count; $i += 2) {
        $x = $At[$i]; $y = $At[$i+1]
        if ($x -lt 0 -or $y -lt 0 -or $x -ge $w -or $y -ge $h) { Write-Host ('  ({0},{1}) 越界' -f $x, $y); continue }
        $c = $bmp.GetPixel($x, $y)
        Write-Host ('  ({0},{1}) = #{2:X2}{3:X2}{4:X2}' -f $x, $y, $c.R, $c.G, $c.B)
    }
}

$bmp.Dispose()
