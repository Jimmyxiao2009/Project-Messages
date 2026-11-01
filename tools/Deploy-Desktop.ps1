# tools/Deploy-Desktop.ps1
#
# 把桌面 x86 调试包注册到本机（松散文件注册，比装 .appx 快，改完重新编译再跑一次即可）。
#
# 两个注意点：
#   1) 注册前先关掉正在运行的实例，否则文件被占用会报 0x80070005；
#   2) 用 -Register 走宽松文件注册，依赖开发者模式（本机已开启）。

[CmdletBinding()]
param(
    [string]$Configuration = 'Debug',
    [string]$Platform = 'x86'
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$bin = Join-Path $repoRoot "src\Yanshuai.Messages\bin\$Platform\$Configuration"
$manifest = Join-Path $bin 'AppxManifest.xml'

if (-not (Test-Path -LiteralPath $manifest)) {
    throw "找不到清单：$manifest`n先编译一次：`n  & `$msbuild src\Yanshuai.Messages\Yanshuai.Messages.csproj /t:Build /p:Configuration=$Configuration /p:Platform=$Platform"
}

Write-Host "注册：$manifest"

# 关掉正在运行的实例
$running = Get-Process -Name 'Yanshuai.Messages' -ErrorAction SilentlyContinue
if ($running) {
    Write-Host ("关闭已运行实例：PID " + ($running.Id -join ', '))
    $running | Stop-Process -Force
    Start-Sleep -Seconds 1
}

Add-AppxPackage -Register $manifest -ForceUpdateFromAnyVersion

$pkg = Get-AppxPackage -Name 'com.yanshuai.messages'
if (-not $pkg) { throw '注册后仍然查不到包，注册可能失败了' }

Write-Host ''
Write-Host '注册完成'
Write-Host ("  包全名    : " + $pkg.PackageFullName)
Write-Host ("  包族名    : " + $pkg.PackageFamilyName)
Write-Host ("  安装位置  : " + $pkg.InstallLocation)
Write-Host ("  本地数据  : " + (Join-Path $env:LOCALAPPDATA ("Packages\" + $pkg.PackageFamilyName + "\LocalState")))
