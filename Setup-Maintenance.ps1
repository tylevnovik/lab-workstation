<#
.SYNOPSIS
    课题组工作站 · 系统维护配置脚本
.DESCRIPTION
    一键配置工作站的系统维护策略：
    1. 远程桌面会话超时（防止僵尸会话占用资源）
    2. 每周自动清理临时文件
    3. C 盘空间监控告警
    4. 禁用非管理员用户的 Windows Update 操作
    5. 部署资源监控守护程序（CPU/内存/GPU/磁盘）
    仅管理员可运行。
.NOTES
    使用方法：以管理员身份运行 PowerShell，执行 .\Setup-Maintenance.ps1
#>

#Requires -RunAsAdministrator

$ErrorActionPreference = "Stop"

Write-Host "========================================" -ForegroundColor Cyan
Write-Host " 课题组工作站 · 系统维护配置" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# ==================== 1. 远程桌面会话超时 ====================
Write-Host ">> 配置远程桌面会话超时..." -ForegroundColor White

$tsPath = "HKLM:\SOFTWARE\Policies\Microsoft\Windows NT\Terminal Services"
if (-not (Test-Path $tsPath)) { New-Item -Path $tsPath -Force | Out-Null }

# 断开连接的会话 2 小时后自动注销（7200000ms = 2小时）
Set-ItemProperty -Path $tsPath -Name "MaxDisconnectionTime" -Value 7200000 -Type DWord -Force
Write-Host "  [OK] 断开连接 2 小时后自动注销" -ForegroundColor Green

# 空闲超过 4 小时的会话自动断开（14400000ms = 4小时）
Set-ItemProperty -Path $tsPath -Name "MaxIdleTime" -Value 14400000 -Type DWord -Force
Write-Host "  [OK] 空闲 4 小时后自动断开" -ForegroundColor Green

# 限制每个用户只能有 1 个活跃会话（防止同一用户开多个会话）
Set-ItemProperty -Path $tsPath -Name "fSingleSessionPerUser" -Value 1 -Type DWord -Force
Write-Host "  [OK] 限制每用户仅 1 个活跃会话" -ForegroundColor Green

Write-Host ""

# ==================== 2. 每周自动清理 ====================
Write-Host ">> 配置每周自动清理任务..." -ForegroundColor White

# 清理脚本内容
$cleanupScript = @'
# 工作站每周自动清理脚本
$ErrorActionPreference = "SilentlyContinue"
$logFile = "D:\GroupData\_公共\_使用手册\cleanup.log"

function Log($msg) {
    $line = "[$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')] $msg"
    Add-Content -Path $logFile -Value $line
}

Log "开始每周清理..."

# 清理所有用户的临时文件夹
Get-ChildItem "C:\Users\*\AppData\Local\Temp" -ErrorAction SilentlyContinue |
    Where-Object { $_.LastWriteTime -lt (Get-Date).AddDays(-7) } |
    Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
Log "已清理用户临时文件（7天前的）"

# 清理系统临时文件夹
Get-ChildItem $env:TEMP -ErrorAction SilentlyContinue |
    Where-Object { $_.LastWriteTime -lt (Get-Date).AddDays(-3) } |
    Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
Log "已清理系统临时文件（3天前的）"

# 清理 Windows Update 下载缓存（仅已完成更新的）
if (Test-Path "C:\Windows\SoftwareDistribution\Download") {
    Get-ChildItem "C:\Windows\SoftwareDistribution\Download" -ErrorAction SilentlyContinue |
        Where-Object { $_.LastWriteTime -lt (Get-Date).AddDays(-14) } |
        Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
    Log "已清理 Windows Update 缓存（14天前的）"
}

# 检查 C 盘空间
$cDrive = Get-PSDrive C
$freeGB = [math]::Round($cDrive.Free / 1GB, 1)
$totalGB = [math]::Round(($cDrive.Used + $cDrive.Free) / 1GB, 1)
$pct = [math]::Round($cDrive.Free / ($cDrive.Used + $cDrive.Free) * 100, 0)
Log "C 盘空间: ${freeGB}GB / ${totalGB}GB (剩余 ${pct}%)"

