# tools/Deploy-Phone.ps1
#
# 把 ARM 包部署到真机（Windows 10 Mobile）。默认走 USB 上的设备门户
# （127.0.0.1:10080 由 WDP 转发），也可以 -DeviceOrigin 指向 WiFi 地址。
#
# 这里踩过、必须绕开的四个坑（每一条都是实测撞出来的）：
#
#   1) **系统代理**。本机跑着 Clash，它会把 127.0.0.1 的请求也劫走，导致
#      "连不上设备门户"。所以 curl 一律带 --noproxy '*'，
#      Invoke-WebRequest 动态探测有没有 -NoProxy 参数（PS 7 有，5.1 没有）。
#
#   2) **multipart 的引号 boundary**。W10M 上那个老旧的 multipart 解析器
#      不接受 PowerShell -Form 生成的带引号 boundary，会直接 400。
#      必须用 curl.exe 的 -F。
#
#   3) **CSRF**。门户要求先 GET 一次拿到 CSRF-Token cookie，
#      之后所有写操作都要同时带 cookie 与 X-CSRF-Token 头。
#
#   4) **先强停**。应用正在跑时更新会失败（文件占用），所以先 forcestop。
#
# 另外：ARM 包是 .NET Native 编译的，需要 Microsoft.NET.Native.Framework/Runtime，
# 设备上不一定有，所以把 AppPackages 里的 Dependencies\ARM 一起上传。

[CmdletBinding()]
param(
    [string]$DeviceOrigin = 'http://127.0.0.1:10080',
    [string]$Configuration = 'Release',
    [string]$PackagePath = '',
    [string]$CertSubject = 'CN=LockWizard',
    [switch]$SkipSign
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$base = $DeviceOrigin.TrimEnd('/')

if (-not $PackagePath) {
    $manifestDoc = [xml](Get-Content -LiteralPath (Join-Path $repoRoot 'src\Yanshuai.Messages\Package.appxmanifest') -Raw)
    $version = $manifestDoc.Package.Identity.Version
    $suffix = if ($Configuration -eq 'Debug') { '_ARM_Debug' } else { '_ARM' }
    $PackagePath = Join-Path $repoRoot ("src\Yanshuai.Messages\AppPackages\Yanshuai.Messages_" + $version + $suffix + "_Test\Yanshuai.Messages_" + $version + $suffix + ".appx")
}
if (-not (Test-Path -LiteralPath $PackagePath)) {
    throw "找不到包：$PackagePath`n先构建：`n  & `$msbuild src\Yanshuai.Messages\Yanshuai.Messages.csproj /p:Configuration=$Configuration /p:Platform=ARM"
}

$package = Get-Item -LiteralPath $PackagePath
Write-Host ("包：" + $package.Name + "  " + [math]::Round($package.Length / 1MB, 2) + " MB")

# ── 1) 签名（设备侧载要求包已签名）──────────────────────────────────
function Test-Signed([string]$path) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem -ErrorAction SilentlyContinue
    $zip = [System.IO.Compression.ZipFile]::OpenRead($path)
    try {
        return ($null -ne ($zip.Entries | Where-Object { $_.FullName -eq 'AppxSignature.p7x' }))
    } finally { $zip.Dispose() }
}

if (-not $SkipSign) {
    if (Test-Signed $package.FullName) {
        Write-Host '已签名，跳过'
    } else {
        $cert = Get-ChildItem Cert:\CurrentUser\My |
                Where-Object { $_.Subject -eq $CertSubject -and $_.HasPrivateKey } |
                Sort-Object NotAfter -Descending | Select-Object -First 1
        if (-not $cert) { throw "找不到证书 $CertSubject（需要带私钥）" }

        $signtool = Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin' -Recurse -Filter 'signtool.exe' -ErrorAction SilentlyContinue |
                    Where-Object { $_.FullName -match '\\x64\\' } | Sort-Object FullName -Descending | Select-Object -First 1
        if (-not $signtool) { throw '找不到 signtool.exe' }

        Write-Host ("签名：" + $cert.Thumbprint + "（到期 " + $cert.NotAfter.ToString('yyyy-MM-dd') + "）")
        & $signtool.FullName sign /fd SHA256 /sha1 $cert.Thumbprint $package.FullName
        if ($LASTEXITCODE -ne 0) { throw '签名失败' }
    }
}

# ── 2) 探测门户 + 拿 CSRF ────────────────────────────────────────────
$webArgs = @{ Uri = "$base/api/os/info"; TimeoutSec = 20 }
if ((Get-Command Invoke-WebRequest).Parameters.ContainsKey('NoProxy')) { $webArgs['NoProxy'] = $true }

$session = $null
$info = Invoke-WebRequest @webArgs -SessionVariable session
$osInfo = $info.Content | ConvertFrom-Json
Write-Host ("设备：" + $osInfo.ComputerName + "  " + $osInfo.OsVersion)

