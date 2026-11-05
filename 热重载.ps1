<#
.SYNOPSIS
    进程内换核——编译 Core → 覆盖当前槽的 core 目录 → POST /api/service/reload → 等结果。

.DESCRIPTION
    对应「改动 → 生效」链第 3 层：只改业务核心（Web / Store / Sites / Browser / Collect / Config）时用它。
    宿主 exe 与契约（IShellCore）改动不走这里——那要走 部署.ps1（进程重启）。

    做的事：
      1) 从 8766 端口反查运行实例 → 槽名（A / B）与 core 目录
      2) dotnet publish 核心到暂存区（运行区不动，编译失败即停）
      3) 覆盖 core\<槽>\（影子副本已加载，源 dll 不被锁，可直接覆盖）
      4) 读 data\run\service.token，POST /api/service/reload
      5) 轮询 data\run\reload.result.json（等 mtime 变新）→ 打印 ok / note / error

.PARAMETER Config
    配置文件路径（默认脚本同级 config.json——它决定 data 目录位置）。

.EXAMPLE
    .\热重载.ps1
#>
[CmdletBinding()]
param(
    [string]$Config = '',
    [int]$Port = 8766
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$coreProj = Join-Path $root 'ApiSiteAnalyzer.Core\ApiSiteAnalyzer.Core.csproj'
$stagingCore = Join-Path $root '..\tools\CatTemp\asa-publish-core'
$port = $Port

if (-not $Config) { $Config = Join-Path $root 'config.json' }
$dataDir = Join-Path (Split-Path -Parent ([IO.Path]::GetFullPath($Config))) 'data'
$runDir = Join-Path $dataDir 'run'
$tokenPath = Join-Path $runDir 'service.token'
$resultPath = Join-Path $runDir 'reload.result.json'

# ---- 1. 定位运行实例 → 槽名 --------------------------------------------
$line = netstat -ano | Select-String -Pattern ":$port\s" | Select-String -Pattern 'LISTENING' | Select-Object -First 1
if (-not $line) { throw "端口 $port 没有监听——面板没在跑，无需热重载（直接 启动.bat）" }
$pidText = ($line.ToString().Trim() -split '\s+')[-1]
$proc = Get-Process -Id ([int]$pidText) -ErrorAction Stop
# 非单文件布局：槽 = **目录**（apphost 固定按 ApiSiteAnalyzer.dll 找伴生文件，exe 不能改名）
$slotDir = Split-Path -Parent $proc.Path
Write-Host "运行实例：pid $pidText · 槽 $slotDir · $($proc.Path)"

$targetCore = Join-Path $slotDir 'core'
if (-not (Test-Path (Join-Path $targetCore 'ApiSiteAnalyzer.Core.dll'))) {
    throw "槽 $slotDir 没有 core 目录：$targetCore（先用 部署.ps1 铺一次新布局）"
}

# ---- 2. 编译核心到暂存区（运行区不动） ----------------------------------
Write-Host "编译核心 → 暂存区 ..."
dotnet publish $coreProj -c Debug -r win-x64 --self-contained false -v minimal
if ($LASTEXITCODE -ne 0) { throw "核心编译失败（退出码 $LASTEXITCODE）——运行区未改动" }

$builtCore = Join-Path $stagingCore 'ApiSiteAnalyzer.Core.dll'
if (-not (Test-Path $builtCore)) { throw "暂存区没有核心产物：$builtCore" }

# 契约必须由默认 ALC 持有——core 目录里那份删掉（两份会让类型身份劈开）
Get-ChildItem -Path $stagingCore -Filter 'ApiSiteAnalyzer.Contract.*' -File -ErrorAction SilentlyContinue |
    ForEach-Object { Remove-Item -LiteralPath $_.FullName -Force }

# ---- 3. 覆盖当前槽的 core 目录（影子副本在跑，源 dll 不被锁） -----------
Copy-Item -Path (Join-Path $stagingCore '*') -Destination $targetCore -Recurse -Force
$newVersion = (Get-Item $builtCore).VersionInfo.FileVersion
Write-Host "已覆盖 $targetCore（v$newVersion）"

# ---- 4. 触发换核 --------------------------------------------------------
if (-not (Test-Path $tokenPath)) { throw "读不到控制面令牌：$tokenPath（面板版本太旧？先跑 部署.ps1）" }
$token = (Get-Content -LiteralPath $tokenPath -Raw -Encoding UTF8).Trim()

$before = if (Test-Path $resultPath) { (Get-Item $resultPath).LastWriteTimeUtc } else { [datetime]::MinValue }
$headers = @{ 'X-Reload-Token' = $token }
try {
    $resp = Invoke-RestMethod -Uri "http://127.0.0.1:$port/api/service/reload" -Method Post -Headers $headers -TimeoutSec 10
    Write-Host "已受理：$($resp.note)"
}
catch {
    throw "reload 请求失败：$($_.Exception.Message)"
}

# ---- 5. 等结果（宿主心跳 5 s；给 30 s 窗口） ----------------------------
$deadline = (Get-Date).AddSeconds(30)
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 500
    if (-not (Test-Path $resultPath)) { continue }
    if ((Get-Item $resultPath).LastWriteTimeUtc -le $before) { continue }

    $result = Get-Content -LiteralPath $resultPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($result.ok) {
        Write-Host "换核成功：$($result.note)"
        Write-Host "  pid $($result.pid) · 核心 v$($result.coreVersion) · 影子 $($result.loadedFrom) · 泄漏计数 $($result.leakCount)"
        exit 0
    }

    Write-Error "换核失败：$($result.error)"
    exit 1
}

throw "30 秒内没等到 reload.result.json 更新——看控制台输出"
