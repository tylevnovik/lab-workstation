<#
.SYNOPSIS
    课题组工作站 · 一键初始化总入口
.DESCRIPTION
    按顺序执行全部初始化步骤：
      1. setup_workstation.ps1    → 文件夹结构 + 安全组 + 权限
      2. Setup-Maintenance.ps1    → 系统维护策略 + 资源监控守护程序
      3. Deploy-TrayApp.ps1       → 悬浮导航部署到开机启动
      4. Setup-Desktop.ps1        → 壁纸 + 桌面快捷方式
      5. Setup-Backup.ps1         → 分级备份策略（可选）
    执行完成后工作站即可投入使用。
.NOTES
    使用方法：以管理员身份运行 PowerShell，执行 .\Install-Workstation.ps1
    所有子脚本必须与本脚本在同一目录下。
#>

#Requires -RunAsAdministrator

$ErrorActionPreference = "Stop"
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path

Write-Host ""
Write-Host "╔══════════════════════════════════════════════╗" -ForegroundColor Cyan
Write-Host "║   课题组工作站 · 一键初始化                  ║" -ForegroundColor Cyan
Write-Host "╚══════════════════════════════════════════════╝" -ForegroundColor Cyan
Write-Host ""
Write-Host "本脚本将按顺序执行以下五个步骤：" -ForegroundColor White
Write-Host "  [1/5] 创建文件夹结构和安全组权限" -ForegroundColor Gray
Write-Host "  [2/5] 配置系统维护策略（含资源监控守护程序）" -ForegroundColor Gray
Write-Host "  [3/5] 部署悬浮导航工具" -ForegroundColor Gray
Write-Host "  [4/5] 配置桌面环境（壁纸 + 快捷方式）" -ForegroundColor Gray
Write-Host "  [5/5] 配置分级备份策略（可选）" -ForegroundColor Gray
Write-Host ""

$confirm = Read-Host "确认开始初始化？(Y/n)"
if ($confirm -eq "n" -or $confirm -eq "N") {
    Write-Host "已取消。" -ForegroundColor Yellow
    exit 0
}

# ==================== Step 1 ====================
Write-Host ""
Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Cyan
Write-Host " [1/5] 文件夹结构 + 安全组 + 权限" -ForegroundColor Cyan
Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Cyan
Write-Host ""

$step1 = Join-Path $ScriptDir "setup_workstation.ps1"
if (-not (Test-Path $step1)) {
    Write-Host "[错误] 未找到 setup_workstation.ps1" -ForegroundColor Red
    Write-Host "  请确保所有脚本在同一目录下。" -ForegroundColor Yellow
    exit 1
}
& $step1

if ($LASTEXITCODE -ne 0) {
    Write-Host "[错误] 步骤 1 执行失败，请检查上方输出。" -ForegroundColor Red
    exit 1
}

# ==================== Step 2 ====================
Write-Host ""
Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Cyan
Write-Host " [2/5] 系统维护策略（含资源监控守护程序）" -ForegroundColor Cyan
Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Cyan
Write-Host ""

$step2 = Join-Path $ScriptDir "Setup-Maintenance.ps1"
if (-not (Test-Path $step2)) {
    Write-Host "[错误] 未找到 Setup-Maintenance.ps1" -ForegroundColor Red
    exit 1
}
& $step2

if ($LASTEXITCODE -ne 0) {
    Write-Host "[错误] 步骤 2 执行失败，请检查上方输出。" -ForegroundColor Red
    exit 1
}

# ==================== Step 3 ====================
Write-Host ""
Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Cyan
Write-Host " [3/5] 部署悬浮导航工具" -ForegroundColor Cyan
Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Cyan
Write-Host ""

$step3 = Join-Path $ScriptDir "Deploy-TrayApp.ps1"
if (-not (Test-Path $step3)) {
    Write-Host "[错误] 未找到 Deploy-TrayApp.ps1" -ForegroundColor Red
    exit 1
}
& $step3 -Silent

if ($LASTEXITCODE -ne 0) {
    Write-Host "[错误] 步骤 3 执行失败，请检查上方输出。" -ForegroundColor Red
    exit 1
}

