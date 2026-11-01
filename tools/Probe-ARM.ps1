# tools/Probe-ARM.ps1
#
# 真机二分定位：一次调用完成「构建 → 部署 → 启动 → 读埋点」。
#
# 为什么需要它：ARM 上的启动崩溃只在真机复现（x86 Release 在桌面上完全正常），
# 所以每验证一个假设都要走一遍真机。手工敲四步命令既慢又容易漏，
# 于是把整条链路封成一个脚本，一次调用就能拿到结论。
#
# 用法：
#   .\Probe-ARM.ps1                    # 构建当前代码并测
#   .\Probe-ARM.ps1 -NoBuild           # 不改代码、只重测（部署仍是必须的）
#   .\Probe-ARM.ps1 -NoPyUwp           # 构建时不带 PyUwp 载荷（缩小包体）

[CmdletBinding()]
param(
    [string]$DeviceOrigin = 'http://192.168.3.159',
    [switch]$NoBuild,
    [switch]$NoPyUwp
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$base = $DeviceOrigin.TrimEnd('/')
$pkgFullName = 'com.yanshuai.messages_1.0.0.0_arm__amaw0954g0pkg'

if (-not $NoBuild) {
    $msbuild = 'C:\Program Files (x86)\Microsoft Visual Studio\2017\Community\MSBuild\15.0\Bin\MSBuild.exe'
    $buildArgs = @(
        (Join-Path $repoRoot 'src\Yanshuai.Messages\Yanshuai.Messages.csproj'),
        '/p:Configuration=Release', '/p:Platform=ARM', '/v:minimal', '/nologo'
    )
    if ($NoPyUwp) {
        # 指向一个不存在的目录 → 载荷自动关闭；但必须先清掉上次的 bin/obj，
        # 否则打包目标会把残留的 Lib\ 一起收进去（踩过）
        Remove-Item -LiteralPath (Join-Path $repoRoot 'src\Yanshuai.Messages\obj\ARM\Release'),
                                  (Join-Path $repoRoot 'src\Yanshuai.Messages\bin\ARM\Release') -Recurse -Force -ErrorAction SilentlyContinue
        $buildArgs += '/p:PyUwpRuntimeDir=C:\__nonexistent__'
    }

    $buildOutput = & $msbuild @buildArgs 2>&1
    $errors = $buildOutput | Select-String -Pattern 'error '
    if ($errors) {
        Write-Host '构建失败，已中止（避免拿旧包测出假结论）：' -ForegroundColor Red
        $errors | Select-Object -First 8 | ForEach-Object { Write-Host ('  ' + $_.Line.Trim()) }
        return
    }
    Write-Host '构建成功'
}

# 部署
& (Join-Path $PSScriptRoot 'Deploy-Phone.ps1') -DeviceOrigin $base 2>&1 |
    Select-String -Pattern '包：|部署完成|失败' | Select-Object -Last 2

# 清掉上次的埋点，这样看到的只可能是这一次启动
$encPkg = [Uri]::EscapeDataString($pkgFullName)
& curl.exe --noproxy '*' --silent --show-error --max-time 20 -X DELETE `
    "$base/api/filesystem/apps/file?knownfolderid=LocalAppData&packagefullname=$encPkg&path=%5CLocalState&filename=startup-trace.log" 2>&1 | Out-Null

# 启动
$appId = [Uri]::EscapeDataString([Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes('com.yanshuai.messages_amaw0954g0pkg!App')))
$encFull = [Uri]::EscapeDataString([Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($pkgFullName)))
& curl.exe --noproxy '*' --silent --show-error --max-time 30 -X POST -H 'Content-Length: 0' `
    "$base/api/taskmanager/app?appid=$appId&package=$encFull" 2>&1 | Out-Null

Start-Sleep -Seconds 20

# 结果
Write-Host ''
Write-Host '════ 埋点 ════'
& (Join-Path $PSScriptRoot 'Read-PhoneDiagnostics.ps1') -DeviceOrigin $base -File 'startup-trace.log'

Write-Host ''
Write-Host '════ 进程 ════'
$p = & curl.exe --noproxy '*' --silent --show-error --max-time 20 "$base/api/resourcemanager/processes"
$hit = (($p | ConvertFrom-Json).Processes | Where-Object { $_.PackageFullName -match 'yanshuai' })
if ($hit) { Write-Host ('仍在运行，内存 ' + [math]::Round($hit.PageFileUsage / 1MB, 1) + 'MB') }
else { Write-Host '进程已退出' }
