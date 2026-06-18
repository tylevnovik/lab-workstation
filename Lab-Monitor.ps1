<#
.SYNOPSIS
    课题组工作站 · 资源监控守护程序
.DESCRIPTION
    后台定时检查 CPU、内存、GPU（如有）、磁盘空间，超阈值时记录告警。
    由 Setup-Maintenance.ps1 部署为计划任务（开机自启 + SYSTEM 身份运行）。
    手动测试：powershell -WindowStyle Hidden -File Lab-Monitor.ps1
.NOTES
    日志路径：D:\GroupData\_公共\_使用手册\system_monitor.log
    告警冷却：同一告警 10 分钟内不重复。
#>

#Requires -Version 5.1

[CmdletBinding()]
param()

# Force culture to zh-CN for consistent log formatting
[System.Threading.Thread]::CurrentThread.CurrentCulture  = 'zh-CN'
[System.Threading.Thread]::CurrentThread.CurrentUICulture = 'zh-CN'

# ── Strict mode ──────────────────────────────────────────────
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ═════════════════════════════════════════════════════════════
#  CONFIGURATION
# ═════════════════════════════════════════════════════════════

$script:LogPath              = "D:\GroupData\_公共\_使用手册\system_monitor.log"
$script:CheckIntervalSeconds = 60
$script:AlertCooldownMinutes = 10
$script:MaxLogSizeBytes      = 5MB
$script:StatusInterval       = 10          # Write a summary INFO line every N cycles (~10 min)
$script:ProcessSampleSeconds = 2           # Seconds to sample per-process CPU delta

# Thresholds
$script:CpuThreshold         = 90          # CPU usage %
$script:MemoryThreshold      = 90          # Memory usage %
$script:DiskThreshold        = 90          # Disk usage % (any fixed drive)
$script:GpuThreshold         = 95          # GPU usage % (if NVIDIA available)
$script:LongRunningHours     = 48          # Alert on user processes running longer than this

# ═════════════════════════════════════════════════════════════
#  INTERNAL STATE
# ═════════════════════════════════════════════════════════════

$script:alertCooldowns = @{}               # @{ AlertKey = [datetime] }
$script:cycleCount     = 0
$script:nvidiaSmiPath  = $null
$script:gpuAvailable   = $false

# ═════════════════════════════════════════════════════════════
#  LOGGING
# ═════════════════════════════════════════════════════════════

function Rotate-LogFile {
    <#
    .SYNOPSIS
        Rotate the monitor log when it exceeds the configured size limit.
    .DESCRIPTION
        Archives the current log as .bak and starts a fresh file.
        Only one archive is retained (the previous .bak is overwritten).
    #>
    try {
        if (-not (Test-Path -LiteralPath $script:LogPath)) { return }

        $fileInfo = Get-Item -LiteralPath $script:LogPath -Force
        if ($fileInfo.Length -le $script:MaxLogSizeBytes) { return }

        $bakPath = "$($script:LogPath).bak"

        # Remove previous archive if it exists
        if (Test-Path -LiteralPath $bakPath) {
            Remove-Item -LiteralPath $bakPath -Force
        }

        Rename-Item -LiteralPath $script:LogPath -NewName "$($script:LogPath).bak" -Force

        $null = New-Item -Path $script:LogPath -ItemType File -Force
    }
    catch {
        # Last-resort: try to write a rotation-failure notice
        try {
            $ts = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
            Add-Content -LiteralPath $script:LogPath `
                -Value "[$ts] [WARN] 日志轮转失败: $_" `
                -Encoding UTF8
        }
        catch {
            # Completely silent – nothing we can do
        }
    }
}

