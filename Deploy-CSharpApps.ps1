<#
.SYNOPSIS
    课题组工作站 C# 应用部署脚本
.DESCRIPTION
    编译发布 LabWorkstation 解决方案的三个应用并完成部署：
      - LabWorkstation.TrayApp  → 当前用户开机启动（悬浮导航+托盘）
      - LabWorkstation.Admin    → 部署到公共目录（管理员账户管理工具）
      - LabWorkstation.Monitor  → 注册为 Windows 服务（后台资源监控）
.PARAMETER DeployDir
    部署目标目录，默认 D:\GroupData\_公共\Tools
.PARAMETER SkipBuild
    跳过编译，直接部署已有产物。
.EXAMPLE
    .\Deploy-CSharpApps.ps1
.NOTES
    需以管理员身份运行（注册服务需要）。
#>
[CmdletBinding()]
param(
    [string]$DeployDir = "D:\GroupData\_公共\Tools",
    [switch]$SkipBuild
)

#Requires -RunAsAdministrator

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$solution = Join-Path $repoRoot 'LabWorkstation.slnx'

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  课题组工作站 C# 应用部署" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan

# ── 1. 编译发布 ──────────────────────────────────────────
if (-not $SkipBuild) {
    Write-Host "`n[1/5] 编译发布解决方案..." -ForegroundColor Yellow
    if (-not (Test-Path $solution)) {
        throw "找不到解决方案文件: $solution"
    }
    dotnet publish $solution -c Release -o "$repoRoot\publish" --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "编译失败，请检查错误。" }
    Write-Host "  -> 编译完成" -ForegroundColor Green
}

$publishDir = Join-Path $repoRoot 'publish'
if (-not (Test-Path $publishDir)) { throw "发布目录不存在: $publishDir" }

# ── 2. 准备部署目录 ──────────────────────────────────────
Write-Host "`n[2/5] 准备部署目录 $DeployDir ..." -ForegroundColor Yellow
New-Item -ItemType Directory -Force -Path $DeployDir | Out-Null
Write-Host "  -> 目录就绪" -ForegroundColor Green

# ── 3. 部署 Admin 管理工具 ───────────────────────────────
Write-Host "`n[3/5] 部署 Admin 管理工具..." -ForegroundColor Yellow
Copy-Item "$publishDir\LabWorkstation.Admin.exe" $DeployDir -Force
Copy-Item "$publishDir\LabWorkstation.Common.dll" $DeployDir -Force
# 复制依赖运行时 DLL
Get-ChildItem "$publishDir\*.dll" | Where-Object { $_.Name -like 'System.*' -or $_.Name -like 'Microsoft.*' } |
    Copy-Item -Destination $DeployDir -Force
Copy-Item "$publishDir\LabWorkstation.Admin.dll" $DeployDir -Force -ErrorAction SilentlyContinue
Copy-Item "$publishDir\LabWorkstation.Admin.runtimeconfig.json" $DeployDir -Force -ErrorAction SilentlyContinue
Write-Host "  -> Admin 已部署到 $DeployDir\LabWorkstation.Admin.exe" -ForegroundColor Green

# ── 4. 部署 TrayApp 到开机启动 ───────────────────────────
Write-Host "`n[4/5] 部署 TrayApp 悬浮导航（开机启动）..." -ForegroundColor Yellow
$startupDir = [Environment]::GetFolderPath('Startup')
$trayAppSrc = "$publishDir\LabWorkstation.TrayApp.exe"
if (Test-Path $trayAppSrc) {
    # 复制到部署目录
    Copy-Item $trayAppSrc $DeployDir -Force
    Copy-Item "$publishDir\LabWorkstation.TrayApp.dll" $DeployDir -Force -ErrorAction SilentlyContinue
    Copy-Item "$publishDir\LabWorkstation.TrayApp.runtimeconfig.json" $DeployDir -Force -ErrorAction SilentlyContinue

    # 创建开机启动快捷方式
    $shortcutPath = Join-Path $startupDir '课题组工作站导航.lnk'
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($shortcutPath)
    $shortcut.TargetPath = Join-Path $DeployDir 'LabWorkstation.TrayApp.exe'
    $shortcut.WorkingDirectory = $DeployDir
    $shortcut.WindowStyle = 1
    $shortcut.Description = '课题组工作站悬浮导航'
    $shortcut.Save()
    Write-Host "  -> TrayApp 已部署，开机启动快捷方式: $shortcutPath" -ForegroundColor Green
} else {
    Write-Host "  -> 警告：未找到 TrayApp.exe，跳过" -ForegroundColor Red
}

# ── 5. 注册 Monitor 为 Windows 服务 ──────────────────────
Write-Host "`n[5/5] 注册 Monitor 监控服务..." -ForegroundColor Yellow
$monitorExe = Join-Path $DeployDir 'LabWorkstation.Monitor.exe'
$monitorSrc = "$publishDir\LabWorkstation.Monitor.exe"
if (Test-Path $monitorSrc) {
    Copy-Item $monitorSrc $DeployDir -Force
    Copy-Item "$publishDir\LabWorkstation.Monitor.dll" $DeployDir -Force -ErrorAction SilentlyContinue
    Copy-Item "$publishDir\LabWorkstation.Monitor.runtimeconfig.json" $DeployDir -Force -ErrorAction SilentlyContinue
    Copy-Item "$publishDir\appsettings.json" $DeployDir -Force -ErrorAction SilentlyContinue

    $serviceName = 'LabMonitor'
    $existing = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
    if ($existing) {
        Write-Host "  -> 服务已存在，停止并删除旧服务..." -ForegroundColor Yellow
        if ($existing.Status -eq 'Running') { Stop-Service $serviceName -Force }
        sc.exe delete $serviceName | Out-Null
        Start-Sleep -Seconds 2
    }

    sc.exe create $serviceName binPath= "$monitorExe" start= auto obj= LocalSystem DisplayName= "课题组工作站资源监控" | Out-Null
    if ($LASTEXITCODE -eq 0) {
        sc.exe description $serviceName "课题组工作站后台资源监控守护进程（CPU/内存/磁盘/GPU/长时进程）" | Out-Null
        Start-Service $serviceName
        Write-Host "  -> 服务 $serviceName 已创建并启动" -ForegroundColor Green
    } else {
        Write-Host "  -> 服务创建失败" -ForegroundColor Red
    }
} else {
    Write-Host "  -> 警告：未找到 Monitor.exe，跳过" -ForegroundColor Red
}

# ── 完成 ─────────────────────────────────────────────────
Write-Host "`n========================================" -ForegroundColor Cyan
Write-Host "  部署完成！" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Cyan
Write-Host @"
部署摘要：
  - Admin 管理工具 : $DeployDir\LabWorkstation.Admin.exe
  - TrayApp 悬浮导航: $DeployDir\LabWorkstation.TrayApp.exe (开机启动)
  - Monitor 监控服务: Windows 服务 'LabMonitor' (LocalSystem, 自动启动)

后续操作：
  - 双击 LabWorkstation.Admin.exe 启动管理工具（会自动提权）
  - 重新登录或运行 TrayApp.exe 启动悬浮导航
  - 用 Get-Service LabMonitor 查看监控服务状态
  - 卸载服务：sc.exe delete LabMonitor
"@ -ForegroundColor White
