# tools/Build-AppxBundle.ps1
#
# 编译 → 逐架构签名 → 合成 appxbundle → 签 bundle。
#
# 为什么绕开 MSBuild 自带的 AppxBundle=Always，改成显式三步走：
#
#   1) 单次 msbuild 调用只构建一个架构。Microsoft.AppxPackage.Targets 里
#      _CreateBundle 的输入是 @(_MainPackageToBundle)，只含"当前正在构建的
#      架构"；AppxBundlePlatforms 不显式指定时只会按当前架构推算，并报
#      APPX4001 警告。多架构 bundle 依赖 "Create App Packages" 向导内部的
#      多轮构建编排，命令行一把梭做不到。
#   2) csproj 里钉了 AppxBundle=Never / AppxPackageSigningEnabled=false
#      （见工程文件头说明），每次手工 /p: 覆盖既容易漏、也容易被后来的提交
#      覆盖掉。
#
# 所以：逐架构构建（与 Deploy-Phone 的约定一致：先不签名）
#   → signtool 逐包签名（证书与 Deploy-Phone 同一套：CN=LockWizard，CurrentUser\My）
#   → makeappx bundle 合成 → signtool 签 bundle。
#
# 签名校验为什么用 signtool 而不是 SignedCms（实测）：
#   appx/appxbundle 里的 AppxSignature.p7x 不是标准 PKCS7 SignedData 外壳，
#   System.Security.Cryptography.Pkcs.SignedCms.Decode 直接抛
#   "ASN1 corrupted data"，验不了。而 `signtool verify /pa` 认识这种格式，
#   对 .appx 和 .appxbundle 都能校验成功。注意必须带 /pa：证书是自签名的，
#   不带 /pa 的默认策略会以"根证书不受信"失败（exit 1），带 /pa 才过。
#
# 产物命名与 VS "Create App Packages" 一致：
#   src\Yanshuai.Messages\AppPackages\Yanshuai.Messages_<版本>_<架构列表>_Test\
#     Yanshuai.Messages_<版本>_<架构列表>.appx        逐架构包（手机部署用这个）
#     Yanshuai.Messages_<版本>_<架构列表>.appxbundle  合成包（桌面侧载用）
#
# 两个注意点：
#   * bundle 是给桌面侧载/商店用的；Windows 10 Mobile 真机装不了 bundle，
#     手机部署仍然走 tools\Deploy-Phone.ps1（逐架构 .appx）。
#   * 签名用 /fd SHA256，但**不打时间戳**（本机无外网）。签名在证书到期
#     （2029-04-18）前一直有效；之后需要换证书重签。

[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string[]]$Platforms = @('x86', 'x64', 'ARM'),
    [string]$Thumbprint = '',
    [string]$CertSubject = 'CN=LockWizard',
    [switch]$SkipBuild,
    [string]$PyUwpRuntimeDir = ''
)

$ErrorActionPreference = 'Stop'

$repoRoot    = Split-Path -Parent $PSScriptRoot
$projectDir  = Join-Path $repoRoot 'src\Yanshuai.Messages'
$projectFile = Join-Path $projectDir 'Yanshuai.Messages.csproj'
$appPackages = Join-Path $projectDir 'AppPackages'

# ── 工具链：钉 VS2017 MSBuild 15 + SDK 19041（与 csproj 头说明保持一致）────
$msbuild = 'C:\Program Files (x86)\Microsoft Visual Studio\2017\Community\MSBuild\15.0\Bin\MSBuild.exe'
if (-not (Test-Path -LiteralPath $msbuild)) { throw "找不到 MSBuild 15：$msbuild" }

function Get-SdkTool([string]$name, [string]$pinnedVersion) {
    $all = Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin' -Recurse -Filter $name -ErrorAction SilentlyContinue |
           Where-Object { $_.FullName -match '\\x64\\' }
    if (-not $all) { throw "Windows Kits 里找不到 $name" }
    $pinned = $all | Where-Object { $_.FullName -like "*\bin\$pinnedVersion\*" } | Select-Object -First 1
    if ($pinned) { return $pinned.FullName }
    $newest = $all | Sort-Object FullName -Descending | Select-Object -First 1
    $ver = [regex]::Match($newest.FullName, 'bin\\([\d\.]+)\\').Groups[1].Value
    Write-Warning "没有 $pinnedVersion 版的 $name，退回 $ver"
    return $newest.FullName
}