$csrf = ($session.Cookies.GetCookies([Uri]$base) | Where-Object { $_.Name -eq 'CSRF-Token' } | Select-Object -First 1)
$csrfHeaders = @{}
if ($csrf) { $csrfHeaders['X-CSRF-Token'] = $csrf.Value }
else { Write-Warning '没有拿到 CSRF-Token，写操作可能被拒' }

# ── 3) 强停已运行的实例 ──────────────────────────────────────────────
function Get-Installed {
    $json = & curl.exe --noproxy '*' --silent --show-error --fail --max-time 20 "$base/api/app/packagemanager/packages"
    if ($LASTEXITCODE -ne 0) { return @() }
    # 部分 W10M 门户返回的包列表 JSON 里混了非 UTF-8 字节（中文包名），
    # ConvertFrom-Json 会整份解析失败（在 InstalledPackages[n].Name 处报错）。
    # 不依赖整份 JSON 合法：直接用正则把包全名抠出来。
    $names = [regex]::Matches($json, '"PackageFullName"\s*:\s*"([^"]+)"') |
              ForEach-Object { $_.Groups[1].Value }
    return @($names | Where-Object { $_ -like 'com.yanshuai.messages_*' } |
             ForEach-Object { [pscustomobject]@{ PackageFullName = $_ } })
}

$existing = Get-Installed | Select-Object -First 1
if ($existing) {
    Write-Host ("已装：" + $existing.PackageFullName + "，先强停")
    $encoded = [Uri]::EscapeDataString([Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($existing.PackageFullName)))
    $stopArgs = @{ Uri = "$base/api/taskmanager/app?package=$encoded&forcestop=yes"; Method = 'Delete'; TimeoutSec = 20 }
    if ($webArgs.ContainsKey('NoProxy')) { $stopArgs['NoProxy'] = $true }
    try { Invoke-WebRequest @stopArgs | Out-Null } catch { Write-Warning '强停失败，继续尝试安装' }
}

# ── 4) 上传（curl.exe -F，见文件头的坑 2）────────────────────────────
$curlArgs = @(
    '--noproxy', '*', '--silent', '--show-error', '--fail-with-body', '--max-time', '300',
    '-X', 'POST',
    "$base/api/app/packagemanager/package?package=$([Uri]::EscapeDataString($package.Name))",
    '-F', "package=@$($package.FullName);type=application/octet-stream"
)

$depsDir = Join-Path $package.DirectoryName 'Dependencies\ARM'
if (Test-Path -LiteralPath $depsDir) {
    foreach ($dep in Get-ChildItem -LiteralPath $depsDir -Filter '*.appx' -ErrorAction SilentlyContinue) {
        Write-Host ("依赖：" + $dep.Name)
        $curlArgs += @('-F', "dependency=@$($dep.FullName);type=application/octet-stream")
    }
}

if ($csrf) {
    $curlArgs += @('-H', "X-CSRF-Token: $($csrf.Value)", '-H', "Cookie: CSRF-Token=$($csrf.Value)")
}

Write-Host '上传中（8MB 包在 USB 上大约 20–60 秒）…'
& curl.exe @curlArgs
if ($LASTEXITCODE -ne 0) { throw '上传失败' }
Write-Host '上传完成，等待设备安装…'

# ── 5) 轮询安装状态 ─────────────────────────────────────────────────
# 注意：安装进行中时该端点会超时或返回非 200，这属于正常。
# 所以这里必须 catch 所有异常 —— PS7 的超时抛的不是 WebException
# （是 TaskCanceledException/HttpRequestException），只 catch WebException 会直接中断。
$sawSuccess = $false
for ($attempt = 1; $attempt -le 90; $attempt++) {
    Start-Sleep -Seconds 2
    $stateArgs = @{ Uri = "$base/api/app/packagemanager/state"; Headers = $csrfHeaders; TimeoutSec = 10 }
    if ($webArgs.ContainsKey('NoProxy')) { $stateArgs['NoProxy'] = $true }

    try {
        $state = Invoke-WebRequest @stateArgs
        if ($state.StatusCode -eq 200) {
            Write-Host $state.Content
            $parsed = $state.Content | ConvertFrom-Json
            if ($parsed.Success -eq $false) { throw ("设备侧安装失败：" + $state.Content) }
            $sawSuccess = $true
            break
        }
    } catch {
        # 还在装：继续等。真正的失败在下面用"查得到包"来判断。
    }
}

if (-not $sawSuccess) {
    Write-Warning '没拿到安装完成回执，改用"包是否已注册"判断'
}

$installed = Get-Installed | Select-Object -First 1
if (-not $installed) { throw '安装后查不到包，部署可能失败了' }

Write-Host ''
Write-Host '部署完成'
Write-Host ("  包全名  : " + $installed.PackageFullName)
Write-Host ("  包族名  : " + $installed.PackageFamilyName)
Write-Host ("  版本    : " + $installed.Version)
