<#
.SYNOPSIS
    课题组工作站 · 分级备份配置脚本
.DESCRIPTION
    配置工作站数据的分级备份策略：
    1. 审计日志 & 系统配置 -> 每日备份（最重要）
    2. 组内公共数据（GroupData） -> 每周增量备份
    3. 用户个人数据（D:\Users） -> 按需手动备份
    支持自动计划任务 + 手动一键备份。
.NOTES
    使用方法：以管理员身份运行 PowerShell，执行 .\Setup-Backup.ps1
    备份目标：E:\LabBackup\（可在配置区修改）
#>

#Requires -RunAsAdministrator

$ErrorActionPreference = "Stop"

# ===== 配置区 =====
$BackupRoot       = "E:\LabBackup"           # 备份根目录（可改为网络路径如 \\NAS\backup）
$AuditLogSource   = "D:\GroupData\_公共\_使用手册"
$GroupDataSource = "D:\GroupData"
$UserDataSource  = "D:\Users"

# 备份子目录
$AuditBackupDir   = Join-Path $BackupRoot "01_审计日志与配置"
$GroupBackupDir   = Join-Path $BackupRoot "02_组内数据"
$UserBackupDir    = Join-Path $BackupRoot "03_用户数据"
$BackupLogPath    = Join-Path $BackupRoot "backup.log"

# 计划任务名称
$TaskNameAudit = "Lab_Backup_AuditDaily"
$TaskNameGroup = "Lab_Backup_GroupWeekly"

# 辅助脚本路径
$ScriptsDir       = "C:\Scripts"
$BackupTaskScript = Join-Path $ScriptsDir "Lab-BackupTask.ps1"
$BackupNowScript  = Join-Path $ScriptsDir "Lab-BackupNow.ps1"

# ==================== 辅助函数 ====================

function Write-BackupLog {
    <#
    .SYNOPSIS
        写入备份日志文件，带时间戳和级别。
    .PARAMETER Message
        日志内容
    .PARAMETER Level
        日志级别：INFO / WARN / ERROR
    #>
    param(
        [string]$Message,
        [string]$Level = "INFO"
    )
    $ts = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
    $line = "[$ts] [$Level] $Message"

    # 确保日志目录存在
    $logDir = Split-Path $BackupLogPath -Parent
    if (-not (Test-Path $logDir)) {
        New-Item -ItemType Directory -Path $logDir -Force | Out-Null
    }

    try {
        Add-Content -Path $BackupLogPath -Value $line -Encoding UTF8
    } catch {
        # 日志写入失败不应阻塞主流程
        Write-Host "  [警告] 备份日志写入失败: $_" -ForegroundColor Yellow
    }

    # 同时输出到控制台
    $color = switch ($Level) {
        "ERROR" { "Red" }
        "WARN"  { "Yellow" }
        default { "Gray" }
    }
    Write-Host "  $line" -ForegroundColor $color
}

function Get-FolderSizeMB {
    <#
    .SYNOPSIS
        获取目录大小（MB），目录不存在返回 0。
    #>
    param([string]$Path)
    if (-not (Test-Path $Path)) { return 0 }
    try {
        $size = (Get-ChildItem -Path $Path -Recurse -Force -ErrorAction SilentlyContinue |
                 Measure-Object -Property Length -Sum).Sum
        return [math]::Round($size / 1MB, 1)
    } catch {
        return 0
    }
}

function Format-FileSize {
    <#
    .SYNOPSIS
        将字节数格式化为可读字符串。
    #>
    param([double]$Bytes)
    if ($Bytes -ge 1GB) { return "{0:N1} GB" -f ($Bytes / 1GB) }
    if ($Bytes -ge 1MB) { return "{0:N1} MB" -f ($Bytes / 1MB) }
    if ($Bytes -ge 1KB) { return "{0:N1} KB" -f ($Bytes / 1KB) }
    return "$Bytes B"
}

# ==================== 备份功能函数 ====================

function Initialize-BackupDirs {
    <#
    .SYNOPSIS
        创建所有备份目录，并验证备份根目录可写。
    #>
    Write-Host ">> 初始化备份目录..." -ForegroundColor White

    # 检查备份根目录所在驱动器是否存在
    $backupDrive = (Split-Path $BackupRoot -Qualifier) -replace ':', ''
    $drive = Get-PSDrive -Name $backupDrive -ErrorAction SilentlyContinue
    if (-not $drive) {
        Write-Host "  [错误] 备份目标驱动器 ${backupDrive}: 不存在" -ForegroundColor Red
        Write-Host "  请检查备份路径配置，或插入/挂载备份磁盘。" -ForegroundColor Yellow
        return $false
    }

    # 创建各子目录
    $dirs = @($BackupRoot, $AuditBackupDir, $GroupBackupDir, $UserBackupDir)
    foreach ($dir in $dirs) {
        if (-not (Test-Path $dir)) {
            try {
                New-Item -ItemType Directory -Path $dir -Force | Out-Null
                Write-Host "  [OK] 创建目录: $dir" -ForegroundColor Green
            } catch {
                Write-Host "  [错误] 无法创建目录 ${dir}: $_" -ForegroundColor Red
                return $false
            }
        } else {
            Write-Host "  [跳过] 目录已存在: $dir" -ForegroundColor Yellow
        }
    }

    # 验证可写
    $testFile = Join-Path $BackupRoot ".write_test"
    try {
        Set-Content -Path $testFile -Value "test" -Force
        Remove-Item -Path $testFile -Force
        Write-Host "  [OK] 备份目录可写" -ForegroundColor Green
    } catch {
        Write-Host "  [错误] 备份目录不可写: $BackupRoot" -ForegroundColor Red
        Write-Host "  请检查磁盘权限或以管理员身份运行。" -ForegroundColor Yellow
        return $false
    }

    Write-BackupLog "备份目录初始化完成: $BackupRoot"
    return $true
}