# ==================== Step 4 ====================
Write-Host ""
Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Cyan
Write-Host " [4/5] 桌面环境（壁纸 + 快捷方式）" -ForegroundColor Cyan
Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Cyan
Write-Host ""

$step4 = Join-Path $ScriptDir "Setup-Desktop.ps1"
if (-not (Test-Path $step4)) {
    Write-Host "[警告] 未找到 Setup-Desktop.ps1，跳过桌面配置步骤。" -ForegroundColor Yellow
    Write-Host "  如需配置桌面壁纸和快捷方式，请手动运行 Setup-Desktop.ps1" -ForegroundColor Gray
} else {
    & $step4
    if ($LASTEXITCODE -ne 0) {
        Write-Host "[错误] 步骤 4 执行失败，请检查上方输出。" -ForegroundColor Red
        exit 1
    }
}

# ==================== Step 5 ====================
Write-Host ""
Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Cyan
Write-Host " [5/5] 分级备份策略（可选）" -ForegroundColor Cyan
Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Cyan
Write-Host ""

$step5 = Join-Path $ScriptDir "Setup-Backup.ps1"
if (-not (Test-Path $step5)) {
    Write-Host "[提示] 未找到 Setup-Backup.ps1，跳过备份配置步骤。" -ForegroundColor Yellow
    Write-Host "  如需配置自动备份，请手动运行 Setup-Backup.ps1" -ForegroundColor Gray
} else {
    Write-Host "  备份脚本将引导你配置分级自动备份（审计日志每日/组数据每周）。" -ForegroundColor Gray
    Write-Host "  如果当前没有备份盘（如 E: 盘），可以先跳过，稍后手动运行。" -ForegroundColor Gray
    $backupConfirm = Read-Host "是否现在配置备份？(y/N)"
    if ($backupConfirm -eq "y" -or $backupConfirm -eq "Y") {
        & $step5
        if ($LASTEXITCODE -ne 0) {
            Write-Host "[警告] 步骤 5 执行时出现问题，请稍后手动运行 Setup-Backup.ps1" -ForegroundColor Yellow
        }
    } else {
        Write-Host "  已跳过备份配置。稍后可手动运行 .\Setup-Backup.ps1" -ForegroundColor Gray
    }
}

# ==================== 完成 ====================
Write-Host ""
Write-Host "╔══════════════════════════════════════════════╗" -ForegroundColor Green
Write-Host "║   初始化全部完成！                           ║" -ForegroundColor Green
Write-Host "╚══════════════════════════════════════════════╝" -ForegroundColor Green
Write-Host ""
Write-Host "接下来你需要做的事：" -ForegroundColor White
Write-Host ""
Write-Host "  1. 把管理工具复制到工作站上" -ForegroundColor Yellow
Write-Host "     复制 Manage-LabAccounts.ps1 到 D:\GroupData\_公共\_使用手册\" -ForegroundColor Gray
Write-Host ""
Write-Host "  2. 把使用手册复制到工作站上" -ForegroundColor Yellow
Write-Host "     复制 工作站使用手册.md 到 D:\GroupData\_公共\_使用手册\" -ForegroundColor Gray
Write-Host ""
Write-Host "  3. 创建第一批用户账户" -ForegroundColor Yellow
Write-Host "     运行 Manage-LabAccounts.ps1，在「创建账户」页添加用户" -ForegroundColor Gray
Write-Host ""
Write-Host "  4.（可选）配置备份目标盘" -ForegroundColor Yellow
Write-Host "     如果跳过了步骤 5，稍后运行 .\Setup-Backup.ps1 配置备份" -ForegroundColor Gray
Write-Host ""
Write-Host "  5.（可选）启用 BitLocker 加密数据盘" -ForegroundColor Yellow
Write-Host "     Enable-BitLocker -MountPoint 'D:' -EncryptionMethod Aes256 -RecoveryPasswordProtector" -ForegroundColor Gray
Write-Host ""
Write-Host "  6.（建议）重启一次服务器，让所有策略完全生效" -ForegroundColor Yellow
Write-Host ""