$signtool = Get-SdkTool 'signtool.exe' '10.0.19041.0'
$makeappx = Get-SdkTool 'makeappx.exe' '10.0.19041.0'
Write-Host ("MSBuild   : " + $msbuild)
Write-Host ("signtool  : " + $signtool)
Write-Host ("makeappx  : " + $makeappx)

# ── 版本号从清单读，避免两处各写一份 ────────────────────────────────────
$manifestDoc = [xml](Get-Content -LiteralPath (Join-Path $projectDir 'Package.appxmanifest') -Raw)
$version   = $manifestDoc.Package.Identity.Version
$publisher = $manifestDoc.Package.Identity.Publisher
$debugTag  = if ($Configuration -eq 'Debug') { '_Debug' } else { '' }
$archesLabel = ($Platforms -join '_')
Write-Host ("包        : " + $manifestDoc.Package.Identity.Name + "  v" + $version + "  " + $publisher)

# ── 签名：p7x 存在只证明"签过"，signtool verify /pa 才是真校验 ────────────
function Test-HasAppxSignature([string]$path) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem -ErrorAction SilentlyContinue
    $zip = [System.IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $path))
    try {
        return ($null -ne ($zip.Entries | Where-Object { $_.FullName -eq 'AppxSignature.p7x' }))
    } finally {
        $zip.Dispose()
    }
}

function Confirm-Signed([string]$path) {
    if (-not (Test-HasAppxSignature $path)) { return $false }
    # 切到文件所在目录再传文件名：仓库路径含中文，signtool 对完整路径的
    # 回显会乱码（功能不受影响，但日志没法看）
    $name = Split-Path -Leaf $path
    Push-Location (Split-Path -Parent $path)
    try {
        & $signtool verify /pa $name > $null 2>&1
        return ($LASTEXITCODE -eq 0)
    } finally {
        Pop-Location
    }
}

function Invoke-Sign([string]$path) {
    $name = Split-Path -Leaf $path
    Push-Location (Split-Path -Parent $path)
    try {
        & $signtool sign /fd SHA256 /sha1 $cert.Thumbprint $name
        if ($LASTEXITCODE -ne 0) { throw "signtool 签名失败：$name" }
    } finally {
        Pop-Location
    }
}

# ── 证书：与 Deploy-Phone 同规则（CN=LockWizard、带私钥、取最晚到期）────────
if ($Thumbprint) {
    $cert = Get-Item ("Cert:\CurrentUser\My\{0}" -f $Thumbprint) -ErrorAction SilentlyContinue
    if (-not $cert -or -not $cert.HasPrivateKey) { throw "指定的证书不可用或无私钥：$Thumbprint" }
} else {
    $cert = Get-ChildItem Cert:\CurrentUser\My |
            Where-Object { $_.Subject -eq $CertSubject -and $_.HasPrivateKey } |
            Sort-Object NotAfter -Descending | Select-Object -First 1
    if (-not $cert) { throw "找不到证书 $CertSubject（需要带私钥）" }
}
Write-Host ("签名证书  : " + $cert.Subject + "  " + $cert.Thumbprint + "  到期 " + $cert.NotAfter.ToString('yyyy-MM-dd'))

# ── 1) 逐架构构建 ───────────────────────────────────────────────────────
$appxFiles = @{}
foreach ($platform in $Platforms) {
    $tag    = "{0}{1}" -f $platform, $debugTag
    $pkgDir = Join-Path $appPackages ("Yanshuai.Messages_{0}_{1}_Test" -f $version, $tag)
    $appx   = Join-Path $pkgDir ("Yanshuai.Messages_{0}_{1}.appx" -f $version, $tag)

    if (-not $SkipBuild) {
        Write-Host ''
        Write-Host "== 构建 $platform / $Configuration ==" -ForegroundColor Cyan
        $extra = @()
        # 不要给值加引号：MSBuild 会把引号当成值的一部分，Exists() 判断就会失败，PyUwp 载荷会整个丢掉。传无空格路径。
        if ($PyUwpRuntimeDir) { $extra += "/p:PyUwpRuntimeDir=$PyUwpRuntimeDir" }
        & $msbuild $projectFile /t:Build /nologo /v:m /nodeReuse:false `
            /p:Configuration=$Configuration /p:Platform=$platform `
            /p:AppxBundle=Never /p:AppxPackageSigningEnabled=false @extra
        if ($LASTEXITCODE -ne 0) { throw "$platform 构建失败（exit $LASTEXITCODE）" }
    }
    if (-not (Test-Path -LiteralPath $appx)) { throw "找不到包：$appx" }
    $appxFiles[$platform] = $appx
}