function Write-MonitorLog {
    <#
    .SYNOPSIS
        Append a timestamped entry to the monitor log file.
    .PARAMETER Level
        Severity level: INFO, WARN, ALERT, ERROR
    .PARAMETER Message
        Free-form message text.
    #>
    param(
        [Parameter(Mandatory)]
        [ValidateSet('INFO', 'WARN', 'ALERT', 'ERROR')]
        [string]$Level,

        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string]$Message
    )

    try {
        # Rotate before writing if the file is already oversized
        Rotate-LogFile

        $timestamp = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
        $entry     = "[$timestamp] [$Level] $Message"

        # Ensure the parent directory exists
        $logDir = Split-Path -Path $script:LogPath -Parent
        if ($logDir -and -not (Test-Path -LiteralPath $logDir)) {
            $null = New-Item -Path $logDir -ItemType Directory -Force
        }

        Add-Content -LiteralPath $script:LogPath -Value $entry -Encoding UTF8
    }
    catch {
        # Swallow to prevent logging failures from crashing the daemon
        Write-Warning "Write-MonitorLog failed: $_"
    }
}

# ═════════════════════════════════════════════════════════════
#  ALERT COOLDOWN
# ═════════════════════════════════════════════════════════════

function Check-CanAlert {
    <#
    .SYNOPSIS
        Determine whether enough time has elapsed since the last alert
        identified by $AlertKey.
    .OUTPUTS
        [bool]
    #>
    param(
        [Parameter(Mandatory)]
        [string]$AlertKey
    )

    if (-not $script:alertCooldowns.ContainsKey($AlertKey)) {
        return $true
    }

    $elapsed = (Get-Date) - $script:alertCooldowns[$AlertKey]
    return ($elapsed.TotalMinutes -ge $script:AlertCooldownMinutes)
}

function Record-AlertTime {
    <#
    .SYNOPSIS
        Record the current time as the last-alert time for $AlertKey.
    #>
    param(
        [Parameter(Mandatory)]
        [string]$AlertKey
    )

    $script:alertCooldowns[$AlertKey] = Get-Date
}

# ═════════════════════════════════════════════════════════════
#  METRIC COLLECTORS
# ═════════════════════════════════════════════════════════════

