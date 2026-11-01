# tools/Read-PhoneDiagnostics.ps1
#
# 读真机上的排障文件（crash.log / diagnostics.log / workspace 下的 JSON）。
# 走设备门户的文件 API，不需要 adb，也不需要把手机挂成 U 盘。
#
# 用法：
#   .\Read-PhoneDiagnostics.ps1                    # 读 crash.log 与 diagnostics.log
#   .\Read-PhoneDiagnostics.ps1 -File crash.log
#   .\Read-PhoneDiagnostics.ps1 -Path '\LocalState\workspace\_shared' -File api-profiles.json
#   .\Read-PhoneDiagnostics.ps1 -List              # 列出 LocalState

[CmdletBinding()]
param(
    [string]$DeviceOrigin = 'http://127.0.0.1:10080',
    [string]$Path = '\LocalState',
    [string]$File = '',
    [switch]$List
)

$ErrorActionPreference = 'Stop'
$base = $DeviceOrigin.TrimEnd('/')

$json = & curl.exe --noproxy '*' --silent --show-error --fail --max-time 20 "$base/api/app/packagemanager/packages"
if ($LASTEXITCODE -ne 0) { throw '连不上设备门户' }

# 与 Deploy-Phone.ps1 同样的坑：门户的包列表 JSON 可能含非 UTF-8 字节（中文包名），
# ConvertFrom-Json 会整份失败。用正则抠包全名，不依赖整份 JSON 合法。
$names = [regex]::Matches($json, '"PackageFullName"\s*:\s*"([^"]+)"') | ForEach-Object { $_.Groups[1].Value }
$pkg = [pscustomobject]@{ PackageFullName = @($names | Where-Object { $_ -like 'com.yanshuai.messages_*' } | Select-Object -First 1) }
if (-not $pkg) { throw '真机上还没装这个应用' }

function Get-DeviceFile([string]$relativeDir, [string]$name) {
    $url = "$base/api/filesystem/apps/file?knownfolderid=LocalAppData" +
           "&packagefullname=$([Uri]::EscapeDataString($pkg.PackageFullName))" +
           "&path=$([Uri]::EscapeDataString($relativeDir))" +
           "&filename=$([Uri]::EscapeDataString($name))"
    return & curl.exe --noproxy '*' --silent --show-error --max-time 30 $url
}

if ($List) {
    $url = "$base/api/filesystem/apps/files?knownfolderid=LocalAppData" +
           "&packagefullname=$([Uri]::EscapeDataString($pkg.PackageFullName))" +
           "&path=$([Uri]::EscapeDataString($Path))"
    & curl.exe --noproxy '*' --silent --show-error --max-time 30 $url
    return
}

$targets = if ($File) { @($File) } else { @('crash.log', 'diagnostics.log') }

foreach ($name in $targets) {
    Write-Host ("──── " + $name + " ────")
    try {
        $content = Get-DeviceFile $Path $name
        if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($content)) {
            Write-Host '(空或不存在)'
        } else {
            Write-Host $content
        }
    } catch {
        Write-Host ('读取失败：' + $_.Exception.Message)
    }
    Write-Host ''
}
