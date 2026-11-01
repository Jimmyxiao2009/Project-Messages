# tools/Capture-AppWindow.ps1
#
# 抓 UWP 应用窗口的截图。
#
# 为什么不能按进程名找窗口：桌面上的 UWP 应用，可见窗口属于 ApplicationFrameHost.exe，
# 我们的进程自己的 MainWindowHandle 通常是 0。按进程找只会抓到别的窗口。
# 所以这里按**窗口标题**找，并用 PrintWindow(PW_RENDERFULLCONTENT) 直接渲染窗口内容 ——
# 这样即使窗口被别的窗口盖住也能抓到，也不会把用户其他窗口拍进来。
#
# 用法：
#   .\Capture-AppWindow.ps1 -TitleLike '言枢' -Out shot.png
#   .\Capture-AppWindow.ps1 -TitleLike '言枢' -Out shot.png -List   # 只列出候选窗口

[CmdletBinding()]
param(
    [string]$TitleLike = '言枢',
    [string]$Out,
    [switch]$List
)

$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing

Add-Type -Namespace Native -Name Win -MemberDefinition @'
public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

[DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
[DllImport("user32.dll")] public static extern int GetWindowTextLength(IntPtr hWnd);
[DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);
[DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
[DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
[DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
[DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
[DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);
[DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
[StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
'@

[Native.Win]::SetProcessDPIAware() | Out-Null

function Get-VisibleWindows {
    $found = New-Object System.Collections.ArrayList
    $callback = [Native.Win+EnumWindowsProc] {
        param($hWnd, $lParam)
        if ([Native.Win]::IsWindowVisible($hWnd)) {
            $len = [Native.Win]::GetWindowTextLength($hWnd)
            if ($len -gt 0) {
                $sb = New-Object System.Text.StringBuilder ($len + 2)
                [Native.Win]::GetWindowText($hWnd, $sb, $sb.Capacity) | Out-Null
                $rect = New-Object Native.Win+RECT
                [Native.Win]::GetWindowRect($hWnd, [ref]$rect) | Out-Null
                $null = $found.Add([pscustomobject]@{
                    Handle = $hWnd
                    Title  = $sb.ToString()
                    Left   = $rect.Left
                    Top    = $rect.Top
                    Width  = $rect.Right - $rect.Left
                    Height = $rect.Bottom - $rect.Top
                })
            }
        }
        return $true
    }
    [Native.Win]::EnumWindows($callback, [IntPtr]::Zero) | Out-Null
    return $found
}

$candidates = Get-VisibleWindows | Where-Object { $_.Title -like "*$TitleLike*" }

if ($List) {
    Write-Host "标题包含「$TitleLike」的可见窗口："
    if (-not $candidates) { Write-Host '  （没有）' }
    foreach ($c in $candidates) {
        Write-Host ("  [{0}] {1}  {2}x{3} @({4},{5})" -f $c.Handle, $c.Title, $c.Width, $c.Height, $c.Left, $c.Top)
    }
    return
}

if (-not $Out) { throw '需要 -Out 指定输出路径' }

$target = $candidates | Sort-Object { $_.Width * $_.Height } -Descending | Select-Object -First 1
if (-not $target) { throw "找不到标题包含「$TitleLike」的可见窗口" }

Write-Host ("目标窗口：{0}  ({1}x{2})" -f $target.Title, $target.Width, $target.Height)
if ($target.Width -le 0 -or $target.Height -le 0) { throw '窗口尺寸异常' }

# SW_RESTORE
[Native.Win]::ShowWindow($target.Handle, 9) | Out-Null
Start-Sleep -Milliseconds 400

$bitmap = New-Object System.Drawing.Bitmap($target.Width, $target.Height)
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$hdc = $graphics.GetHdc()

# PW_RENDERFULLCONTENT = 2：让 DWM 合成的窗口也能被渲染出来
$ok = [Native.Win]::PrintWindow($target.Handle, $hdc, 2)
$graphics.ReleaseHdc($hdc)

if (-not $ok) {
    # 退路：把它提到前台再抓屏幕区域（可能被别的窗口盖住）
    Write-Host 'PrintWindow 失败，退回屏幕区域抓取'
    [Native.Win]::SetForegroundWindow($target.Handle) | Out-Null
    Start-Sleep -Milliseconds 800
    $graphics.CopyFromScreen($target.Left, $target.Top, 0, 0, (New-Object System.Drawing.Size($target.Width, $target.Height)))
}

$graphics.Dispose()
$bitmap.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$bitmap.Dispose()

Write-Host ("已截图：$Out")
