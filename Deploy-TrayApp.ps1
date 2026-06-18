<#
.SYNOPSIS
    部署托盘导航工具到所有用户的开机启动
.DESCRIPTION
    将 Lab-TrayApp.ps1 部署到 All Users 启动文件夹，
    确保每个用户登录时自动运行托盘导航程序。
    仅管理员可运行。
.PARAMETER Silent
    跳过交互式提示（供 Install-Workstation.ps1 调用时使用）
.NOTES
    使用方法：以管理员身份运行 PowerShell，执行 .\Deploy-TrayApp.ps1
#>

param([switch]$Silent)

#Requires -RunAsAdministrator

$ErrorActionPreference = "Stop"

# ==================== 配置 ====================
$ScriptDir    = Split-Path -Parent $MyInvocation.MyCommand.Path
$TrayAppPath  = Join-Path $ScriptDir "Lab-TrayApp.ps1"
$StartupDir   = [Environment]::GetFolderPath("CommonStartup")
$ShortcutPath = Join-Path $StartupDir "Lab-TrayApp.lnk"

# ==================== 检查 ====================
Write-Host "========================================" -ForegroundColor Cyan
Write-Host " 托盘导航工具部署" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

if (-not (Test-Path $TrayAppPath)) {
    Write-Host "[错误] 未找到 Lab-TrayApp.ps1" -ForegroundColor Red
    Write-Host "  请确保 Deploy-TrayApp.ps1 和 Lab-TrayApp.ps1 在同一目录下。" -ForegroundColor Yellow
    exit 1
}

Write-Host "[信息] 托盘程序: $TrayAppPath" -ForegroundColor Gray
Write-Host "[信息] 启动目录: $StartupDir" -ForegroundColor Gray
Write-Host ""

# ==================== 创建快捷方式 ====================
try {
    # 如果已存在则先删除
    if (Test-Path $ShortcutPath) {
        Remove-Item $ShortcutPath -Force
        Write-Host "[OK] 已移除旧的快捷方式" -ForegroundColor Green
    }

    $WshShell = New-Object -ComObject WScript.Shell
    $shortcut = $WshShell.CreateShortcut($ShortcutPath)
    $shortcut.TargetPath   = "powershell.exe"
    $shortcut.Arguments    = "-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$TrayAppPath`""
    $shortcut.WorkingDirectory = $ScriptDir
    $shortcut.Description  = "课题组工作站 · 悬浮导航工具"
    # 不设置 WindowStyle，完全依赖 -WindowStyle Hidden 参数来隐藏控制台
    $shortcut.Save()

    Write-Host "[OK] 快捷方式已创建: $ShortcutPath" -ForegroundColor Green
    Write-Host ""

    # ==================== 设置执行策略 ====================
    $currentPolicy = Get-ExecutionPolicy -Scope LocalMachine
    if ($currentPolicy -eq "Restricted" -or $currentPolicy -eq "AllSigned") {
        Set-ExecutionPolicy -Scope LocalMachine -ExecutionPolicy RemoteSigned -Force
        Write-Host "[OK] 已将执行策略从 '$currentPolicy' 改为 'RemoteSigned'" -ForegroundColor Green
    } else {
        Write-Host "[跳过] 执行策略已为 '$currentPolicy'，无需修改" -ForegroundColor Yellow
    }
    Write-Host ""

    # ==================== 验证 ====================
    Write-Host "========================================" -ForegroundColor Cyan
    Write-Host " 部署完成" -ForegroundColor Green
    Write-Host "========================================" -ForegroundColor Cyan
    Write-Host ""
    Write-Host "效果：" -ForegroundColor White
    Write-Host "  · 所有用户下次登录时，托盘导航工具将自动启动" -ForegroundColor White
    Write-Host "  · 桌面右下角出现文件夹图标，右键可快速打开各目录" -ForegroundColor White
    Write-Host "  · 首次启动时会弹出气泡提示" -ForegroundColor White
    Write-Host ""
    Write-Host "测试：" -ForegroundColor White
    Write-Host "  · 手动运行一次: powershell -NoProfile -WindowStyle Hidden -File `"$TrayAppPath`"" -ForegroundColor Gray
    Write-Host ""

    $runNow = "Y"
    if (-not $Silent) {
        $runNow = Read-Host "是否现在就启动一次托盘程序测试？(Y/n)"
    }
    if ($runNow -ne "n" -and $runNow -ne "N") {
        Start-Process powershell.exe -ArgumentList "-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$TrayAppPath`""
        Write-Host "[OK] 托盘程序已启动，请检查桌面右下角" -ForegroundColor Green
    }

} catch {
    Write-Host "[错误] 部署失败: $_" -ForegroundColor Red
    exit 1
}