if ($pct -lt 20) {
    Log "[警告] C 盘剩余空间不足 20%！请及时清理。"
    # 尝试运行 DISM 清理组件
    Dism /Online /Cleanup-Image /StartComponentCleanup /Quiet 2>$null
    Log "已运行 DISM 组件清理"
}

Log "每周清理完成"
'@

$cleanupScriptPath = "C:\Scripts\WeeklyCleanup.ps1"
$scriptDir = Split-Path $cleanupScriptPath -Parent
if (-not (Test-Path $scriptDir)) { New-Item -ItemType Directory -Path $scriptDir -Force | Out-Null }
Set-Content -Path $cleanupScriptPath -Value $cleanupScript -Encoding UTF8

# 创建计划任务
$existingTask = Get-ScheduledTask -TaskName "LabWeeklyCleanup" -ErrorAction SilentlyContinue
if ($existingTask) { Unregister-ScheduledTask -TaskName "LabWeeklyCleanup" -Confirm:$false }

$action  = New-ScheduledTaskAction -Execute "PowerShell.exe" `
    -Argument "-ExecutionPolicy Bypass -WindowStyle Hidden -File `"$cleanupScriptPath`""
$trigger = New-ScheduledTaskTrigger -Weekly -DaysOfWeek Sunday -At 3:00AM
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
Register-ScheduledTask -TaskName "LabWeeklyCleanup" -Action $action `
    -Trigger $trigger -Settings $settings -RunLevel Highest `
    -User "NT AUTHORITY\SYSTEM" `
    -Description "课题组工作站每周自动清理" | Out-Null

Write-Host "  [OK] 每周日 3:00 自动清理临时文件" -ForegroundColor Green
Write-Host "  [OK] C 盘空间低于 20% 时自动告警并运行 DISM 清理" -ForegroundColor Green
Write-Host "  [OK] 清理日志: D:\GroupData\_公共\_使用手册\cleanup.log" -ForegroundColor Green

Write-Host ""

# ==================== 3. Windows Update 管理 ====================
Write-Host ">> 配置 Windows Update 策略..." -ForegroundColor White

$wuPath = "HKLM:\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU"
if (-not (Test-Path $wuPath)) { New-Item -Path $wuPath -Force | Out-Null }

# 自动下载但通知安装（防止自动重启）
Set-ItemProperty -Path $wuPath -Name "AUOptions" -Value 3 -Type DWord -Force
Write-Host "  [OK] Windows Update: 自动下载，通知安装（不会自动重启）" -ForegroundColor Green

# 不自动重启（已登录用户时）
Set-ItemProperty -Path $wuPath -Name "NoAutoRebootWithLoggedOnUsers" -Value 1 -Type DWord -Force
Write-Host "  [OK] 有用户登录时不自动重启" -ForegroundColor Green

# 计划安装时间：周日 4:00（在清理之后）
Set-ItemProperty -Path $wuPath -Name "ScheduledInstallDay" -Value 0 -Type DWord -Force
Set-ItemProperty -Path $wuPath -Name "ScheduledInstallTime" -Value 4 -Type DWord -Force
Write-Host "  [OK] 更新安装窗口：周日 4:00（需管理员手动确认）" -ForegroundColor Green

Write-Host ""

# ==================== 4. 禁止非管理员安装/卸载程序 ====================
Write-Host ">> 限制标准用户的程序安装权限..." -ForegroundColor White

$installerPath = "HKLM:\SOFTWARE\Policies\Microsoft\Windows\Installer"
if (-not (Test-Path $installerPath)) { New-Item -Path $installerPath -Force | Out-Null }

# 标准用户不能安装需要管理员权限的程序
Set-ItemProperty -Path $installerPath -Name "DisableUserInstalls" -Value 1 -Type DWord -Force
Write-Host "  [OK] 标准用户无法安装需要管理员权限的程序" -ForegroundColor Green

Write-Host ""

# ==================== 5. 磁盘配额提示 ====================
Write-Host ">> 配置数据盘磁盘配额提示..." -ForegroundColor White

# 启用 D 盘磁盘配额（仅记录，不强制限制）
$quotaPath = "HKLM:\SOFTWARE\Policies\Microsoft\Windows NT\DiskQuota"
if (-not (Test-Path $quotaPath)) { New-Item -Path $quotaPath -Force | Out-Null }
Set-ItemProperty -Path $quotaPath -Name "Enable" -Value 1 -Type DWord -Force
Set-ItemProperty -Path $quotaPath -Name "Enforce" -Value 0 -Type DWord -Force  # 仅记录不强制
Write-Host "  [OK] D 盘磁盘配额：已启用记录模式（不强制限制，但可追踪用量）" -ForegroundColor Green

Write-Host ""

# ==================== 6. 资源监控守护程序 ====================
Write-Host ">> 部署资源监控守护程序..." -ForegroundColor White

$monitorSource = Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) "Lab-Monitor.ps1"
$monitorDest   = "C:\Scripts\Lab-Monitor.ps1"

if (Test-Path $monitorSource) {
    # 复制监控脚本到 C:\Scripts\
    Copy-Item -Path $monitorSource -Destination $monitorDest -Force
    Write-Host "  [OK] 监控脚本已复制到 $monitorDest" -ForegroundColor Green

    # 创建开机自启的计划任务（SYSTEM 身份）
    $existingMonitor = Get-ScheduledTask -TaskName "LabResourceMonitor" -ErrorAction SilentlyContinue
    if ($existingMonitor) { Unregister-ScheduledTask -TaskName "LabResourceMonitor" -Confirm:$false }

    $monAction  = New-ScheduledTaskAction -Execute "PowerShell.exe" `
        -Argument "-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$monitorDest`""
    $monTrigger = New-ScheduledTaskTrigger -AtStartup
    $monSettings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
        -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1)
    Register-ScheduledTask -TaskName "LabResourceMonitor" -Action $monAction `
        -Trigger $monTrigger -Settings $monSettings -RunLevel Highest `
        -User "NT AUTHORITY\SYSTEM" `
        -Description "课题组工作站 · 资源监控守护程序（CPU/内存/GPU/磁盘）" | Out-Null

    Write-Host "  [OK] 计划任务 LabResourceMonitor 已创建（开机自启 + SYSTEM 身份）" -ForegroundColor Green
    Write-Host "  [OK] 检查间隔 60 秒，告警冷却 10 分钟" -ForegroundColor Green
    Write-Host "  [OK] 监控日志: D:\GroupData\_公共\_使用手册\system_monitor.log" -ForegroundColor Green

    # 立即启动监控
    Start-ScheduledTask -TaskName "LabResourceMonitor" -ErrorAction SilentlyContinue
    Write-Host "  [OK] 监控服务已启动" -ForegroundColor Green
} else {
    Write-Host "  [警告] 未找到 Lab-Monitor.ps1，跳过监控部署" -ForegroundColor Yellow
    Write-Host "    如需启用，请将 Lab-Monitor.ps1 放在与本脚本相同目录后重新运行" -ForegroundColor Gray
}

Write-Host ""

# ==================== 完成 ====================
Write-Host "========================================" -ForegroundColor Cyan
Write-Host " 系统维护配置完成" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "已配置的策略：" -ForegroundColor White
Write-Host "  1. 远程桌面：断开 2h 注销 / 空闲 4h 断开 / 单会话限制" -ForegroundColor Gray
Write-Host "  2. 每周日 3:00 自动清理临时文件 + C 盘监控" -ForegroundColor Gray
Write-Host "  3. Windows Update：自动下载、通知安装、有人时不重启" -ForegroundColor Gray
Write-Host "  4. 标准用户禁止安装需要管理员权限的程序" -ForegroundColor Gray
Write-Host "  5. D 盘配额记录（可追踪每用户磁盘用量）" -ForegroundColor Gray
Write-Host "  6. 资源监控守护程序（CPU/内存/GPU/磁盘，60秒轮询）" -ForegroundColor Gray
Write-Host ""
Write-Host "提示：以上策略可能需要重启后完全生效。" -ForegroundColor Yellow
Write-Host ""
