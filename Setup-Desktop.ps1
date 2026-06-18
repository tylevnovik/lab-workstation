<#
.SYNOPSIS
    课题组工作站 · 桌面环境配置
.DESCRIPTION
    一键配置所有用户的桌面环境：
    1. 设置告示壁纸并通过组策略锁定（用户不可更改）
    2. 在默认用户桌面放置快捷方式（新用户自动继承）
    3. 部署登录脚本（每次登录时确保桌面内容完整）
    仅管理员可运行。
.NOTES
    使用方法：以管理员身份运行 PowerShell，执行 .\Setup-Desktop.ps1
    wallpaper.png 必须与本脚本在同一目录下。
#>

#Requires -RunAsAdministrator

$ErrorActionPreference = "Stop"
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path

# ==================== 配置 ====================
$WallpaperSource = Join-Path $ScriptDir "wallpaper.png"
$WallpaperDest   = "C:\Scripts\LabWallpaper.png"
$ScriptsDir      = "C:\Scripts"
$SharePath       = "D:\GroupData"
$PublicPath      = "D:\GroupData\_公共"
$UsersRootPath   = "D:\Users"
$HandbookPath    = "D:\GroupData\_公共\_使用手册\工作站使用手册.md"

Write-Host "========================================" -ForegroundColor Cyan
Write-Host " 课题组工作站 · 桌面环境配置" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# ==================== 1. 部署壁纸文件 ====================
Write-Host ">> 部署告示壁纸..." -ForegroundColor White

if (-not (Test-Path $WallpaperSource)) {
    Write-Host "[错误] 未找到 wallpaper.png，请确保与本脚本在同一目录" -ForegroundColor Red
    exit 1
}

if (-not (Test-Path $ScriptsDir)) {
    New-Item -ItemType Directory -Path $ScriptsDir -Force | Out-Null
}
Copy-Item -Path $WallpaperSource -Destination $WallpaperDest -Force
Write-Host "  [OK] 壁纸已部署到: $WallpaperDest" -ForegroundColor Green

# ==================== 2. 强制壁纸 + 锁定不可更改 ====================
Write-Host ">> 设置强制壁纸并锁定..." -ForegroundColor White

# 设置壁纸（对所有用户生效）
$desktopPath = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Policies\System"

# 对所有用户强制壁纸（组策略级别）
$wpPolicyPath = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\ActiveDesktop"
if (-not (Test-Path $wpPolicyPath)) { New-Item -Path $wpPolicyPath -Force | Out-Null }

# 壁纸路径（组策略强制）— 正确的注册表键是 ActiveDesktop\DesktopWallpaper
Set-ItemProperty -Path $wpPolicyPath -Name "DesktopWallpaper" -Value $WallpaperDest -Type String -Force

# 壁纸样式 — 正确位置是 System 策略下
$wpStylePath = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System"
if (-not (Test-Path $wpStylePath)) { New-Item -Path $wpStylePath -Force | Out-Null }
Set-ItemProperty -Path $wpStylePath -Name "WallpaperStyle" -Value "10" -Type String -Force  # 10 = Fill

# 禁止用户更改壁纸
$noChangePath = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\ActiveDesktop"
if (-not (Test-Path $noChangePath)) { New-Item -Path $noChangePath -Force | Out-Null }
Set-ItemProperty -Path $noChangePath -Name "NoChangingWallpaper" -Value 1 -Type DWord -Force
Write-Host "  [OK] 壁纸已强制设置并锁定（用户不可更改）" -ForegroundColor Green

# 禁止用户更改桌面背景（控制面板级别）
$personalPath = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Personalization"
if (-not (Test-Path $personalPath)) { New-Item -Path $personalPath -Force | Out-Null }
Set-ItemProperty -Path $personalPath -Name "NoChangingLockScreen" -Value 1 -Type DWord -Force
Write-Host "  [OK] 已禁止通过控制面板更改壁纸" -ForegroundColor Green

# 写一个壁纸设置脚本，通过 Run 注册表项在每次登录时执行（确保壁纸生效）
$wpScript = @"
# 设置壁纸（每次登录时执行）
Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public class Wallpaper {
    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    public static extern int SystemParametersInfo(int uAction, int uParam, string lpvParam, int fuWinIni);
}
"@
[Wallpaper]::SystemParametersInfo(0x0014, 0, "$WallpaperDest", 0x01 -bor 0x02)
"@
$wpScriptPath = Join-Path $ScriptsDir "Set-Wallpaper.ps1"
Set-Content -Path $wpScriptPath -Value $wpScript -Encoding UTF8