function Backup-AuditLogs {
    <#
    .SYNOPSIS
        备份审计日志和系统配置文件（最高优先级）。
        使用 robocopy /MIR 镜像模式，确保备份与源完全一致。
    #>
    Write-Host ""
    Write-Host ">> 备份审计日志与配置..." -ForegroundColor White

    if (-not (Test-Path $AuditLogSource)) {
        Write-Host "  [警告] 审计日志源目录不存在: $AuditLogSource" -ForegroundColor Yellow
        Write-BackupLog "审计日志源目录不存在，跳过: $AuditLogSource" "WARN"
        return
    }

    $timestamp = Get-Date -Format "yyyyMMdd_HHmmss"
    $destDir = Join-Path $AuditBackupDir $timestamp

    # 使用 robocopy 镜像审计日志目录
    # /MIR = 镜像（含删除已不存在的文件）
    # /R:3 /W:5 = 失败重试 3 次，每次间隔 5 秒
    # /NP /NFL /NDL = 不显示进度/文件列表/目录列表（安静模式）
    # /LOG = 输出到日志
    $robocopyLog = Join-Path $BackupRoot "robocopy_audit_$timestamp.log"
    $robocopyArgs = @(
        "`"$AuditLogSource`""
        "`"$destDir`""
        "/MIR"
        "/R:3", "/W:5"
        "/NP", "/NFL", "/NDL"
        "/LOG:`"$robocopyLog`""
    )

    $process = Start-Process -FilePath "robocopy.exe" -ArgumentList $robocopyArgs `
        -Wait -NoNewWindow -PassThru

    # robocopy 退出码：0-7 为成功，8+ 为错误
    if ($process.ExitCode -le 7) {
        $backedUpSize = Get-FolderSizeMB $destDir
        Write-Host "  [OK] 审计日志备份完成: ${backedUpSize} MB -> $destDir" -ForegroundColor Green
        Write-BackupLog "审计日志备份成功: ${backedUpSize} MB -> $destDir"
    } else {
        Write-Host "  [错误] 审计日志备份失败 (robocopy 退出码: $($process.ExitCode))" -ForegroundColor Red
        Write-BackupLog "审计日志备份失败 (退出码: $($process.ExitCode))" "ERROR"
    }

    # 清理临时 robocopy 日志
    if (Test-Path $robocopyLog) { Remove-Item $robocopyLog -Force -ErrorAction SilentlyContinue }

    # 同时备份通知系统数据（JSON 文件等）
    $notifSource = "D:\GroupData\_公共\_notifications"
    if (Test-Path $notifSource) {
        $notifDest = Join-Path $AuditBackupDir "${timestamp}_notifications"
        $notifArgs = @(
            "`"$notifSource`""
            "`"$notifDest`""
            "/MIR"
            "/R:3", "/W:5"
            "/NP", "/NFL", "/NDL"
        )
        $notifProcess = Start-Process -FilePath "robocopy.exe" -ArgumentList $notifArgs `
            -Wait -NoNewWindow -PassThru
        if ($notifProcess.ExitCode -le 7) {
            Write-Host "  [OK] 通知数据备份完成" -ForegroundColor Green
            Write-BackupLog "通知数据备份成功 -> $notifDest"
        } else {
            Write-Host "  [警告] 通知数据备份异常 (退出码: $($notifProcess.ExitCode))" -ForegroundColor Yellow
            Write-BackupLog "通知数据备份异常 (退出码: $($notifProcess.ExitCode))" "WARN"
        }
    }

    # 清理旧备份：保留最近 30 天的审计日志备份
    $cutoff = (Get-Date).AddDays(-30)
    Get-ChildItem -Path $AuditBackupDir -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.CreationTime -lt $cutoff } |
        ForEach-Object {
            Remove-Item -Path $_.FullName -Recurse -Force -ErrorAction SilentlyContinue
            Write-BackupLog "清理过期审计备份: $($_.Name)"
        }
}

function Backup-GroupData {
    <#
    .SYNOPSIS
        增量备份组内公共数据（GroupData）。
        使用 robocopy /E /XO（复制子目录 + 排除更旧文件），实现增量备份。
        排除 _公共\_notifications\pending（临时队列数据，无需备份）。
    #>
    Write-Host ""
    Write-Host ">> 备份组内公共数据..." -ForegroundColor White

    if (-not (Test-Path $GroupDataSource)) {
        Write-Host "  [警告] 组数据源目录不存在: $GroupDataSource" -ForegroundColor Yellow
        Write-BackupLog "组数据源目录不存在，跳过: $GroupDataSource" "WARN"
        return
    }

    $timestamp = Get-Date -Format "yyyyMMdd_HHmmss"

    # 使用 robocopy /E（含子目录） /XO（排除旧文件 = 增量备份）
    # 排除临时通知队列目录
    $robocopyLog = Join-Path $BackupRoot "robocopy_group_$timestamp.log"
    $robocopyArgs = @(
        "`"$GroupDataSource`""
        "`"$GroupBackupDir`""
        "/E"
        "/XO"
        "/R:3", "/W:5"
        "/NP", "/NFL", "/NDL"
        "/XD", "`"_公共\_notifications\pending`""
        "/LOG:`"$robocopyLog`""
    )

    $process = Start-Process -FilePath "robocopy.exe" -ArgumentList $robocopyArgs `
        -Wait -NoNewWindow -PassThru

    if ($process.ExitCode -le 7) {
        $backedUpSize = Get-FolderSizeMB $GroupBackupDir
        Write-Host "  [OK] 组数据备份完成: 备份目录总计 ${backedUpSize} MB" -ForegroundColor Green
        Write-BackupLog "组数据增量备份成功: 备份目录总计 ${backedUpSize} MB"
    } else {
        Write-Host "  [错误] 组数据备份失败 (robocopy 退出码: $($process.ExitCode))" -ForegroundColor Red
        Write-BackupLog "组数据备份失败 (退出码: $($process.ExitCode))" "ERROR"
    }

    # 清理临时 robocopy 日志
    if (Test-Path $robocopyLog) { Remove-Item $robocopyLog -Force -ErrorAction SilentlyContinue }
}

function Backup-UserData {
    <#
    .SYNOPSIS
        备份用户个人数据（D:\Users）。
        可指定单个用户或备份全部用户目录。
    .PARAMETER Username
        指定用户名则仅备份该用户；留空则备份所有用户。
    #>
    param(
        [string]$Username = ""
    )

    Write-Host ""
    Write-Host ">> 备份用户个人数据..." -ForegroundColor White

    if (-not (Test-Path $UserDataSource)) {
        Write-Host "  [警告] 用户数据目录不存在: $UserDataSource" -ForegroundColor Yellow
        Write-BackupLog "用户数据目录不存在，跳过: $UserDataSource" "WARN"
        return
    }

    if ($Username) {
        # 备份单个用户
        $source = Join-Path $UserDataSource $Username
        if (-not (Test-Path $source)) {
            Write-Host "  [错误] 用户目录不存在: $source" -ForegroundColor Red
            Write-BackupLog "用户目录不存在: $source" "ERROR"
            return
        }
        $dest = Join-Path $UserBackupDir $Username
        $label = "用户 $Username"
    } else {
        # 备份全部用户
        $source = $UserDataSource
        $dest = $UserBackupDir
        $label = "所有用户"
    }

    $timestamp = Get-Date -Format "yyyyMMdd_HHmmss"

    # 使用 robocopy /E /XO 增量备份
    $robocopyLog = Join-Path $BackupRoot "robocopy_user_$timestamp.log"
    $robocopyArgs = @(
        "`"$source`""
        "`"$dest`""
        "/E"
        "/XO"
        "/R:3", "/W:5"
        "/NP", "/NFL", "/NDL"
        "/LOG:`"$robocopyLog`""
    )

    $process = Start-Process -FilePath "robocopy.exe" -ArgumentList $robocopyArgs `
        -Wait -NoNewWindow -PassThru

    if ($process.ExitCode -le 7) {
        $backedUpSize = Get-FolderSizeMB $dest
        Write-Host "  [OK] ${label} 数据备份完成: ${backedUpSize} MB" -ForegroundColor Green
        Write-BackupLog "${label} 数据备份成功: ${backedUpSize} MB"
    } else {
        Write-Host "  [错误] ${label} 数据备份失败 (robocopy 退出码: $($process.ExitCode))" -ForegroundColor Red
        Write-BackupLog "${label} 数据备份失败 (退出码: $($process.ExitCode))" "ERROR"
    }

    # 清理临时 robocopy 日志
    if (Test-Path $robocopyLog) { Remove-Item $robocopyLog -Force -ErrorAction SilentlyContinue }
}

function Backup-All {
    <#
    .SYNOPSIS
        手动全量备份：依次执行所有备份层级。
    .PARAMETER IncludeUserData
        是否包含用户个人数据（数据量可能较大）。
    #>
    param(
        [switch]$IncludeUserData
    )

    $startTime = Get-Date
    Write-Host ""
    Write-Host "========================================" -ForegroundColor Cyan
    Write-Host " 开始全量备份" -ForegroundColor Cyan
    Write-Host "========================================" -ForegroundColor Cyan

    Write-BackupLog "========== 开始手动全量备份 =========="

    # 第一层：审计日志（最重要）
    Write-Host ""
    Write-Host "  [1/3] 审计日志与配置..." -ForegroundColor White
    Backup-AuditLogs

    # 第二层：组内数据
    Write-Host ""
    Write-Host "  [2/3] 组内公共数据..." -ForegroundColor White
    Backup-GroupData

    # 第三层：用户数据（可选）
    if ($IncludeUserData) {
        Write-Host ""
        Write-Host "  [3/3] 用户个人数据..." -ForegroundColor White
        Backup-UserData
    } else {
        Write-Host ""
        Write-Host "  [3/3] 用户个人数据 - 已跳过（未指定 -IncludeUserData）" -ForegroundColor Yellow
    }

    # 生成汇总
    $elapsed = (Get-Date) - $startTime
    $elapsedStr = "{0:mm\:ss}" -f $elapsed

    Write-Host ""
    Write-Host "========================================" -ForegroundColor Cyan
    Write-Host " 备份完成 - 耗时 $elapsedStr" -ForegroundColor Green
    Write-Host "========================================" -ForegroundColor Cyan

    # 各层备份大小汇总
    $auditSize = Get-FolderSizeMB $AuditBackupDir
    $groupSize = Get-FolderSizeMB $GroupBackupDir
    $userSize  = Get-FolderSizeMB $UserBackupDir
    $totalSize = $auditSize + $groupSize + $userSize

    Write-Host ""
    Write-Host "  备份汇总：" -ForegroundColor White
    Write-Host "    审计日志与配置 : $auditSize MB" -ForegroundColor Gray
    Write-Host "    组内公共数据   : $groupSize MB" -ForegroundColor Gray
    Write-Host "    用户个人数据   : $userSize MB" -ForegroundColor Gray
    Write-Host "    ─────────────────────────" -ForegroundColor Gray
    Write-Host "    总计           : $totalSize MB" -ForegroundColor White

    Write-BackupLog "全量备份完成，耗时 ${elapsedStr}，总计 ${totalSize} MB"

    # 备份驱动器剩余空间
    $backupDrive = (Split-Path $BackupRoot -Qualifier) -replace ':', ''
    $drive = Get-PSDrive -Name $backupDrive -ErrorAction SilentlyContinue
    if ($drive) {
        $freeGB = [math]::Round($drive.Free / 1GB, 1)
        Write-Host ""
        Write-Host "  备份盘剩余空间: ${freeGB} GB" -ForegroundColor Gray
    }
}

function Get-BackupStatus {
    <#
    .SYNOPSIS
        显示备份状态：各层最后备份时间、备份大小、磁盘空间和错误日志。
    #>
    Write-Host ""
    Write-Host "========================================" -ForegroundColor Cyan
    Write-Host " 备份状态概览" -ForegroundColor Cyan
    Write-Host "========================================" -ForegroundColor Cyan
    Write-Host ""

    # 各层最后备份时间（通过目录时间戳判断）
    Write-Host "  [各层备份状态]" -ForegroundColor White

    # 审计日志：按子目录创建时间排序
    $auditDirs = Get-ChildItem -Path $AuditBackupDir -Directory -ErrorAction SilentlyContinue |
        Sort-Object CreationTime -Descending | Select-Object -First 1
    if ($auditDirs) {
        $lastAudit = $auditDirs.CreationTime.ToString("yyyy-MM-dd HH:mm")
        $auditSize = Get-FolderSizeMB $AuditBackupDir
        Write-Host "    审计日志 : 最后备份 $lastAudit | 总计 $auditSize MB" -ForegroundColor Green
    } else {
        Write-Host "    审计日志 : 尚未备份" -ForegroundColor Yellow
    }

    # 组内数据
    $groupItems = Get-ChildItem -Path $GroupBackupDir -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($groupItems) {
        $lastGroup = $groupItems.LastWriteTime.ToString("yyyy-MM-dd HH:mm")
        $groupSize = Get-FolderSizeMB $GroupBackupDir
        Write-Host "    组内数据 : 最后更新 $lastGroup | 总计 $groupSize MB" -ForegroundColor Green
    } else {
        Write-Host "    组内数据 : 尚未备份" -ForegroundColor Yellow
    }

    # 用户数据
    $userItems = Get-ChildItem -Path $UserBackupDir -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($userItems) {
        $lastUser = $userItems.LastWriteTime.ToString("yyyy-MM-dd HH:mm")
        $userSize = Get-FolderSizeMB $UserBackupDir
        Write-Host "    用户数据 : 最后更新 $lastUser | 总计 $userSize MB" -ForegroundColor Green
    } else {
        Write-Host "    用户数据 : 尚未备份" -ForegroundColor Yellow
    }

    # 备份盘空间
    Write-Host ""
    Write-Host "  [磁盘空间]" -ForegroundColor White
    $backupDrive = (Split-Path $BackupRoot -Qualifier) -replace ':', ''
    $drive = Get-PSDrive -Name $backupDrive -ErrorAction SilentlyContinue
    if ($drive) {
        $freeGB  = [math]::Round($drive.Free / 1GB, 1)
        $totalGB = [math]::Round(($drive.Used + $drive.Free) / 1GB, 1)
        $pct     = [math]::Round($drive.Free / ($drive.Used + $drive.Free) * 100, 0)
        $spaceColor = if ($pct -lt 20) { "Red" } elseif ($pct -lt 40) { "Yellow" } else { "Green" }
        Write-Host "    备份盘 (${backupDrive}:) : ${freeGB} GB / ${totalGB} GB (剩余 ${pct}%)" -ForegroundColor $spaceColor
    } else {
        Write-Host "    备份盘 (${backupDrive}:) : 未挂载或不可用" -ForegroundColor Red
    }

    # 计划任务状态
    Write-Host ""
    Write-Host "  [计划任务]" -ForegroundColor White
    $tasks = @($TaskNameAudit, $TaskNameGroup)
    foreach ($tName in $tasks) {
        $task = Get-ScheduledTask -TaskName $tName -ErrorAction SilentlyContinue
        if ($task) {
            $state = $task.State.ToString()
            $stateColor = if ($state -eq "Ready") { "Green" } else { "Yellow" }
            $lastRun = (Get-ScheduledTaskInfo -TaskName $tName -ErrorAction SilentlyContinue).LastRunTime
            $lastRunStr = if ($lastRun) { $lastRun.ToString("yyyy-MM-dd HH:mm") } else { "从未运行" }
            Write-Host "    $tName : $state (上次运行: $lastRunStr)" -ForegroundColor $stateColor
        } else {
            Write-Host "    $tName : 未创建" -ForegroundColor Yellow
        }
    }

    # 最近的错误日志
    Write-Host ""
    Write-Host "  [最近错误记录]" -ForegroundColor White
    if (Test-Path $BackupLogPath) {
        $errors = Get-Content -Path $BackupLogPath -Tail 50 -Encoding UTF8 -ErrorAction SilentlyContinue |
            Where-Object { $_ -match '\[ERROR\]' } |
            Select-Object -Last 5
        if ($errors) {
            foreach ($err in $errors) {
                Write-Host "    $err" -ForegroundColor Red
            }
        } else {
            Write-Host "    无错误记录" -ForegroundColor Green
        }
    } else {
        Write-Host "    备份日志不存在（尚未执行过备份）" -ForegroundColor Yellow
    }

    Write-Host ""
}

# ==================== 创建计划任务辅助脚本 ====================

function New-BackupTaskScript {
    <#
    .SYNOPSIS
        生成计划任务调用的备份执行脚本 C:\Scripts\Lab-BackupTask.ps1。
        该脚本接受 -Tier 参数，执行对应层级的备份。
    #>
    if (-not (Test-Path $ScriptsDir)) {
        New-Item -ItemType Directory -Path $ScriptsDir -Force | Out-Null
    }

    $taskScriptContent = @'
<#
.SYNOPSIS
    课题组工作站 · 备份任务执行脚本（计划任务调用）
.DESCRIPTION
    由 Windows 计划任务自动调用，根据 -Tier 参数执行对应层级的备份。
    请勿手动修改此文件，如需重新配置请运行 Setup-Backup.ps1。
#>

param(
    [Parameter(Mandatory=$true)]
    [ValidateSet("audit", "group", "user", "all")]
    [string]$Tier
)

$ErrorActionPreference = "Stop"

# ===== 配置（与 Setup-Backup.ps1 保持一致） =====
$BackupRoot       = "BACKUP_ROOT_PLACEHOLDER"
$AuditLogSource   = "D:\GroupData\_公共\_使用手册"
$GroupDataSource = "D:\GroupData"
$UserDataSource  = "D:\Users"
$AuditBackupDir   = Join-Path $BackupRoot "01_审计日志与配置"
$GroupBackupDir   = Join-Path $BackupRoot "02_组内数据"
$UserBackupDir    = Join-Path $BackupRoot "03_用户数据"
$BackupLogPath    = Join-Path $BackupRoot "backup.log"

function Write-BackupLog {
    param([string]$Message, [string]$Level = "INFO")
    $ts = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
    $line = "[$ts] [$Level] $Message"
    $logDir = Split-Path $BackupLogPath -Parent
    if (-not (Test-Path $logDir)) { New-Item -ItemType Directory -Path $logDir -Force | Out-Null }
    try { Add-Content -Path $BackupLogPath -Value $line -Encoding UTF8 } catch { }
}

function Get-FolderSizeMB {
    param([string]$Path)
    if (-not (Test-Path $Path)) { return 0 }
    try {
        $size = (Get-ChildItem -Path $Path -Recurse -Force -ErrorAction SilentlyContinue |
                 Measure-Object -Property Length -Sum).Sum
        return [math]::Round($size / 1MB, 1)
    } catch { return 0 }
}

# ===== 审计日志备份 =====
function Do-BackupAudit {
    Write-BackupLog "===== 计划任务：审计日志备份开始 ====="
    if (-not (Test-Path $AuditLogSource)) {
        Write-BackupLog "审计日志源目录不存在: $AuditLogSource" "WARN"
        return
    }
    $timestamp = Get-Date -Format "yyyyMMdd_HHmmss"
    $destDir = Join-Path $AuditBackupDir $timestamp
    $rcArgs = @("`"$AuditLogSource`"", "`"$destDir`"", "/MIR", "/R:3", "/W:5", "/NP", "/NFL", "/NDL")
    $proc = Start-Process -FilePath "robocopy.exe" -ArgumentList $rcArgs -Wait -NoNewWindow -PassThru
    if ($proc.ExitCode -le 7) {
        $sz = Get-FolderSizeMB $destDir
        Write-BackupLog "审计日志备份成功: ${sz} MB -> $destDir"
    } else {
        Write-BackupLog "审计日志备份失败 (退出码: $($proc.ExitCode))" "ERROR"
    }
    # 通知数据
    $notifSource = "D:\GroupData\_公共\_notifications"
    if (Test-Path $notifSource) {
        $notifDest = Join-Path $AuditBackupDir "${timestamp}_notifications"
        $nArgs = @("`"$notifSource`"", "`"$notifDest`"", "/MIR", "/R:3", "/W:5", "/NP", "/NFL", "/NDL")
        $nProc = Start-Process -FilePath "robocopy.exe" -ArgumentList $nArgs -Wait -NoNewWindow -PassThru
        if ($nProc.ExitCode -le 7) { Write-BackupLog "通知数据备份成功" }
        else { Write-BackupLog "通知数据备份异常 (退出码: $($nProc.ExitCode))" "WARN" }
    }
    # 清理 30 天前旧备份
    $cutoff = (Get-Date).AddDays(-30)
    Get-ChildItem -Path $AuditBackupDir -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.CreationTime -lt $cutoff } |
        ForEach-Object {
            Remove-Item -Path $_.FullName -Recurse -Force -ErrorAction SilentlyContinue
            Write-BackupLog "清理过期审计备份: $($_.Name)"
        }
}

# ===== 组数据备份 =====
function Do-BackupGroup {
    Write-BackupLog "===== 计划任务：组数据备份开始 ====="
    if (-not (Test-Path $GroupDataSource)) {
        Write-BackupLog "组数据源目录不存在: $GroupDataSource" "WARN"
        return
    }
    $rcArgs = @("`"$GroupDataSource`"", "`"$GroupBackupDir`"", "/E", "/XO", "/R:3", "/W:5", "/NP", "/NFL", "/NDL", "/XD", "`"_公共\_notifications\pending`"")
    $proc = Start-Process -FilePath "robocopy.exe" -ArgumentList $rcArgs -Wait -NoNewWindow -PassThru
    if ($proc.ExitCode -le 7) {
        $sz = Get-FolderSizeMB $GroupBackupDir
        Write-BackupLog "组数据增量备份成功: 备份目录总计 ${sz} MB"
    } else {
        Write-BackupLog "组数据备份失败 (退出码: $($proc.ExitCode))" "ERROR"
    }
}

# ===== 用户数据备份 =====
function Do-BackupUser {
    Write-BackupLog "===== 计划任务：用户数据备份开始 ====="
    if (-not (Test-Path $UserDataSource)) {
        Write-BackupLog "用户数据目录不存在: $UserDataSource" "WARN"
        return
    }
    $rcArgs = @("`"$UserDataSource`"", "`"$UserBackupDir`"", "/E", "/XO", "/R:3", "/W:5", "/NP", "/NFL", "/NDL")
    $proc = Start-Process -FilePath "robocopy.exe" -ArgumentList $rcArgs -Wait -NoNewWindow -PassThru
    if ($proc.ExitCode -le 7) {
        $sz = Get-FolderSizeMB $UserBackupDir
        Write-BackupLog "用户数据备份成功: ${sz} MB"
    } else {
        Write-BackupLog "用户数据备份失败 (退出码: $($proc.ExitCode))" "ERROR"
    }
}

# ===== 执行 =====
try {
    switch ($Tier) {
        "audit" { Do-BackupAudit }
        "group" { Do-BackupGroup }
        "user"  { Do-BackupUser }
        "all"   { Do-BackupAudit; Do-BackupGroup; Do-BackupUser }
    }
} catch {
    Write-BackupLog "备份脚本异常: $_" "ERROR"
}
'@

    # 替换备份根目录占位符
    $taskScriptContent = $taskScriptContent -replace 'BACKUP_ROOT_PLACEHOLDER', $BackupRoot

    Set-Content -Path $BackupTaskScript -Value $taskScriptContent -Encoding UTF8
    Write-Host "  [OK] 备份任务脚本: $BackupTaskScript" -ForegroundColor Green
}

function New-BackupNowScript {
    <#
    .SYNOPSIS
        生成管理员手动备份快捷脚本 C:\Scripts\Lab-BackupNow.ps1。
        管理员可直接运行此脚本进行手动备份。
    #>
    if (-not (Test-Path $ScriptsDir)) {
        New-Item -ItemType Directory -Path $ScriptsDir -Force | Out-Null
    }

    $nowScriptContent = @'
<#
.SYNOPSIS
    课题组工作站 · 手动一键备份
.DESCRIPTION
    管理员手动触发备份。可选择备份层级。
    用法：
      .\Lab-BackupNow.ps1              # 备份审计日志 + 组数据
      .\Lab-BackupNow.ps1 -Tier audit  # 仅备份审计日志
      .\Lab-BackupNow.ps1 -Tier all    # 全量备份（含用户数据）
#>

param(
    [ValidateSet("audit", "group", "user", "all")]
    [string]$Tier = ""
)

$taskScript = "C:\Scripts\Lab-BackupTask.ps1"
if (-not (Test-Path $taskScript)) {
    Write-Host "[错误] 备份任务脚本不存在，请先运行 Setup-Backup.ps1 配置备份。" -ForegroundColor Red
    exit 1
}

if (-not $Tier) {
    Write-Host ""
    Write-Host "课题组工作站 · 手动备份" -ForegroundColor Cyan
    Write-Host "  [1] 审计日志备份（每日自动，手动补一次）" -ForegroundColor White
    Write-Host "  [2] 组数据备份（每周自动，手动补一次）" -ForegroundColor White
    Write-Host "  [3] 用户数据备份（仅手动）" -ForegroundColor White
    Write-Host "  [4] 全量备份（全部层级）" -ForegroundColor White
    Write-Host ""
    $choice = Read-Host "请选择备份层级 (1-4)"
    $Tier = switch ($choice) {
        "1" { "audit" }
        "2" { "group" }
        "3" { "user" }
        "4" { "all" }
        default { "audit" }
    }
}

Write-Host ""
Write-Host "正在执行 $Tier 层级备份..." -ForegroundColor Cyan
Write-Host ""
& $taskScript -Tier $Tier
Write-Host ""
Write-Host "备份完成。日志: BACKUP_ROOT_PLACEHOLDER\backup.log" -ForegroundColor Green
'@

    $nowScriptContent = $nowScriptContent -replace 'BACKUP_ROOT_PLACEHOLDER', $BackupRoot

    Set-Content -Path $BackupNowScript -Value $nowScriptContent -Encoding UTF8
    Write-Host "  [OK] 手动备份脚本: $BackupNowScript" -ForegroundColor Green
}

# ==================== 创建计划任务 ====================

function New-BackupScheduledTasks {
    <#
    .SYNOPSIS
        创建备份计划任务：
        - Lab_Backup_AuditDaily: 每日凌晨 2:00 备份审计日志
        - Lab_Backup_GroupWeekly: 每周日凌晨 3:00 备份组数据
        用户数据备份仅手动执行（数据量大，不适合自动定时）。
    #>
    Write-Host ""
    Write-Host ">> 创建备份计划任务..." -ForegroundColor White

    # ----- 任务 1：每日审计日志备份 -----
    $existingAudit = Get-ScheduledTask -TaskName $TaskNameAudit -ErrorAction SilentlyContinue
    if ($existingAudit) {
        Unregister-ScheduledTask -TaskName $TaskNameAudit -Confirm:$false
    }

    $auditAction = New-ScheduledTaskAction -Execute "powershell.exe" `
        -Argument "-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -Command `"& { . '$BackupTaskScript' -Tier audit }`""
    $auditTrigger = New-ScheduledTaskTrigger -Daily -At 2:00AM
    $auditPrincipal = New-ScheduledTaskPrincipal -UserId "NT AUTHORITY\SYSTEM" -RunLevel Highest
    $auditSettings = New-ScheduledTaskSettingsSet `
        -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
        -StartWhenAvailable -ExecutionTimeLimit (New-TimeSpan -Hours 1)

    Register-ScheduledTask `
        -TaskName $TaskNameAudit `
        -Action $auditAction `
        -Trigger $auditTrigger `
        -Principal $auditPrincipal `
        -Settings $auditSettings `
        -Description "课题组工作站 · 每日审计日志备份（凌晨 2:00）" `
        -Force | Out-Null

    Write-Host "  [OK] $TaskNameAudit - 每日 2:00 备份审计日志" -ForegroundColor Green

    # ----- 任务 2：每周组数据备份 -----
    $existingGroup = Get-ScheduledTask -TaskName $TaskNameGroup -ErrorAction SilentlyContinue
    if ($existingGroup) {
        Unregister-ScheduledTask -TaskName $TaskNameGroup -Confirm:$false
    }

    $groupAction = New-ScheduledTaskAction -Execute "powershell.exe" `
        -Argument "-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -Command `"& { . '$BackupTaskScript' -Tier group }`""
    $groupTrigger = New-ScheduledTaskTrigger -Weekly -DaysOfWeek Sunday -At 3:00AM
    $groupPrincipal = New-ScheduledTaskPrincipal -UserId "NT AUTHORITY\SYSTEM" -RunLevel Highest
    $groupSettings = New-ScheduledTaskSettingsSet `
        -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
        -StartWhenAvailable -ExecutionTimeLimit (New-TimeSpan -Hours 4)

    Register-ScheduledTask `
        -TaskName $TaskNameGroup `
        -Action $groupAction `
        -Trigger $groupTrigger `
        -Principal $groupPrincipal `
        -Settings $groupSettings `
        -Description "课题组工作站 · 每周日组数据增量备份（凌晨 3:00）" `
        -Force | Out-Null

    Write-Host "  [OK] $TaskNameGroup - 每周日 3:00 备份组数据" -ForegroundColor Green

    Write-Host ""
    Write-Host "  [提示] 用户数据备份不设置自动任务（数据量大），请定期手动运行:" -ForegroundColor Yellow
    Write-Host "         C:\Scripts\Lab-BackupNow.ps1 -Tier user" -ForegroundColor Gray
}

# ==================== 主执行流程 ====================

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host " 课题组工作站 · 分级备份配置" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# 显示当前配置
Write-Host "当前备份配置：" -ForegroundColor White
Write-Host "  备份目标目录   : $BackupRoot" -ForegroundColor Gray
Write-Host "  审计日志源     : $AuditLogSource" -ForegroundColor Gray
Write-Host "  组数据源       : $GroupDataSource" -ForegroundColor Gray
Write-Host "  用户数据源     : $UserDataSource" -ForegroundColor Gray
Write-Host ""
Write-Host "备份策略：" -ForegroundColor White
Write-Host "  [第一层] 审计日志与配置 -> 每日 2:00 自动备份（镜像模式）" -ForegroundColor Gray
Write-Host "  [第二层] 组内公共数据 -> 每周日 3:00 自动备份（增量模式）" -ForegroundColor Gray
Write-Host "  [第三层] 用户个人数据 -> 仅手动备份" -ForegroundColor Gray
Write-Host ""

# 确认或修改备份路径
$confirmPath = Read-Host "确认使用以上备份路径？(Y/n/输入新路径)"
if ($confirmPath -eq "n" -or $confirmPath -eq "N") {
    Write-Host "已取消。" -ForegroundColor Yellow
    exit 0
} elseif ($confirmPath -ne "" -and $confirmPath -ne "Y" -and $confirmPath -ne "y") {
    # 用户输入了新路径
    $newPath = $confirmPath.Trim()
    if ($newPath.Length -gt 2 -and $newPath -match '^[A-Za-z]:\\') {
        $BackupRoot      = $newPath
        $AuditBackupDir  = Join-Path $BackupRoot "01_审计日志与配置"
        $GroupBackupDir  = Join-Path $BackupRoot "02_组内数据"
        $UserBackupDir   = Join-Path $BackupRoot "03_用户数据"
        $BackupLogPath   = Join-Path $BackupRoot "backup.log"
        Write-Host "  [OK] 已更新备份路径: $BackupRoot" -ForegroundColor Green
    } else {
        Write-Host "  [错误] 路径格式无效（应为 X:\路径），使用默认路径继续。" -ForegroundColor Yellow
    }
}

Write-Host ""

# 第一步：初始化备份目录
Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Cyan
Write-Host " [1/4] 初始化备份目录" -ForegroundColor Cyan
Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Cyan
Write-Host ""

$initOk = Initialize-BackupDirs
if (-not $initOk) {
    Write-Host ""
    Write-Host "[错误] 备份目录初始化失败，请检查上方输出。" -ForegroundColor Red
    exit 1
}

# 第二步：生成辅助脚本
Write-Host ""
Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Cyan
Write-Host " [2/4] 生成备份脚本" -ForegroundColor Cyan
Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Cyan
Write-Host ""

New-BackupTaskScript
New-BackupNowScript

# 第三步：创建计划任务
Write-Host ""
Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Cyan
Write-Host " [3/4] 创建计划任务" -ForegroundColor Cyan
Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Cyan

New-BackupScheduledTasks

# 第四步：初始备份
Write-Host ""
Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Cyan
Write-Host " [4/4] 执行初始备份" -ForegroundColor Cyan
Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Cyan

# 检查用户数据是否存在，决定是否包含在初始备份中
$hasUserData = $false
if (Test-Path $UserDataSource) {
    $userDirs = Get-ChildItem -Path $UserDataSource -Directory -ErrorAction SilentlyContinue
    $hasUserData = ($userDirs.Count -gt 0)
}

if ($hasUserData) {
    Write-Host ""
    Write-Host "  检测到 $($userDirs.Count) 个用户目录，将包含在初始备份中。" -ForegroundColor White
    Backup-All -IncludeUserData
} else {
    Write-Host ""
    Write-Host "  暂未检测到用户数据，初始备份仅包含审计日志和组数据。" -ForegroundColor White
    Backup-All
}

# ==================== 完成 & 交互菜单 ====================

Write-Host ""
Write-Host "========================================" -ForegroundColor Green
Write-Host " 备份配置完成" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Green
Write-Host ""
Write-Host "已配置的内容：" -ForegroundColor White
Write-Host "  1. 审计日志 -> 每日 2:00 自动备份（镜像模式，保留 30 天）" -ForegroundColor Gray
Write-Host "  2. 组内数据 -> 每周日 3:00 自动备份（增量模式）" -ForegroundColor Gray
Write-Host "  3. 手动备份脚本 -> C:\Scripts\Lab-BackupNow.ps1" -ForegroundColor Gray
Write-Host "  4. 备份日志 -> $BackupLogPath" -ForegroundColor Gray
Write-Host ""

# 交互菜单
while ($true) {
    Write-Host "  [1] 立即执行全量备份（含用户数据）" -ForegroundColor White
    Write-Host "  [2] 立即执行审计日志备份" -ForegroundColor White
    Write-Host "  [3] 立即执行组数据备份" -ForegroundColor White
    Write-Host "  [4] 查看备份状态" -ForegroundColor White
    Write-Host "  [5] 退出" -ForegroundColor White
    Write-Host ""

    $choice = Read-Host "请选择操作 (1-5)"

    switch ($choice) {
        "1" {
            Backup-All -IncludeUserData
            Write-Host ""
        }
        "2" {
            Backup-AuditLogs
            Write-Host ""
        }
        "3" {
            Backup-GroupData
            Write-Host ""
        }
        "4" {
            Get-BackupStatus
        }
        "5" {
            Write-Host ""
            Write-Host "备份配置已完成，退出。" -ForegroundColor Cyan
            Write-Host "日后如需手动备份，请运行: C:\Scripts\Lab-BackupNow.ps1" -ForegroundColor Gray
            Write-Host ""
            exit 0
        }
        default {
            Write-Host "  无效选择，请输入 1-5。" -ForegroundColor Yellow
            Write-Host ""
        }
    }
}
