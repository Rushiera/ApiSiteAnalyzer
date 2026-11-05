<#
.SYNOPSIS
    ApiSiteAnalyzer 部署脚本——编译到暂存区，成功后才切换槽位。

.DESCRIPTION
    对应「改动 → 生效」链的第 4 层（宿主级改动才走这里）：
      改页面    → 直接改 data/web/index.html，刷新浏览器
      改设置    → 直接改 data/settings.json，下一次请求生效
      改业务代码 → 本脚本

    两条硬判据：
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
$proj = Join-Path $root 'ApiSiteAnalyzer\ApiSiteAnalyzer.csproj'
# 暂存区与 csproj 的 PublishDir 同址（csproj 在 ApiSiteAnalyzer/ 下，其 `..\..` = WorkSpace）
$staging = Join-Path $root '..\tools\CatTemp\asa-publish'
$port = 8766

if (-not (Test-Path $proj)) {
    throw "找不到工程文件：$proj"
}

# ---- 1. 记录部署前状态：哪个槽在跑（运行中的文件不可覆盖） -------------
$runningPath = ''
$line = netstat -ano | Select-String -Pattern ":$port\s" | Select-String -Pattern 'LISTENING' | Select-Object -First 1
if ($line) {
    $pidText = ($line.ToString().Trim() -split '\s+')[-1]
    try {
        $runningPath = (Get-Process -Id ([int]$pidText) -ErrorAction Stop).Path
    }
    catch {
        Write-Warning "端口 $port 被 PID $pidText 占用，但读不到进程路径（权限不足？）——按「无运行实例」处理"
        $runningPath = ''
    }
}
$runningSlot = ''
if ($runningPath) {
    $runningSlot = [IO.Path]::GetFileNameWithoutExtension($runningPath)
    Write-Host "运行中：$runningSlot（部署后需由 启动.bat 切换）"
}

# ---- 2. 编译到暂存区（不触碰运行区） ------------------------------------
$config = if ($Release) { 'Release' } else { 'Debug' }
Write-Host "编译（$config）→ 暂存区 ..."
dotnet publish $proj -c $config -r win-x64 --self-contained false `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -v minimal
if ($LASTEXITCODE -ne 0) {
    Write-Error "编译失败（退出码 $LASTEXITCODE）——运行区未改动。"
    exit 1
}

$built = Join-Path $staging 'ApiSiteAnalyzer.exe'
if (-not (Test-Path $built)) {
    Write-Error "暂存区没有产物：$built ——运行区未改动。"
    exit 1
}
$newVersion = (Get-Item $built).VersionInfo.FileVersion
$newSize = [math]::Round((Get-Item $built).Length / 1MB, 2)
Write-Host "产物：v$newVersion · $newSize MB"

# ---- 3. 选空闲槽：运行中的那个跳过；都没在跑就挑较旧的一个 -------------
$slots = @('ApiSiteAnalyzer_A', 'ApiSiteAnalyzer_B')
$target = ''
foreach ($slot in $slots) {
    if ($slot -ne $runningSlot) { $target = $slot; break }
}
if (-not $target) {
    throw "两个槽都不可写（运行中的是 $runningSlot）——先关掉面板再部署"
}

$targetExe = Join-Path $root "$target.exe"
$targetDll = Join-Path $root "$target.dll"

# ---- 4. 旧槽转备份，再把新产物放进去（原子性：搬失败可从备份恢复） ------
if (Test-Path $targetExe) {
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    # 备份落仓库外暂存区——不污染仓库根，也不进版本管理
    $backupDir = Join-Path $root '..\tools\CatTemp\asa-backup'
    if (-not (Test-Path $backupDir)) { New-Item -ItemType Directory -Path $backupDir | Out-Null }
    $backup = Join-Path $backupDir "$target.exe.bak-$stamp"
    Move-Item -LiteralPath $targetExe -Destination $backup -Force
    Write-Host "旧槽备份：$backup"
}
if (Test-Path $targetDll) { Remove-Item -LiteralPath $targetDll -Force }

Copy-Item -LiteralPath $built -Destination $targetExe -Force
Write-Host "已部署：$target.exe（v$newVersion）"

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
    Write-Host "部署完成。双击 启动.bat 切到新版本。"
}