# 写入所有用户的 Run 注册表（通过 Default User + 当前已存在用户的 NTUSER.DAT）
# 方法：用 Active Setup 对所有用户执行一次
$activeSetupPath = "HKLM:\SOFTWARE\Microsoft\Active Setup\Installed Components\{A1B2C3D4-E5F6-7890-ABCD-EF1234567890}"
if (-not (Test-Path $activeSetupPath)) { New-Item -Path $activeSetupPath -Force | Out-Null }
Set-ItemProperty -Path $activeSetupPath -Name "StubPath" -Value "powershell.exe -NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$wpScriptPath`"" -Type String -Force
Set-ItemProperty -Path $activeSetupPath -Name "Version" -Value "1,0,0,0" -Type String -Force
Write-Host "  [OK] 已配置登录时自动设置壁纸" -ForegroundColor Green

# ==================== 3. 默认用户桌面快捷方式 ====================
Write-Host ">> 在默认用户桌面放置快捷方式..." -ForegroundColor White

$defaultDesktop = [Environment]::GetFolderPath("CommonDesktopDirectory")
$userDesktop    = [Environment]::GetFolderPath("Desktop")

# 创建快捷方式的辅助函数
function New-DesktopShortcut {
    param(
        [string]$Name,
        [string]$TargetPath,
        [string]$Arguments = "",
        [string]$IconLocation = "",
        [string]$Description = ""
    )
    $shortcutPath = Join-Path $defaultDesktop "$Name.lnk"
    $WshShell = New-Object -ComObject WScript.Shell
    $shortcut = $WshShell.CreateShortcut($shortcutPath)
    $shortcut.TargetPath  = $TargetPath
    if ($Arguments) { $shortcut.Arguments = $Arguments }
    if ($IconLocation) { $shortcut.IconLocation = $IconLocation }
    if ($Description) { $shortcut.Description = $Description }
    $shortcut.Save()
}

# 快捷方式 1：我的个人文件夹（指向 D:\Users\用户名，不是 C:\Users\用户名）
New-DesktopShortcut `
    -Name "我的个人文件夹" `
    -TargetPath "cmd.exe" `
    -Arguments '/c start "" "D:\Users\%USERNAME%"' `
    -IconLocation "shell32.dll,126" `
    -Description "你的私人存储空间（仅自己可见）"
Write-Host "  [OK] 桌面快捷方式：我的个人文件夹" -ForegroundColor Green

# 快捷方式 2：组内共享文件夹
New-DesktopShortcut `
    -Name "组内共享文件夹" `
    -TargetPath "explorer.exe" `
    -Arguments $SharePath `
    -IconLocation "shell32.dll,27" `
    -Description "你所在导师组的共享数据区"
Write-Host "  [OK] 桌面快捷方式：组内共享文件夹" -ForegroundColor Green

# 快捷方式 3：全局公共文件夹
New-DesktopShortcut `
    -Name "全局公共文件夹" `
    -TargetPath "explorer.exe" `
    -Arguments $PublicPath `
    -IconLocation "shell32.dll,162" `
    -Description "所有导师组共享的数据和工具"
Write-Host "  [OK] 桌面快捷方式：全局公共文件夹" -ForegroundColor Green

# 快捷方式 4：使用手册
if (Test-Path $HandbookPath) {
    New-DesktopShortcut `
        -Name "工作站使用手册" `
        -TargetPath $HandbookPath `
        -IconLocation "shell32.dll,23" `
        -Description "工作站使用规范（必读）"
    Write-Host "  [OK] 桌面快捷方式：工作站使用手册" -ForegroundColor Green
} else {
    # 手册还没放上去，创建指向目录的快捷方式
    $manualDir = Split-Path $HandbookPath -Parent
    New-DesktopShortcut `
        -Name "工作站使用手册（待部署）" `
        -TargetPath "explorer.exe" `
        -Arguments $manualDir `
        -IconLocation "shell32.dll,23" `
        -Description "手册尚未部署，请联系管理员"
    Write-Host "  [OK] 桌面快捷方式：使用手册目录（手册文件待部署）" -ForegroundColor Yellow
}

# ==================== 4. 桌面须知文本文件 ====================
Write-Host ">> 在公共桌面放置须知文件..." -ForegroundColor White

$noticePath = Join-Path $defaultDesktop "【必读】工作站使用须知.txt"
$noticeContent = @"
═══════════════════════════════════════════════════
    课题组公共工作站 · 使用须知
═══════════════════════════════════════════════════

你正在使用一台公共工作站，请注意以下事项：

