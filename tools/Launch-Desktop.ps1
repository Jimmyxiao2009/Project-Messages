# tools/Launch-Desktop.ps1
#
# 启动已注册的桌面实例。加 -Wait 会等进程出现再返回，方便后面截图。

[CmdletBinding()]
param(
    [switch]$Wait,
    [int]$TimeoutSeconds = 30
)

$ErrorActionPreference = 'Stop'

$pkg = Get-AppxPackage -Name 'com.yanshuai.messages'
if (-not $pkg) { throw '还没注册。先跑 tools\Deploy-Desktop.ps1' }

$appUserModelId = $pkg.PackageFamilyName + '!App'
Write-Host "启动：$appUserModelId"

Start-Process ('shell:appsFolder\' + $appUserModelId)

if ($Wait) {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $proc = Get-Process -Name 'Yanshuai.Messages' -ErrorAction SilentlyContinue |
                Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
        if ($proc) {
            Write-Host ("进程已起：PID " + $proc.Id)
            return
        }
        Start-Sleep -Milliseconds 400
    }
    throw "等了 $TimeoutSeconds 秒没等到主窗口。看看是不是启动就崩了（见下方 LocalState\crash.log）"
}
