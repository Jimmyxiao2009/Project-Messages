# tools/Launch-Phone.ps1
#
# 启动真机上已安装的应用。默认走 USB 上的设备门户。
#
# 门户的启动端点要的是 **base64 编码后**的 appid / package 参数（再 URL 编码一层），
# 不是明文 —— 传明文会 400。

[CmdletBinding()]
param(
    [string]$DeviceOrigin = 'http://127.0.0.1:10080'
)

$ErrorActionPreference = 'Stop'
$base = $DeviceOrigin.TrimEnd('/')

function Encode-Base64Url([string]$text) {
    return [Uri]::EscapeDataString([Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($text)))
}

$json = & curl.exe --noproxy '*' --silent --show-error --fail --max-time 20 "$base/api/app/packagemanager/packages"
if ($LASTEXITCODE -ne 0) { throw '连不上设备门户（检查 USB 连接与设备门户是否开启）' }

$app = (($json | ConvertFrom-Json).InstalledPackages |
        Where-Object { $_.PackageFullName -like 'com.yanshuai.messages_*' } | Select-Object -First 1)
if (-not $app) { throw '真机上还没装。先跑 tools\Deploy-Phone.ps1' }

$appId = Encode-Base64Url $app.PackageRelativeId
$packageId = Encode-Base64Url $app.PackageFullName

& curl.exe --noproxy '*' --silent --show-error --fail --max-time 30 `
    -X POST -H 'Content-Length: 0' `
    "$base/api/taskmanager/app?appid=$appId&package=$packageId"

if ($LASTEXITCODE -ne 0) { throw '启动失败' }
Write-Host ("已启动 " + $app.PackageFullName)