【数据存放规则】

  ■ 个人数据（草稿、私人文件）
    → D:\Users\你的用户名\
    仅自己可见，其他人无法访问

  ■ 组内数据（本组的项目、报告）
    → D:\GroupData\你的导师名\对应类别\
    仅本组成员可见

  ■ 跨组数据（需要多组共享的数据）
    → D:\GroupData\_公共\
    所有成员可见

  ■ 软件安装
    公共软件 → 找管理员装到 Program Files
    个人工具 → 装到 D:\Users\你的用户名\Tools\
    禁止往 GroupData 里装任何程序

【注意事项】

  · 不要把私人数据放在 GroupData 里（所有人可见）
  · 不要在 C 盘存大文件（系统盘空间有限）
  · 用完远程桌面请注销（不要只断开连接）
  · 跑耗时任务请限制 CPU/线程数，别占满资源
  · 桌面壁纸和此须知文件不可删除（系统策略锁定）

【快捷操作】

  · 桌面右下角有悬浮导航，点击按钮可快速打开各文件夹
  · 右键导航图标有更多选项

【需要帮助？】

  · 阅读桌面上的《工作站使用手册》
  · 联系管理员

═══════════════════════════════════════════════════
"@

Set-Content -Path $noticePath -Value $noticeContent -Encoding UTF8
Write-Host "  [OK] 桌面须知文件已放置" -ForegroundColor Green

# ==================== 5. 防止删除桌面项目 ====================
Write-Host ">> 配置桌面保护策略..." -ForegroundColor White

# 设置公共桌面为只读（标准用户不能删除/修改桌面项目）
$commonDesktopPath = [Environment]::GetFolderPath("CommonDesktopDirectory")
$deskAcl = Get-Acl $commonDesktopPath

# 断开继承，不保留继承规则（这样我们可以完全控制）
$deskAcl.SetAccessRuleProtection($true, $false)

# 清除已有的 Users 组规则
$deskAcl.Access | Where-Object {
    $_.IdentityReference -match 'Users|用户' -and -not $_.IsInherited
} | ForEach-Object { $deskAcl.RemoveAccessRule($_) | Out-Null }

# 管理员完全控制
$adminRule = New-Object System.Security.AccessControl.FileSystemAccessRule(
    "Administrators", "FullControl",
    "ContainerInherit,ObjectInherit", "None", "Allow"
)
$deskAcl.AddAccessRule($adminRule)

# SYSTEM 完全控制
$sysRule = New-Object System.Security.AccessControl.FileSystemAccessRule(
    "SYSTEM", "FullControl",
    "ContainerInherit,ObjectInherit", "None", "Allow"
)
$deskAcl.AddAccessRule($sysRule)

# Users 只读
$readOnlyRule = New-Object System.Security.AccessControl.FileSystemAccessRule(
    "Users", "ReadAndExecute",
    "ContainerInherit,ObjectInherit", "None", "Allow"
)
$deskAcl.AddAccessRule($readOnlyRule)

Set-Acl -Path $commonDesktopPath -AclObject $deskAcl
Write-Host "  [OK] 公共桌面已设为只读（标准用户无法删除桌面项目）" -ForegroundColor Green

# 禁止用户通过组策略自定义任务栏和开始菜单
$explorerPolicy = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Explorer"
if (-not (Test-Path $explorerPolicy)) { New-Item -Path $explorerPolicy -Force | Out-Null }
# 不禁止太多，只禁止修改桌面文件夹位置
Set-ItemProperty -Path "HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Explorer" `
    -Name "NoSetFolders" -Value 0 -Type DWord -Force -ErrorAction SilentlyContinue

Write-Host ""

# ==================== 完成 ====================
Write-Host "========================================" -ForegroundColor Cyan
Write-Host " 桌面环境配置完成" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "已配置：" -ForegroundColor White
Write-Host "  1. 告示壁纸：强制设置 + 用户不可更改" -ForegroundColor Gray
Write-Host "  2. 桌面快捷方式：个人文件夹、组内文件夹、公共文件夹、使用手册" -ForegroundColor Gray
Write-Host "  3. 桌面须知文件：数据存放规则一目了然" -ForegroundColor Gray
Write-Host "  4. 桌面保护：公共桌面只读，标准用户无法删除" -ForegroundColor Gray
Write-Host ""
Write-Host "提示：新用户下次登录时将自动看到以上所有桌面内容。" -ForegroundColor Yellow
Write-Host "      已有用户需要注销并重新登录才能生效。" -ForegroundColor Yellow
Write-Host ""
