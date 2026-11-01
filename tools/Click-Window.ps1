# tools/Click-Window.ps1
#
# 往一个窗口的指定位置发一次点击。
#
# 为什么需要它：桌面上的 UWP 窗口属于 ApplicationFrameHost，我们没法用 UI Automation
# 稳定地找到 XAML 里的按钮；而这个窗口本身是我们自己的应用、位置固定，
# 按窗口内坐标点击最直接。坐标用窗口内的相对位置（0–1 比例），
# 这样换分辨率或窗口尺寸都不用改脚本。

[CmdletBinding()]
param(
    [string]$TitleLike = '言枢',
    [double]$X = 0.5,
    [double]$Y = 0.5,
    [int]$DelayMs = 600
)

$ErrorActionPreference = 'Stop'

Add-Type -Namespace Native -Name Click -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool SetCursorPos(int X, int Y);
[DllImport("user32.dll")] public static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, System.UIntPtr dwExtraInfo);
[DllImport("user32.dll")] public static extern bool SetForegroundWindow(System.IntPtr hWnd);
[DllImport("user32.dll")] public static extern bool ShowWindow(System.IntPtr hWnd, int nCmdShow);
[DllImport("user32.dll")] public static extern bool GetWindowRect(System.IntPtr hWnd, out RECT lpRect);
[StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
'@

$MOUSEEVENTF_LEFTDOWN = 0x0002
$MOUSEEVENTF_LEFTUP = 0x0004

# 用 Get-Process 找窗口：UWP 的可见窗口挂在 ApplicationFrameHost 上，但
# MainWindowTitle 会跟随实际内容，所以按标题找进程再取 MainWindowHandle 不可靠。
# 这里直接枚举进程里标题匹配的那个。
$target = Get-Process | Where-Object { $_.MainWindowTitle -like "*$TitleLike*" } | Select-Object -First 1
if (-not $target) { throw "找不到标题包含「$TitleLike」的窗口" }

$handle = $target.MainWindowHandle
[Native.Click]::ShowWindow($handle, 9) | Out-Null       # SW_RESTORE
[Native.Click]::SetForegroundWindow($handle) | Out-Null
Start-Sleep -Milliseconds 400

$rect = New-Object Native.Click+RECT
[Native.Click]::GetWindowRect($handle, [ref]$rect) | Out-Null

$width = $rect.Right - $rect.Left
$height = $rect.Bottom - $rect.Top
$px = [int]($rect.Left + $width * $X)
$py = [int]($rect.Top + $height * $Y)

Write-Host ("窗口 {0}x{1} @({2},{3}) → 点击 ({4},{5})" -f $width, $height, $rect.Left, $rect.Top, $px, $py)

[Native.Click]::SetCursorPos($px, $py) | Out-Null
Start-Sleep -Milliseconds 150
[Native.Click]::mouse_event($MOUSEEVENTF_LEFTDOWN, 0, 0, 0, [System.UIntPtr]::Zero)
Start-Sleep -Milliseconds 60
[Native.Click]::mouse_event($MOUSEEVENTF_LEFTUP, 0, 0, 0, [System.UIntPtr]::Zero)

Start-Sleep -Milliseconds $DelayMs
Write-Host '点击完成'