# ── 2) 逐包签名 ─────────────────────────────────────────────────────────
foreach ($platform in $Platforms) {
    $appx = $appxFiles[$platform]
    $name = Split-Path -Leaf $appx
    if (Confirm-Signed $appx) {
        Write-Host "已签名，跳过：$name"
        continue
    }
    Write-Host "签名：$name"
    Invoke-Sign $appx
    if (-not (Confirm-Signed $appx)) { throw "签名后校验失败：$name" }
}

# ── 3) 合成 bundle（只放 appx，别把 Dependencies 带进去）─────────────────
$bundleDir  = Join-Path $appPackages ("Yanshuai.Messages_{0}_{1}_Test" -f $version, $archesLabel)
$bundlePath = Join-Path $bundleDir ("Yanshuai.Messages_{0}_{1}.appxbundle" -f $version, $archesLabel)
$stage      = Join-Path $bundleDir '_stage'

New-Item -ItemType Directory -Path $bundleDir -Force | Out-Null
if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
New-Item -ItemType Directory -Path $stage | Out-Null
foreach ($platform in $Platforms) {
    Copy-Item -LiteralPath $appxFiles[$platform] -Destination $stage
}

Write-Host ''
Write-Host "== 合成 bundle ==" -ForegroundColor Cyan
# /bv 不能省：不传的话 makeappx 用当前日期时间当 bundle 版本，而 VS 的
# MakeAppxBundle 任务传的是应用版本（AppxBundleManifestVersion），两者不一致。
& $makeappx bundle /bv $version /d $stage /p $bundlePath /o
if ($LASTEXITCODE -ne 0) { throw 'makeappx bundle 失败' }
Remove-Item -LiteralPath $stage -Recurse -Force

if (-not (Confirm-Signed $bundlePath)) {
    Write-Host "签名 bundle"
    Invoke-Sign $bundlePath
    if (-not (Confirm-Signed $bundlePath)) { throw "bundle 签名后校验失败" }
}

# ── 4) 汇总 ─────────────────────────────────────────────────────────────
function Get-FileSha256([string]$path) {
    return (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
}

Write-Host ''
Write-Host '== 产物 ==' -ForegroundColor Green
foreach ($platform in $Platforms) {
    $f = Get-Item -LiteralPath $appxFiles[$platform]
    Write-Host ("  {0,-36} {1,8:N2} MB  {2}" -f $f.Name, ($f.Length / 1MB), (Get-FileSha256 $f.FullName))
}
$b = Get-Item -LiteralPath $bundlePath
Write-Host ("  {0,-36} {1,8:N2} MB  {2}" -f $b.Name, ($b.Length / 1MB), (Get-FileSha256 $b.FullName))

# bundle 清单：确认架构都在
Add-Type -AssemblyName System.IO.Compression.FileSystem -ErrorAction SilentlyContinue
$zip = [System.IO.Compression.ZipFile]::OpenRead($bundlePath)
try {
    $entry = $zip.Entries | Where-Object { $_.FullName -eq 'AppxMetadata/AppxBundleManifest.xml' }
    if ($entry) {
        $reader = New-Object System.IO.StreamReader($entry.Open())
        $xmlText = $reader.ReadToEnd()
        $reader.Close()
        $bx = [xml]$xmlText
        $archs = ($bx.Bundle.Packages.Package | ForEach-Object { $_.Architecture }) -join ', '
        Write-Host ("  bundle 内架构: " + $archs)
    } else {
        Write-Warning 'bundle 里没有 AppxBundleManifest.xml？'
    }
} finally { $zip.Dispose() }

Write-Host ''
Write-Host '完成'
Write-Host ("  appx   : " + $appxFiles[$Platforms[0]])
Write-Host ("  bundle : " + $bundlePath)