<#
.SYNOPSIS
    ApiSiteAnalyzer 部署脚本——编译到暂存区，成功后才切换槽位（宿主级改动的第 4 层）。

.DESCRIPTION
    四层生效链（对应 design-shell-hotreload §三）：
      改页面    → 直接改 data/web/index.html，刷新浏览器
      改设置    → 直接改 data/settings.json，下一次请求生效
      改业务核心 → 编译 Core → POST /api/service/reload（进程内换核，本脚本不管）
      改宿主机   → 本脚本（编译 → 暂存 → 切槽 → 起）

    🔴 布局（非单文件 + 每槽一个 core 子目录）：
      ApiSiteAnalyzer_A.exe / _B.exe            宿主（薄层）
      ApiSiteAnalyzer_A.dll / _A.runtimeconfig.json / _A.deps.json
      ApiSiteAnalyzer.Contract.dll              契约（两槽共用一份，必须在默认 ALC）
      core\A\ / core\B\                         业务核心 + 第三方依赖（影子副本从这里复制）
    每槽独立 core 目录 → 槽切换时 Core 与宿主天然同版本，不会出现「旧 exe + 新 Core」错配。

    两条硬判据（第一轮，不得回归）：
      1) 编译失败 → 运行区一字未动（不出现"半新半旧"的运行区）
      2) 运行中的槽位不覆盖（exe 锁自身文件）——产物进另一个槽，由 启动.bat 起最新一份

.PARAMETER Start
    部署完成后顺手起面板（等同双击 启动.bat）。

.PARAMETER Release
    走 Release 配置（默认 Debug）。

.EXAMPLE
    .\部署.ps1
    .\部署.ps1 -Release -Start
#>
[CmdletBinding()]
param(
    [switch]$Start,
    [switch]$Release
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$hostProj = Join-Path $root 'ApiSiteAnalyzer\ApiSiteAnalyzer.csproj'
$coreProj = Join-Path $root 'ApiSiteAnalyzer.Core\ApiSiteAnalyzer.Core.csproj'
# 暂存区与 csproj 的 PublishDir 同址（csproj 在 ApiSiteAnalyzer/ 下，其 `..\..` = WorkSpace）
$staging = Join-Path $root '..\tools\CatTemp\asa-publish'
$stagingCore = Join-Path $root '..\tools\CatTemp\asa-publish-core'
$backupDir = Join-Path $root '..\tools\CatTemp\asa-backup'
$port = 8766

if (-not (Test-Path $hostProj)) { throw "找不到宿主工程：$hostProj" }
if (-not (Test-Path $coreProj)) { throw "找不到核心工程：$coreProj" }

# ---- 1. 记录部署前状态：哪个槽在跑（运行中的文件不可覆盖） -------------
$runningSlotDir = ''
$line = netstat -ano | Select-String -Pattern ":$port\s" | Select-String -Pattern 'LISTENING' | Select-Object -First 1
if ($line) {
    $pidText = ($line.ToString().Trim() -split '\s+')[-1]
    try {
        $runningPath = (Get-Process -Id ([int]$pidText) -ErrorAction Stop).Path
        # 非单文件布局：槽 = **目录**（apphost 固定按 ApiSiteAnalyzer.dll 找伴生文件，exe 不能改名）
        $runningSlotDir = Split-Path -Parent $runningPath
        Write-Host "运行中：$runningSlotDir（部署后需由 启动.bat 切换）"
    }
    catch {
        Write-Warning "端口 $port 被 PID $pidText 占用，但读不到进程路径（权限不足？）——按「无运行实例」处理"
        $runningSlotDir = ''
    }
}

# ---- 2. 编译到暂存区（不触碰运行区） ------------------------------------
$config = if ($Release) { 'Release' } else { 'Debug' }
Write-Host "编译宿主（$config）→ 暂存区 ..."
dotnet publish $hostProj -c $config -r win-x64 --self-contained false -v minimal
if ($LASTEXITCODE -ne 0) {
    Write-Error "宿主编译失败（退出码 $LASTEXITCODE）——运行区未改动。"
    exit 1
}

Write-Host "编译核心（$config）→ 暂存区 ..."
dotnet publish $coreProj -c $config -r win-x64 --self-contained false -v minimal
if ($LASTEXITCODE -ne 0) {
    Write-Error "核心编译失败（退出码 $LASTEXITCODE）——运行区未改动。"
    exit 1
}

$built = Join-Path $staging 'ApiSiteAnalyzer.exe'
$builtCore = Join-Path $stagingCore 'ApiSiteAnalyzer.Core.dll'
if (-not (Test-Path $built)) { Write-Error "暂存区没有宿主产物：$built ——运行区未改动。"; exit 1 }
if (-not (Test-Path $builtCore)) { Write-Error "暂存区没有核心产物：$builtCore ——运行区未改动。"; exit 1 }

# 契约由默认 ALC 持有：core 目录里那份必须删掉（两份会让类型身份劈开，cast 失败）
Get-ChildItem -Path $stagingCore -Filter 'ApiSiteAnalyzer.Contract.*' -File -ErrorAction SilentlyContinue |
    ForEach-Object { Remove-Item -LiteralPath $_.FullName -Force }

$newVersion = (Get-Item $built).VersionInfo.FileVersion
$newCoreVersion = (Get-Item $builtCore).VersionInfo.FileVersion
$coreFiles = (Get-ChildItem -Path $stagingCore -Recurse -File).Count
Write-Host "宿主 v$newVersion · 核心 v$newCoreVersion（$coreFiles 个文件）"

# ---- 3. 选空闲槽：运行中的那个跳过；都没在跑就挑 slot_A -----------------
# 🔴 非单文件发布后槽位是**目录**：apphost 固定按 ApiSiteAnalyzer.dll 找伴生文件，exe 不能改名
$slots = @('slot_A', 'slot_B')
$targetDir = ''
foreach ($name in $slots) {
    $candidate = Join-Path $root $name
    if ($candidate -ne $runningSlotDir) { $targetDir = $candidate; break }
}
if (-not $targetDir) { throw "两个槽都不可写（运行中的是 $runningSlotDir）——先关掉面板再部署" }

$targetCoreDir = Join-Path $targetDir 'core'

# ---- 4. 旧槽整目录转备份，再铺新槽（搬失败可从备份恢复） ----------------
if (-not (Test-Path $backupDir)) { New-Item -ItemType Directory -Path $backupDir | Out-Null }
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
if (Test-Path $targetDir) {
    Move-Item -LiteralPath $targetDir -Destination (Join-Path $backupDir "slot.bak-$stamp") -Force
    Write-Host "旧槽备份：slot.bak-$stamp"
}

New-Item -ItemType Directory -Path $targetDir -Force | Out-Null
Copy-Item -Path (Join-Path $staging '*') -Destination $targetDir -Recurse -Force
New-Item -ItemType Directory -Path $targetCoreDir -Force | Out-Null
Copy-Item -Path (Join-Path $stagingCore '*') -Destination $targetCoreDir -Recurse -Force
Write-Host "已部署：$(Split-Path -Leaf $targetDir)\ApiSiteAnalyzer.exe + core\"

# ---- 5. 可选：起面板 ----------------------------------------------------
if ($Start) {
    $launcher = Join-Path $root '启动.bat'
    if (Test-Path $launcher) {
        Write-Host "启动面板 ..."
        Start-Process -FilePath $launcher -WorkingDirectory $root
    }
    else {
        Write-Warning "找不到 启动.bat——请手动双击启动。"
    }
}
else {
    Write-Host "部署完成。双击 启动.bat 切到新版本（正在跑的那份不受影响）。"
}