function Get-CpuUsage {
    <#
    .SYNOPSIS
        Collect overall CPU usage and the top 3 CPU-consuming processes.
    .DESCRIPTION
        Uses Win32_Processor.LoadPercentage for the system-wide value,
        then samples per-process CPU time over a short interval to rank
        the busiest processes.
    .OUTPUTS
        @{ Usage = [int]; TopProcesses = @(@{ Name; PID; User; CpuPct }) }
    #>
    try {
        # ── Overall CPU ──────────────────────────────────────
        $processors = @(Get-CimInstance -ClassName Win32_Processor -ErrorAction Stop)
        $avgLoad    = [int](($processors | Measure-Object -Property LoadPercentage -Average).Average)

        # ── Per-process CPU (sampled delta) ──────────────────
        $snap1 = @{}
        Get-Process -ErrorAction SilentlyContinue | ForEach-Object {
            if ($null -ne $_.Id -and $null -ne $_.CPU) {
                $snap1[$_.Id] = @{
                    Name = $_.ProcessName
                    Cpu  = $_.CPU            # total processor time (seconds)
                }
            }
        }

        Start-Sleep -Seconds $script:ProcessSampleSeconds

        $snap2 = @{}
        Get-Process -ErrorAction SilentlyContinue | ForEach-Object {
            if ($null -ne $_.Id -and $null -ne $_.CPU) {
                $snap2[$_.Id] = @{
                    Name = $_.ProcessName
                    Cpu  = $_.CPU
                }
            }
        }

        $logicalCores = [Environment]::ProcessorCount
        $processList    = [System.Collections.ArrayList]::new()

        foreach ($pid2 in $snap2.Keys) {
            if (-not $snap1.ContainsKey($pid2)) { continue }

            $delta = $snap2[$pid2].Cpu - $snap1[$pid2].Cpu
            if ($delta -le 0) { continue }

            # delta seconds / (sample window * cores) * 100
            $cpuPct = [math]::Round(
                ($delta / ($script:ProcessSampleSeconds * $logicalCores)) * 100, 1
            )

            $null = $processList.Add(@{
                Name   = $snap2[$pid2].Name
                PID    = $pid2
                CpuPct = $cpuPct
            })
        }

        # Sort descending by CpuPct, take top 3
        $topProcesses = $processList |
            Sort-Object -Property CpuPct -Descending |
            Select-Object -First 3

        # Resolve the owning user for each top process
        foreach ($proc in $topProcesses) {
            try {
                $wmiProc = Get-WmiObject -Class Win32_Process `
                    -Filter "ProcessId = $($proc.PID)" `
                    -ErrorAction Stop `
                    -WarningAction SilentlyContinue
                $ownerResult = $wmiProc.GetOwner()
                $user = if ($ownerResult.ReturnValue -eq 0 -and $ownerResult.User) {
                    $ownerResult.User
                } else {
                    'N/A'
                }
            }
            catch {
                $user = 'N/A'
            }
            $proc['User'] = $user
        }

        return @{
            Usage        = $avgLoad
            TopProcesses = @($topProcesses)
        }
    }
    catch {
        Write-MonitorLog 'WARN' "CPU 指标采集失败: $_"
        return @{ Usage = 0; TopProcesses = @() }
    }
}

function Get-MemoryUsage {
    <#
    .SYNOPSIS
        Collect system memory utilisation.
    .OUTPUTS
        @{ Usage = [double]; UsedGB = [double]; TotalGB = [double] }
    #>
    try {
        $os = Get-CimInstance -ClassName Win32_OperatingSystem -ErrorAction Stop

        $totalKB = $os.TotalVisibleMemorySize
        $freeKB  = $os.FreePhysicalMemory
        $usedKB  = $totalKB - $freeKB

        return @{
            Usage   = [math]::Round(($usedKB / $totalKB) * 100, 1)
            UsedGB  = [math]::Round($usedKB  / 1MB, 1)
            TotalGB = [math]::Round($totalKB / 1MB, 1)
        }
    }
    catch {
        Write-MonitorLog 'WARN' "内存指标采集失败: $_"
        return @{ Usage = 0; UsedGB = 0; TotalGB = 0 }
    }
}

function Get-DiskUsage {
    <#
    .SYNOPSIS
        Collect usage for every fixed (local) drive.
    .OUTPUTS
        Array of @{ Drive; Usage; FreeGB; TotalGB }
    #>
    $results = [System.Collections.ArrayList]::new()

    try {
        $drives = Get-CimInstance -ClassName Win32_LogicalDisk `
            -Filter "DriveType = 3" `
            -ErrorAction Stop

        foreach ($drive in $drives) {
            if ($drive.Size -le 0) { continue }

            $freeGB  = [math]::Round($drive.FreeSpace / 1GB, 1)
            $totalGB = [math]::Round($drive.Size      / 1GB, 1)
            $usage   = [math]::Round(
                (($drive.Size - $drive.FreeSpace) / $drive.Size) * 100, 1
            )

            $null = $results.Add(@{
                Drive   = $drive.DeviceID
                Usage   = $usage
                FreeGB  = $freeGB
                TotalGB = $totalGB
            })
        }
    }
    catch {
        Write-MonitorLog 'WARN' "磁盘指标采集失败: $_"
    }

    return @($results)
}

function Get-GpuUsage {
    <#
    .SYNOPSIS
        Query NVIDIA GPU utilisation via nvidia-smi.
    .DESCRIPTION
        Locates nvidia-smi on first call and caches the path.
        Returns $null when no NVIDIA GPU or driver is present.
    .OUTPUTS
        @{ Usage = [int]; MemoryUsedMB = [int]; MemoryTotalMB = [int] } or $null
    #>

    # ── One-time discovery of nvidia-smi ─────────────────────
    if (-not $script:gpuAvailable -and $null -eq $script:nvidiaSmiPath) {
        $candidates = @(
            "${env:ProgramFiles}\NVIDIA Corporation\NVSMI\nvidia-smi.exe"
            "${env:ProgramFiles(x86)}\NVIDIA Corporation\NVSMI\nvidia-smi.exe"
            "$env:SystemRoot\System32\nvidia-smi.exe"
        )

        foreach ($candidate in $candidates) {
            if ($candidate -and (Test-Path -LiteralPath $candidate)) {
                $script:nvidiaSmiPath = $candidate
                $script:gpuAvailable  = $true
                Write-MonitorLog 'INFO' "已检测到 nvidia-smi: $candidate"
                break
            }
        }

        # Also try PATH as a fallback
        if (-not $script:gpuAvailable) {
            $pathHit = Get-Command -Name 'nvidia-smi' -ErrorAction SilentlyContinue
            if ($pathHit) {
                $script:nvidiaSmiPath = $pathHit.Source
                $script:gpuAvailable  = $true
                Write-MonitorLog 'INFO' "已检测到 nvidia-smi (via PATH): $($pathHit.Source)"
            }
        }

        if (-not $script:gpuAvailable) {
            $script:nvidiaSmiPath = ""
            Write-MonitorLog 'INFO' '未检测到 NVIDIA GPU (nvidia-smi)，跳过 GPU 监控'
        }
    }

    if (-not $script:gpuAvailable) { return $null }

    # ── Query nvidia-smi ─────────────────────────────────────
    try {
        $rawOutput = & $script:nvidiaSmiPath `
            --query-gpu=utilization.gpu,memory.used,memory.total `
            --format=csv,noheader,nounits 2>&1

        if ($LASTEXITCODE -ne 0) {
            Write-MonitorLog 'WARN' "nvidia-smi 返回非零退出码 ($LASTEXITCODE)"
            return $null
        }

        # Take the first GPU (index 0) in multi-GPU systems
        $firstLine = ($rawOutput | Select-Object -First 1).ToString().Trim()
        $parts     = $firstLine -split ',' | ForEach-Object { $_.Trim() }

        if ($parts.Count -lt 3) {
            Write-MonitorLog 'WARN' "nvidia-smi 输出格式异常: $firstLine"
            return $null
        }

        $gpuUtil   = [int]($parts[0] -replace '[^\d]', '')
        $memUsed   = [int]($parts[1] -replace '[^\d]', '')
        $memTotal  = [int]($parts[2] -replace '[^\d]', '')

        return @{
            Usage         = $gpuUtil
            MemoryUsedMB  = $memUsed
            MemoryTotalMB = $memTotal
        }
    }
    catch {
        Write-MonitorLog 'WARN' "GPU 指标采集失败: $_"
        return $null
    }
}

function Get-LongRunningProcesses {
    <#
    .SYNOPSIS
        Find user-owned processes that have been running longer than
        $script:LongRunningHours.
    .DESCRIPTION
        System / service accounts (SYSTEM, LOCAL SERVICE, NETWORK SERVICE,
        DWM-*) are excluded so only real user workloads are reported.
    .OUTPUTS
        Array of @{ Name; PID; User; RunningHours }
    #>
    $results = [System.Collections.ArrayList]::new()

    try {
        $now       = Get-Date
        $threshold = $now.AddHours(-$script:LongRunningHours)

        # System accounts to skip
        $systemAccounts = @(
            'NT AUTHORITY\SYSTEM'
            'NT AUTHORITY\LOCAL SERVICE'
            'NT AUTHORITY\NETWORK SERVICE'
            'SYSTEM'
            'LOCAL SERVICE'
            'NETWORK SERVICE'
        )

        $processes = Get-WmiObject -Class Win32_Process `
            -ErrorAction SilentlyContinue `
            -WarningAction SilentlyContinue

        foreach ($proc in $processes) {
            # Skip processes with no creation date
            if (-not $proc.CreationDate) { continue }

            try {
                $creationTime = $proc.ConvertToDateTime($proc.CreationDate)
            }
            catch { continue }

            if ($creationTime -gt $threshold) { continue }

            # Resolve owner
            try {
                $ownerResult = $proc.GetOwner()
                if ($ownerResult.ReturnValue -ne 0) { continue }
                $user = $ownerResult.User
                if (-not $user) { continue }

                $fullUser = if ($ownerResult.Domain) {
                    "$($ownerResult.Domain)\$user"
                } else {
                    $user
                }
            }
            catch { continue }

            # Skip system accounts
            $isSystem = $false
            foreach ($sysAcct in $systemAccounts) {
                if ($fullUser -like "*$sysAcct*") {
                    $isSystem = $true
                    break
                }
            }
            if ($isSystem) { continue }

            # Skip common desktop-manager processes that always run under the user
            if ($proc.Name -match '^dwm-\d+') { continue }

            $runningHours = [math]::Round(
                ($now - $creationTime).TotalHours, 1
            )

            $null = $results.Add(@{
                Name         = $proc.Name
                PID          = $proc.ProcessId
                User         = $fullUser
                RunningHours = $runningHours
            })
        }
    }
    catch {
        Write-MonitorLog 'WARN' "长时间运行进程检测失败: $_"
    }

    return @($results)
}

function Get-ActiveSessions {
    <#
    .SYNOPSIS
        Retrieve the list of active console / RDP user sessions.
    .DESCRIPTION
        Parses the output of `query user` (quser).
    .OUTPUTS
        @{ Count = [int]; Users = [string[]] }
    #>
    try {
        $rawOutput = & cmd /c 'query user 2>nul'

        if ($LASTEXITCODE -ne 0 -or -not $rawOutput) {
            # query user may not be available (e.g. Server Core) — try quser
            $rawOutput = & cmd /c 'quser 2>nul'
        }

        if ($LASTEXITCODE -ne 0 -or -not $rawOutput) {
            return @{ Count = 0; Users = @() }
        }

        $lines  = @($rawOutput) | Select-Object -Skip 1   # skip header row
        $users  = [System.Collections.ArrayList]::new()

        foreach ($line in $lines) {
            if ([string]::IsNullOrWhiteSpace($line)) { continue }

            # First column is the username (may have leading '>' for current session)
            $username = ($line -split '\s+')[0] -replace '^>', ''
            if ($username -and $username -ne '') {
                $null = $users.Add($username)
            }
        }

        return @{
            Count = $users.Count
            Users = @($users)
        }
    }
    catch {
        return @{ Count = 0; Users = @() }
    }
}

# ═════════════════════════════════════════════════════════════
#  MAIN LOOP
# ═════════════════════════════════════════════════════════════

Write-MonitorLog 'INFO' '════════════════════════════════════════════'
Write-MonitorLog 'INFO' '资源监控服务已启动'
Write-MonitorLog 'INFO' "  检查间隔   : $($script:CheckIntervalSeconds)s (进程采样: $($script:ProcessSampleSeconds)s)"
Write-MonitorLog 'INFO' "  告警冷却   : $($script:AlertCooldownMinutes) min"
Write-MonitorLog 'INFO' "  日志上限   : $([math]::Round($script:MaxLogSizeBytes / 1MB)) MB → 自动轮转"
Write-MonitorLog 'INFO' "  阈值 → CPU: $($script:CpuThreshold)% | 内存: $($script:MemoryThreshold)% | 磁盘: $($script:DiskThreshold)% | GPU: $($script:GpuThreshold)%"
Write-MonitorLog 'INFO' "  长时进程   : > $($script:LongRunningHours)h"
Write-MonitorLog 'INFO' '════════════════════════════════════════════'

$script:alertCooldowns = @{}
$script:cycleCount     = 0

while ($true) {
    try {
        $script:cycleCount++

        # ──────────────────────────────────────────────────────
        # 1. Collect all metrics
        # ──────────────────────────────────────────────────────
        $cpu    = Get-CpuUsage
        $mem    = Get-MemoryUsage
        $disks  = Get-DiskUsage
        $gpu    = Get-GpuUsage

        # ──────────────────────────────────────────────────────
        # 2. Check CPU
        # ──────────────────────────────────────────────────────
        if ($cpu.Usage -ge $script:CpuThreshold -and (Check-CanAlert 'cpu_high')) {
            $topInfo = if ($cpu.TopProcesses.Count -gt 0) {
                ($cpu.TopProcesses | ForEach-Object {
                    "$($_.Name)(PID $($_.PID), 用户 $($_.User), $($_.CpuPct)%)"
                }) -join '; '
            } else {
                '(无法获取进程详情)'
            }

            Write-MonitorLog 'ALERT' "CPU 使用率 $($cpu.Usage)% 超过阈值 $($script:CpuThreshold)% | 高占用进程: $topInfo"
            Record-AlertTime 'cpu_high'
        }

        # ──────────────────────────────────────────────────────
        # 3. Check Memory
        # ──────────────────────────────────────────────────────
        if ($mem.Usage -ge $script:MemoryThreshold -and (Check-CanAlert 'memory_high')) {
            Write-MonitorLog 'ALERT' "内存使用率 $($mem.Usage)% ($($mem.UsedGB)/$($mem.TotalGB) GB) 超过阈值 $($script:MemoryThreshold)%"
            Record-AlertTime 'memory_high'
        }

        # ──────────────────────────────────────────────────────
        # 4. Check Disk (each fixed drive)
        # ──────────────────────────────────────────────────────
        foreach ($disk in $disks) {
            $diskKey = "disk_$($disk.Drive -replace '[^A-Za-z]', '')"

            if ($disk.Usage -ge $script:DiskThreshold -and (Check-CanAlert $diskKey)) {
                Write-MonitorLog 'ALERT' "磁盘 $($disk.Drive) 使用率 $($disk.Usage)% (剩余 $($disk.FreeGB)/$($disk.TotalGB) GB) 超过阈值 $($script:DiskThreshold)%"
                Record-AlertTime $diskKey
            }
        }

        # ──────────────────────────────────────────────────────
        # 5. Check GPU (skip when unavailable)
        # ──────────────────────────────────────────────────────
        if ($null -ne $gpu) {
            if ($gpu.Usage -ge $script:GpuThreshold -and (Check-CanAlert 'gpu_high')) {
                Write-MonitorLog 'ALERT' "GPU 使用率 $($gpu.Usage)% (显存 $($gpu.MemoryUsedMB)/$($gpu.MemoryTotalMB) MB) 超过阈值 $($script:GpuThreshold)%"
                Record-AlertTime 'gpu_high'
            }
        }

        # ──────────────────────────────────────────────────────
        # 6. Check long-running user processes
        # ──────────────────────────────────────────────────────
        $longRunning = Get-LongRunningProcesses
        if ($longRunning.Count -gt 0 -and (Check-CanAlert 'long_running_procs')) {
            $procInfo = ($longRunning | ForEach-Object {
                "$($_.Name)(PID $($_.PID), 用户 $($_.User), 已运行 $($_.RunningHours)h)"
            }) -join '; '

            Write-MonitorLog 'WARN' "检测到 $($longRunning.Count) 个长时间运行进程 (>$($script:LongRunningHours)h): $procInfo"
            Record-AlertTime 'long_running_procs'
        }

        # ──────────────────────────────────────────────────────
        # 7. Periodic status summary (every N cycles)
        # ──────────────────────────────────────────────────────
        if ($script:cycleCount % $script:StatusInterval -eq 0) {
            $sessions = Get-ActiveSessions

            # Build GPU fragment
            $gpuFrag = if ($null -ne $gpu) {
                "GPU: $($gpu.Usage)% (显存 $($gpu.MemoryUsedMB)/$($gpu.MemoryTotalMB)MB)"
            } else {
                'GPU: N/A'
            }

            # Build disk fragment
            $diskFrag = ($disks | ForEach-Object {
                "$($_.Drive) $($_.Usage)%"
            }) -join ', '
            if (-not $diskFrag) { $diskFrag = 'N/A' }

            # Build session fragment
            $sessFrag = if ($sessions.Count -gt 0) {
                "$($sessions.Count) 人 ($($sessions.Users -join ', '))"
            } else {
                '无活跃会话'
            }

            Write-MonitorLog 'INFO' "状态摘要 | CPU: $($cpu.Usage)% | 内存: $($mem.Usage)% ($($mem.UsedGB)/$($mem.TotalGB)GB) | 磁盘: $diskFrag | $gpuFrag | 会话: $sessFrag | 长时进程: $($longRunning.Count)"
        }
    }
    catch {
        Write-MonitorLog 'ERROR' "检查循环异常: $_"
    }

    Start-Sleep -Seconds $script:CheckIntervalSeconds
}
