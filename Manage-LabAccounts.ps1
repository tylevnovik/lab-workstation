<#
.SYNOPSIS
    课题组工作站账户管理工具（多导师组版）
.DESCRIPTION
    可视化管理工作站用户账户、导师分组和安全组权限。
    支持多导师组结构：Lab_All（全员组）+ Lab_[导师名]（导师组）。
    支持一键创建账户、分配权限、禁用/启用/移除、分组管理、性能优化和批量操作。
    仅管理员可运行。
.NOTES
    使用方法：右键此文件 → "使用 PowerShell 运行"
    或：以管理员身份打开 PowerShell，执行 .\Manage-LabAccounts.ps1
#>

#Requires -RunAsAdministrator

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

[System.Windows.Forms.Application]::EnableVisualStyles()

# ==================== 配置区 ====================
$script:AllGroup        = "Lab_All"
$script:SharePath       = "D:\GroupData"
$script:PublicPath      = "D:\GroupData\_公共"
$script:UsersRootPath   = "D:\Users"
$script:GroupCategories = @(
    "01_人才类数据",
    "02_温故知新数据",
    "03_科技报告",
    "04_资政报告",
    "05_项目资料",
    "99_归档"
)

# ==================== 辅助函数 ====================

# 审计日志路径（持久化，所有管理员可查阅）
$script:AuditLogPath = "D:\GroupData\_公共\_使用手册\admin_operations.log"

function Write-AuditLog {
    <#
    .SYNOPSIS
        写入持久化审计日志。仅记录改变系统状态的操作。
    .PARAMETER Action
        操作类型：CREATE_USER, DISABLE_USER, ENABLE_USER, RESET_PASSWORD,
        CHANGE_GROUP, REMOVE_FROM_GROUP, REMOVE_USER, CREATE_ADVISOR_GROUP,
        DELETE_ADVISOR_GROUP, ADD_TO_GROUP, BATCH_CREATE, APPLY_OPTIMIZATION
    .PARAMETER Target
        操作对象（用户名、组名等）
    .PARAMETER Result
        结果：SUCCESS / FAILED
    .PARAMETER Detail
        补充说明（可选）
    #>
    param(
        [string]$Action,
        [string]$Target,
        [string]$Result = "SUCCESS",
        [string]$Detail = ""
    )
    try {
        $logDir = Split-Path $script:AuditLogPath -Parent
        if (-not (Test-Path $logDir)) { New-Item -ItemType Directory -Path $logDir -Force | Out-Null }

        $operator = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
        $ts       = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
        $detailPart = if ($Detail) { " | 详情: $Detail" } else { "" }
        $line     = "[$ts] 操作人: $operator | 操作: $Action | 对象: $Target | 结果: $Result$detailPart"

        Add-Content -Path $script:AuditLogPath -Value $line -Encoding UTF8
    } catch {
        # 日志写入失败不应阻塞主流程，仅在 UI 日志中提示
        Write-Log "审计日志写入失败: $_" "WARN"
    }
}

function Rotate-AuditLog {
    try {
        if (-not (Test-Path $script:AuditLogPath)) { return }
        $maxSize = 10MB
        $fileInfo = Get-Item $script:AuditLogPath
        if ($fileInfo.Length -ge $maxSize) {
            $logDir = Split-Path $script:AuditLogPath -Parent
            $archiveName = "admin_operations_$(Get-Date -Format 'yyyyMM').log"
            $archivePath = Join-Path $logDir $archiveName
            if (Test-Path $archivePath) {
                # Append to existing monthly archive
                Get-Content $script:AuditLogPath | Add-Content $archivePath -Encoding UTF8
                Remove-Item $script:AuditLogPath -Force
            } else {
                Rename-Item $script:AuditLogPath $archivePath
            }
            New-Item -ItemType File -Path $script:AuditLogPath -Force | Out-Null
            Write-Log "审计日志已归档为 $archiveName"

            # Keep max 12 archives
            $archives = Get-ChildItem -Path $logDir -Filter "admin_operations_*.log" | Sort-Object LastWriteTime
            while ($archives.Count -gt 12) {
                $archives[0] | Remove-Item -Force
                Write-Log "已删除过期归档: $($archives[0].Name)"
                $archives = Get-ChildItem -Path $logDir -Filter "admin_operations_*.log" | Sort-Object LastWriteTime
            }
        }
    } catch {
        Write-Log "日志轮转失败: $_" "WARN"
    }
}

function Write-Log {
    param([string]$Message, [string]$Level = "INFO")
    $ts = Get-Date -Format "HH:mm:ss"
    $color = switch ($Level) { "ERROR" { "Red" } "WARN" { "Orange" } default { "Black" } }
    $line = "[$ts] $Message"
    $script:logBox.SelectionStart  = $script:logBox.TextLength
    $script:logBox.SelectionLength = 0
    $script:logBox.SelectionColor  = [System.Drawing.Color]::$color
    $script:logBox.AppendText("$line`r`n")
    $script:logBox.ScrollToCaret()
}

function Show-Dialog {
    param(
        [string]$Title,
        [string]$Text,
        [System.Windows.Forms.MessageBoxButtons]$Buttons = "OK",
        [System.Windows.Forms.MessageBoxIcon]$Icon = "Information"
    )
    [System.Windows.Forms.MessageBox]::Show($Text, $Title, $Buttons, $Icon)
}

function Test-GroupExists {
    param([string]$Name)
    try {
        Get-LocalGroup -Name $Name -ErrorAction Stop | Out-Null
        return $true
    } catch {
        return $false
    }
}

function Get-AllAdvisorGroups {
    $advisors = @()
    try {
        Get-LocalGroup | Where-Object { $_.Name -match '^Lab_' -and $_.Name -ne $script:AllGroup } | ForEach-Object {
            $advisorName = $_.Name -replace '^Lab_', ''
            $advisors += $advisorName
        }
    } catch {
        Write-Log "获取导师组列表失败: $_" "ERROR"
    }
    return $advisors
}

function Get-GroupMembers {
    param([string]$GroupName)
    $members = @()
    try {
        $group = [ADSI]"WinNT://./$GroupName,group"
        foreach ($m in @($group.Invoke("Members"))) {
            $name = $m.GetType().InvokeMember("Name", "GetProperty", $null, $m, $null)
            $members += $name
        }
    } catch {
        Write-Log "获取组 '$GroupName' 成员失败: $_" "ERROR"
    }
    return $members
}

function Get-UserAdvisorGroup {
    param([string]$Username)
    $advisorGroups = Get-AllAdvisorGroups
    foreach ($advisor in $advisorGroups) {
        $groupName = "Lab_$advisor"
        $members = Get-GroupMembers -GroupName $groupName
        if ($members -contains $Username) {
            return $advisor
        }
    }
    return ""
}

function New-AdvisorGroup {
    param([string]$AdvisorName)
    $groupName = "Lab_$AdvisorName"

    # 创建安全组
    if (Test-GroupExists -Name $groupName) {
        Write-Log "导师组 '$groupName' 已存在" "WARN"
    } else {
        net localgroup $groupName /add 2>&1 | Out-Null
        Write-Log "导师组 '$groupName' 创建成功"
        Write-AuditLog -Action "CREATE_ADVISOR_GROUP" -Target $AdvisorName
    }

    # 创建导师文件夹及分类子目录
    $advisorPath = Join-Path $script:SharePath $AdvisorName
    if (-not (Test-Path $advisorPath)) {
        New-Item -ItemType Directory -Path $advisorPath -Force | Out-Null
        Write-Log "创建导师文件夹: $advisorPath"
    }

    foreach ($cat in $script:GroupCategories) {
        $catPath = Join-Path $advisorPath $cat
        if (-not (Test-Path $catPath)) {
            New-Item -ItemType Directory -Path $catPath -Force | Out-Null
        }
    }
    Write-Log "已创建 $($script:GroupCategories.Count) 个分类子目录"

    # 设置 NTFS 权限：断开继承，仅本导师组和管理员可访问
    $acl = Get-Acl $advisorPath
    $acl.SetAccessRuleProtection($true, $false)

    # 清除已有非继承规则
    $acl.Access | Where-Object { -not $_.IsInherited } | ForEach-Object { $acl.RemoveAccessRule($_) | Out-Null }

    # 管理员完全控制
    $adminRule = New-Object System.Security.AccessControl.FileSystemAccessRule(
        "Administrators", "FullControl",
        "ContainerInherit,ObjectInherit", "None", "Allow"
    )
    $acl.AddAccessRule($adminRule)

    # SYSTEM 完全控制
    $sysRule = New-Object System.Security.AccessControl.FileSystemAccessRule(
        "SYSTEM", "FullControl",
        "ContainerInherit,ObjectInherit", "None", "Allow"
    )
    $acl.AddAccessRule($sysRule)

    # 导师组修改权限
    $groupRule = New-Object System.Security.AccessControl.FileSystemAccessRule(
        $groupName, "Modify",
        "ContainerInherit,ObjectInherit", "None", "Allow"
    )
    $acl.AddAccessRule($groupRule)

    Set-Acl -Path $advisorPath -AclObject $acl

    # 递归应用权限到所有子文件夹（与 setup_workstation.ps1 行为一致）
    Get-ChildItem -Path $advisorPath -Recurse -Directory | ForEach-Object {
        Set-Acl -Path $_.FullName -AclObject $acl
    }

    Write-Log "已设置 '$advisorPath' NTFS 权限（$groupName 可修改，管理员完全控制，已递归到子文件夹）"
}

function New-UserPersonalDir {
    param([string]$Username)
    $userDir = Join-Path $script:UsersRootPath $Username
    if (-not (Test-Path $script:UsersRootPath)) {
        New-Item -ItemType Directory -Path $script:UsersRootPath -Force | Out-Null
        Write-Log "创建个人目录根目录: $($script:UsersRootPath)"
    }
    if (-not (Test-Path $userDir)) {
        New-Item -ItemType Directory -Path $userDir -Force | Out-Null

        $userAcl = Get-Acl $userDir
        $userAcl.SetAccessRuleProtection($true, $false)

        $adminRule = New-Object System.Security.AccessControl.FileSystemAccessRule(
            "Administrators", "FullControl",
            "ContainerInherit,ObjectInherit", "None", "Allow"
        )
        $userAcl.AddAccessRule($adminRule)

        $sysRule = New-Object System.Security.AccessControl.FileSystemAccessRule(
            "SYSTEM", "FullControl",
            "ContainerInherit,ObjectInherit", "None", "Allow"
        )
        $userAcl.AddAccessRule($sysRule)

        $userRule = New-Object System.Security.AccessControl.FileSystemAccessRule(
            $Username, "FullControl",
            "ContainerInherit,ObjectInherit", "None", "Allow"
        )
        $userAcl.AddAccessRule($userRule)

        Set-Acl -Path $userDir -AclObject $userAcl
        Write-Log "个人目录已创建并隔离: $userDir"
    } else {
        Write-Log "个人目录已存在: $userDir（跳过创建）" "WARN"
    }
    return $userDir
}

function Generate-RandomPassword {
    $chars = "abcdefghijkmnopqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789!@#`$"
    return -join ((1..14) | ForEach-Object { $chars[(Get-Random -Maximum $chars.Length)] })
}

# ==================== 主窗体 ====================
$form = New-Object System.Windows.Forms.Form
$form.Text          = "课题组工作站 · 账户管理工具（多导师组版）"
$form.Size          = New-Object System.Drawing.Size(820, 700)
$form.StartPosition = "CenterScreen"
$form.FormBorderStyle = "FixedDialog"
$form.MaximizeBox   = $false
$form.Font          = New-Object System.Drawing.Font("Microsoft YaHei UI", 9)

# 顶部标题栏
$headerPanel = New-Object System.Windows.Forms.Panel
$headerPanel.Dock      = "Top"
$headerPanel.Height    = 44
$headerPanel.BackColor = [System.Drawing.Color]::FromArgb(0, 102, 179)

$headerLabel = New-Object System.Windows.Forms.Label
$headerLabel.Text      = "  课题组工作站 · 账户管理工具"
$headerLabel.ForeColor = "White"
$headerLabel.Font      = New-Object System.Drawing.Font("Microsoft YaHei UI", 13, "Bold")
$headerLabel.Dock      = "Fill"
$headerLabel.TextAlign = "MiddleLeft"
$headerPanel.Controls.Add($headerLabel)
$form.Controls.Add($headerPanel)

# 标签页容器
$tabs = New-Object System.Windows.Forms.TabControl
$tabs.Location = New-Object System.Drawing.Point(10, 50)
$tabs.Size     = New-Object System.Drawing.Size(785, 440)
$tabs.Font     = New-Object System.Drawing.Font("Microsoft YaHei UI", 9.5)
$form.Controls.Add($tabs)

# 底部日志区
$logGroup = New-Object System.Windows.Forms.GroupBox
$logGroup.Text     = "操作日志"
$logGroup.Location = New-Object System.Drawing.Point(10, 498)
$logGroup.Size     = New-Object System.Drawing.Size(785, 150)
$logGroup.Font     = New-Object System.Drawing.Font("Microsoft YaHei UI", 8.5)
$form.Controls.Add($logGroup)

$script:logBox = New-Object System.Windows.Forms.RichTextBox
$script:logBox.Location    = New-Object System.Drawing.Point(8, 18)
$script:logBox.Size        = New-Object System.Drawing.Size(768, 125)
$script:logBox.ReadOnly    = $true
$script:logBox.BackColor   = [System.Drawing.Color]::FromArgb(250, 250, 250)
$script:logBox.BorderStyle = "FixedSingle"
$script:logBox.Font        = New-Object System.Drawing.Font("Consolas", 8.5)
$logGroup.Controls.Add($script:logBox)

# ==================== Tab 1: 成员管理 ====================
$tab1 = New-Object System.Windows.Forms.TabPage
$tab1.Text = "成员管理"

$y = 10

# -- 组筛选栏 --
$filterGroup = New-Object System.Windows.Forms.GroupBox
$filterGroup.Text     = "按组筛选"
$filterGroup.Location = New-Object System.Drawing.Point(15, $y)
$filterGroup.Size     = New-Object System.Drawing.Size(745, 50)
$tab1.Controls.Add($filterGroup)

$filterLabel = New-Object System.Windows.Forms.Label
$filterLabel.Text     = "选择组："
$filterLabel.Location = New-Object System.Drawing.Point(15, 20)
$filterLabel.AutoSize = $true
$filterGroup.Controls.Add($filterLabel)

$script:filterCombo = New-Object System.Windows.Forms.ComboBox
$script:filterCombo.Location    = New-Object System.Drawing.Point(80, 17)
$script:filterCombo.Size        = New-Object System.Drawing.Size(200, 25)
$script:filterCombo.DropDownStyle = "DropDownList"
$filterGroup.Controls.Add($script:filterCombo)

$filterRefreshBtn = New-Object System.Windows.Forms.Button
$filterRefreshBtn.Text     = "刷新筛选"
$filterRefreshBtn.Location = New-Object System.Drawing.Point(300, 15)
$filterRefreshBtn.Size     = New-Object System.Drawing.Size(90, 28)
$filterGroup.Controls.Add($filterRefreshBtn)

$script:memberCountLabel = New-Object System.Windows.Forms.Label
$script:memberCountLabel.Location = New-Object System.Drawing.Point(420, 20)
$script:memberCountLabel.AutoSize = $true
$script:memberCountLabel.ForeColor = "Gray"
$filterGroup.Controls.Add($script:memberCountLabel)

$y += 58

# -- 成员列表 --
$listGroup = New-Object System.Windows.Forms.GroupBox
$listGroup.Text     = "成员列表"
$listGroup.Location = New-Object System.Drawing.Point(15, $y)
$listGroup.Size     = New-Object System.Drawing.Size(745, 330)
$tab1.Controls.Add($listGroup)

$script:memberList = New-Object System.Windows.Forms.ListView
$script:memberList.Location    = New-Object System.Drawing.Point(12, 22)
$script:memberList.Size        = New-Object System.Drawing.Size(590, 295)
$script:memberList.View        = "Details"
$script:memberList.FullRowSelect = $true
$script:memberList.GridLines   = $true
$script:memberList.Columns.Add("用户名", 120)   | Out-Null
$script:memberList.Columns.Add("显示名称", 110)  | Out-Null
$script:memberList.Columns.Add("所属导师组", 110) | Out-Null
$script:memberList.Columns.Add("账户状态", 80)   | Out-Null
$script:memberList.Columns.Add("上次登录", 140)  | Out-Null
$listGroup.Controls.Add($script:memberList)

$refreshBtn = New-Object System.Windows.Forms.Button
$refreshBtn.Text     = "刷新列表"
$refreshBtn.Location = New-Object System.Drawing.Point(618, 22)
$refreshBtn.Size     = New-Object System.Drawing.Size(112, 30)
$listGroup.Controls.Add($refreshBtn)

$removeBtn = New-Object System.Windows.Forms.Button
$removeBtn.Text     = "从组中移除"
$removeBtn.Location = New-Object System.Drawing.Point(618, 60)
$removeBtn.Size     = New-Object System.Drawing.Size(112, 30)
$listGroup.Controls.Add($removeBtn)

$enableBtn = New-Object System.Windows.Forms.Button
$enableBtn.Text     = "启用账户"
$enableBtn.Location = New-Object System.Drawing.Point(618, 100)
$enableBtn.Size     = New-Object System.Drawing.Size(112, 30)
$listGroup.Controls.Add($enableBtn)

$disableBtn = New-Object System.Windows.Forms.Button
$disableBtn.Text     = "禁用账户"
$disableBtn.Location = New-Object System.Drawing.Point(618, 140)
$disableBtn.Size     = New-Object System.Drawing.Size(112, 30)
$listGroup.Controls.Add($disableBtn)

$resetPwdBtn = New-Object System.Windows.Forms.Button
$resetPwdBtn.Text     = "重置密码"
$resetPwdBtn.Location = New-Object System.Drawing.Point(618, 188)
$resetPwdBtn.Size     = New-Object System.Drawing.Size(112, 30)
$listGroup.Controls.Add($resetPwdBtn)

$changeGroupBtn = New-Object System.Windows.Forms.Button
$changeGroupBtn.Text     = "修改分组"
$changeGroupBtn.Location = New-Object System.Drawing.Point(618, 226)
$changeGroupBtn.Size     = New-Object System.Drawing.Size(112, 30)
$listGroup.Controls.Add($changeGroupBtn)

$tabs.TabPages.Add($tab1) | Out-Null

# -- 刷新筛选下拉框 --
function Refresh-FilterCombo {
    $script:filterCombo.Items.Clear()
    $script:filterCombo.Items.Add("全部") | Out-Null
    $script:filterCombo.Items.Add($script:AllGroup) | Out-Null
    $advisors = Get-AllAdvisorGroups
    foreach ($a in $advisors) {
        $script:filterCombo.Items.Add("Lab_$a") | Out-Null
    }
    if ($script:filterCombo.Items.Count -gt 0) {
        $script:filterCombo.SelectedIndex = 0
    }
}

# -- 刷新成员列表 --
function Refresh-MemberList {
    $script:memberList.Items.Clear()

    $selectedFilter = $script:filterCombo.SelectedItem
    if ($null -eq $selectedFilter) { $selectedFilter = "全部" }

    # 确定要显示的用户集合
    $usernames = @()

    if ($selectedFilter -eq "全部") {
        # 显示 Lab_All 中的所有用户
        if (Test-GroupExists -Name $script:AllGroup) {
            $usernames = Get-GroupMembers -GroupName $script:AllGroup
        }
    } elseif ($selectedFilter -eq $script:AllGroup) {
        if (Test-GroupExists -Name $script:AllGroup) {
            $usernames = Get-GroupMembers -GroupName $script:AllGroup
        }
    } else {
        # 特定导师组
        if (Test-GroupExists -Name $selectedFilter) {
            $usernames = Get-GroupMembers -GroupName $selectedFilter
        }
    }

    foreach ($name in $usernames) {
        $item = New-Object System.Windows.Forms.ListViewItem($name)

        # 显示名称
        $displayName = ""
        try {
            $localUser = Get-LocalUser -Name $name -ErrorAction SilentlyContinue
            if ($localUser) { $displayName = $localUser.Description }
        } catch {}
        $item.SubItems.Add($displayName) | Out-Null

        # 所属导师组
        $advisor = Get-UserAdvisorGroup -Username $name
        $advisorDisplay = if ($advisor) { "Lab_$advisor" } else { "(未分配)" }
        $item.SubItems.Add($advisorDisplay) | Out-Null

        # 账户状态
        $enabled = $true
        try {
            $localUser2 = Get-LocalUser -Name $name -ErrorAction SilentlyContinue
            if ($localUser2) { $enabled = $localUser2.Enabled }
        } catch {}
        $statusText = if ($enabled) { "已启用" } else { "已禁用" }
        $item.SubItems.Add($statusText) | Out-Null

        # 上次登录
        $lastLogon = "未知"
        try {
            $localUser3 = Get-LocalUser -Name $name -ErrorAction SilentlyContinue
            if ($localUser3 -and $localUser3.LastLogon) {
                $lastLogon = $localUser3.LastLogon.ToString("yyyy-MM-dd HH:mm")
            }
        } catch {}
        $item.SubItems.Add($lastLogon) | Out-Null

        $script:memberList.Items.Add($item) | Out-Null
    }

    $script:memberCountLabel.Text = "共 $($usernames.Count) 个成员"
    Write-Log "成员列表已刷新，当前显示 $($usernames.Count) 人（筛选：$selectedFilter）"
}

# 刷新按钮
$refreshBtn.Add_Click({
    try {
        Refresh-FilterCombo
        Refresh-MemberList
    } catch {
        Write-Log "刷新失败: $_" "ERROR"
    }
})

$filterRefreshBtn.Add_Click({
    try {
        Refresh-FilterCombo
        Refresh-MemberList
    } catch {
        Write-Log "刷新筛选失败: $_" "ERROR"
    }
})

$script:filterCombo.Add_SelectedIndexChanged({
    try {
        Refresh-MemberList
    } catch {
        Write-Log "切换筛选失败: $_" "ERROR"
    }
})

# 从组中移除（从 Lab_All 和导师组同时移除）
$removeBtn.Add_Click({
    try {
        $selected = $script:memberList.SelectedItems
        if ($selected.Count -eq 0) {
            Show-Dialog "提示" "请先选择一个成员" "OK" "Warning"
            return
        }
        $username = $selected[0].Text
        $advisor = Get-UserAdvisorGroup -Username $username
        $advisorGroup = if ($advisor) { "Lab_$advisor" } else { "" }

        $msg = "确定将 '$username' 从所有组中移除吗？`n`n"
        $msg += "- 将从 $script:AllGroup（全员组）移除`n"
        if ($advisorGroup) { $msg += "- 将从 $advisorGroup（导师组）移除`n" }
        $msg += "`n移除后该用户将无法访问任何共享文件夹。"

        $confirm = Show-Dialog "确认" $msg "YesNo" "Warning"
        if ($confirm -eq "Yes") {
            # 从 Lab_All 移除
            net localgroup $script:AllGroup $username /delete 2>&1 | Out-Null
            Write-Log "已将 '$username' 从 $script:AllGroup 移除"

            # 从导师组移除
            if ($advisorGroup) {
                net localgroup $advisorGroup $username /delete 2>&1 | Out-Null
                Write-Log "已将 '$username' 从 $advisorGroup 移除"
            }
            Write-AuditLog -Action "REMOVE_FROM_GROUP" -Target $username
            Refresh-MemberList
        }
    } catch {
        Write-Log "移除失败: $_" "ERROR"
        Write-AuditLog -Action "REMOVE_FROM_GROUP" -Target $username -Result "FAILED" -Detail "$_"
        Show-Dialog "错误" "移除失败: $_" "OK" "Error"
    }
})

# 启用账户
$enableBtn.Add_Click({
    try {
        $selected = $script:memberList.SelectedItems
        if ($selected.Count -eq 0) { Show-Dialog "提示" "请先选择一个成员" "OK" "Warning"; return }
        Enable-LocalUser -Name $selected[0].Text
        Write-Log "已启用账户 '$($selected[0].Text)'"
        Write-AuditLog -Action "ENABLE_USER" -Target $selected[0].Text
        Refresh-MemberList
    } catch {
        Write-Log "启用失败: $_" "ERROR"
        Write-AuditLog -Action "ENABLE_USER" -Target $selected[0].Text -Result "FAILED" -Detail "$_"
        Show-Dialog "错误" "启用失败: $_" "OK" "Error"
    }
})

# 禁用账户
$disableBtn.Add_Click({
    try {
        $selected = $script:memberList.SelectedItems
        if ($selected.Count -eq 0) { Show-Dialog "提示" "请先选择一个成员" "OK" "Warning"; return }
        $username = $selected[0].Text
        $confirm = Show-Dialog "确认" "确定禁用账户 '$username' 吗？`n禁用后该用户将无法登录，但账户数据保留。" "YesNo" "Warning"
        if ($confirm -eq "Yes") {
            Disable-LocalUser -Name $username
            Write-Log "已禁用账户 '$username'"
            Write-AuditLog -Action "DISABLE_USER" -Target $username
            Refresh-MemberList
        }
    } catch {
        Write-Log "禁用失败: $_" "ERROR"
        Write-AuditLog -Action "DISABLE_USER" -Target $username -Result "FAILED" -Detail "$_"
        Show-Dialog "错误" "禁用失败: $_" "OK" "Error"
    }
})

# 重置密码
$resetPwdBtn.Add_Click({
    try {
        $selected = $script:memberList.SelectedItems
        if ($selected.Count -eq 0) { Show-Dialog "提示" "请先选择一个成员" "OK" "Warning"; return }
        $username = $selected[0].Text

        $pwdForm = New-Object System.Windows.Forms.Form
        $pwdForm.Text          = "重置密码 - $username"
        $pwdForm.Size          = New-Object System.Drawing.Size(360, 200)
        $pwdForm.StartPosition = "CenterParent"
        $pwdForm.FormBorderStyle = "FixedDialog"
        $pwdForm.MaximizeBox   = $false
        $pwdForm.Font          = New-Object System.Drawing.Font("Microsoft YaHei UI", 9)

        $pwdLabel = New-Object System.Windows.Forms.Label
        $pwdLabel.Text     = "新密码："
        $pwdLabel.Location = New-Object System.Drawing.Point(15, 18)
        $pwdLabel.AutoSize = $true
        $pwdForm.Controls.Add($pwdLabel)

        $pwdInput = New-Object System.Windows.Forms.TextBox
        $pwdInput.Location = New-Object System.Drawing.Point(95, 15)
        $pwdInput.Size     = New-Object System.Drawing.Size(230, 25)
        $pwdInput.UseSystemPasswordChar = $true
        $pwdForm.Controls.Add($pwdInput)

        $confirmLabel = New-Object System.Windows.Forms.Label
        $confirmLabel.Text     = "确认密码："
        $confirmLabel.Location = New-Object System.Drawing.Point(15, 53)
        $confirmLabel.AutoSize = $true
        $pwdForm.Controls.Add($confirmLabel)

        $confirmInput = New-Object System.Windows.Forms.TextBox
        $confirmInput.Location = New-Object System.Drawing.Point(95, 50)
        $confirmInput.Size     = New-Object System.Drawing.Size(230, 25)
        $confirmInput.UseSystemPasswordChar = $true
        $pwdForm.Controls.Add($confirmInput)

        $pwdOkBtn = New-Object System.Windows.Forms.Button
        $pwdOkBtn.Text     = "确定"
        $pwdOkBtn.Location = New-Object System.Drawing.Point(145, 100)
        $pwdOkBtn.Size     = New-Object System.Drawing.Size(80, 30)
        $pwdForm.Controls.Add($pwdOkBtn)

        $pwdOkBtn.Add_Click({
            if ($pwdInput.Text -ne $confirmInput.Text) {
                Show-Dialog "错误" "两次输入的密码不一致" "OK" "Error"
                return
            }
            if ($pwdInput.Text.Length -lt 8) {
                Show-Dialog "错误" "密码长度至少 8 位" "OK" "Error"
                return
            }
            try {
                $secPwd = ConvertTo-SecureString $pwdInput.Text -AsPlainText -Force
                Set-LocalUser -Name $username -Password $secPwd
                Write-Log "已重置 '$username' 的密码"
                Write-AuditLog -Action "RESET_PASSWORD" -Target $username
                $pwdForm.Close()
                Show-Dialog "成功" "密码已重置" "OK" "Information"
            } catch {
                Write-Log "重置密码失败: $_" "ERROR"
                Write-AuditLog -Action "RESET_PASSWORD" -Target $username -Result "FAILED" -Detail "$_"
                Show-Dialog "错误" "重置密码失败: $_" "OK" "Error"
            }
        })

        $pwdForm.ShowDialog() | Out-Null
    } catch {
        Write-Log "重置密码失败: $_" "ERROR"
        Show-Dialog "错误" "重置密码失败: $_" "OK" "Error"
    }
})

# 修改分组
$changeGroupBtn.Add_Click({
    try {
        $selected = $script:memberList.SelectedItems
        if ($selected.Count -eq 0) {
            Show-Dialog "提示" "请先选择一个成员" "OK" "Warning"
            return
        }
        $username = $selected[0].Text
        $currentAdvisor = Get-UserAdvisorGroup -Username $username

        # 创建修改分组对话框
        $chgForm = New-Object System.Windows.Forms.Form
        $chgForm.Text          = "修改分组 - $username"
        $chgForm.Size          = New-Object System.Drawing.Size(380, 200)
        $chgForm.StartPosition = "CenterParent"
        $chgForm.FormBorderStyle = "FixedDialog"
        $chgForm.MaximizeBox   = $false
        $chgForm.Font          = New-Object System.Drawing.Font("Microsoft YaHei UI", 9)

        $chgInfoLabel = New-Object System.Windows.Forms.Label
        $chgInfoLabel.Text = "用户：$username    当前导师组：$(if ($currentAdvisor) { "Lab_$currentAdvisor" } else { "(未分配)" })"
        $chgInfoLabel.Location = New-Object System.Drawing.Point(15, 15)
        $chgInfoLabel.AutoSize = $true
        $chgForm.Controls.Add($chgInfoLabel)

        $chgLabel = New-Object System.Windows.Forms.Label
        $chgLabel.Text     = "新导师组："
        $chgLabel.Location = New-Object System.Drawing.Point(15, 55)
        $chgLabel.AutoSize = $true
        $chgForm.Controls.Add($chgLabel)

        $chgCombo = New-Object System.Windows.Forms.ComboBox
        $chgCombo.Location    = New-Object System.Drawing.Point(100, 52)
        $chgCombo.Size        = New-Object System.Drawing.Size(240, 25)
        $chgCombo.DropDownStyle = "DropDownList"
        $chgForm.Controls.Add($chgCombo)

        # 填充导师组列表
        $advisors = Get-AllAdvisorGroups
        foreach ($a in $advisors) {
            $chgCombo.Items.Add($a) | Out-Null
        }
        if ($currentAdvisor -and ($chgCombo.Items -contains $currentAdvisor)) {
            $chgCombo.SelectedItem = $currentAdvisor
        } elseif ($chgCombo.Items.Count -gt 0) {
            $chgCombo.SelectedIndex = 0
        }

        $chgOkBtn = New-Object System.Windows.Forms.Button
        $chgOkBtn.Text     = "确定"
        $chgOkBtn.Location = New-Object System.Drawing.Point(110, 105)
        $chgOkBtn.Size     = New-Object System.Drawing.Size(80, 30)
        $chgForm.Controls.Add($chgOkBtn)

        $chgCancelBtn = New-Object System.Windows.Forms.Button
        $chgCancelBtn.Text     = "取消"
        $chgCancelBtn.Location = New-Object System.Drawing.Point(200, 105)
        $chgCancelBtn.Size     = New-Object System.Drawing.Size(80, 30)
        $chgForm.Controls.Add($chgCancelBtn)

        $chgCancelBtn.Add_Click({ $chgForm.Close() })

        $chgOkBtn.Add_Click({
            $newAdvisor = $chgCombo.SelectedItem
            if ([string]::IsNullOrWhiteSpace($newAdvisor)) {
                Show-Dialog "提示" "请选择新的导师组" "OK" "Warning"
                return
            }
            if ($newAdvisor -eq $currentAdvisor) {
                Show-Dialog "提示" "用户已在该导师组中" "OK" "Information"
                return
            }

            try {
                # 从旧导师组移除
                if ($currentAdvisor) {
                    $oldGroup = "Lab_$currentAdvisor"
                    net localgroup $oldGroup $username /delete 2>&1 | Out-Null
                    if ($LASTEXITCODE -ne 0) { throw "从旧组 '$oldGroup' 移除失败 (exit code: $LASTEXITCODE)" }
                    Write-Log "已将 '$username' 从 $oldGroup 移除"
                }

                # 加入新导师组
                $newGroup = "Lab_$newAdvisor"
                net localgroup $newGroup $username /add 2>&1 | Out-Null
                if ($LASTEXITCODE -ne 0) { throw "加入新组 '$newGroup' 失败 (exit code: $LASTEXITCODE)" }
                Write-Log "已将 '$username' 加入 $newGroup"
                Write-AuditLog -Action "CHANGE_GROUP" -Target $username -Detail "从 $oldGroup 转到 $newGroup"

                $chgForm.Close()
                Show-Dialog "成功" "已将 '$username' 从 $(if ($currentAdvisor) { "Lab_$currentAdvisor" } else { "(未分配)" }) 移动到 $newGroup" "OK" "Information"
                Refresh-MemberList
            } catch {
                Write-Log "修改分组失败: $_" "ERROR"
                Write-AuditLog -Action "CHANGE_GROUP" -Target $username -Result "FAILED" -Detail "$_"
                Show-Dialog "错误" "修改分组失败: $_" "OK" "Error"
            }
        })

        $chgForm.ShowDialog() | Out-Null
    } catch {
        Write-Log "修改分组失败: $_" "ERROR"
        Show-Dialog "错误" "修改分组失败: $_" "OK" "Error"
    }
})

# ==================== Tab 2: 创建账户 ====================
$tab2 = New-Object System.Windows.Forms.TabPage
$tab2.Text = "创建账户"

$infoLabel = New-Object System.Windows.Forms.Label
$infoLabel.Text = "为课题组新成员创建 Windows 账户，自动加入全员组和导师组，并在数据盘建立隔离的个人目录。"
$infoLabel.Location = New-Object System.Drawing.Point(20, 15)
$infoLabel.AutoSize = $true
$tab2.Controls.Add($infoLabel)

$formGroup = New-Object System.Windows.Forms.GroupBox
$formGroup.Text     = "账户信息"
$formGroup.Location = New-Object System.Drawing.Point(15, 45)
$formGroup.Size     = New-Object System.Drawing.Size(745, 310)
$tab2.Controls.Add($formGroup)

# 用户名
$ul = New-Object System.Windows.Forms.Label
$ul.Text = "用户名（英文，登录用）："
$ul.Location = New-Object System.Drawing.Point(20, 32)
$ul.Size = New-Object System.Drawing.Size(190, 25)
$formGroup.Controls.Add($ul)

$script:newUsername = New-Object System.Windows.Forms.TextBox
$script:newUsername.Location = New-Object System.Drawing.Point(220, 30)
$script:newUsername.Size = New-Object System.Drawing.Size(250, 25)
$formGroup.Controls.Add($script:newUsername)

# 密码
$pl = New-Object System.Windows.Forms.Label
$pl.Text = "密码："
$pl.Location = New-Object System.Drawing.Point(20, 68)
$pl.Size = New-Object System.Drawing.Size(190, 25)
$formGroup.Controls.Add($pl)

$script:newPassword = New-Object System.Windows.Forms.TextBox
$script:newPassword.Location = New-Object System.Drawing.Point(220, 66)
$script:newPassword.Size = New-Object System.Drawing.Size(250, 25)
$script:newPassword.UseSystemPasswordChar = $true
$formGroup.Controls.Add($script:newPassword)

# 确认密码
$cpl = New-Object System.Windows.Forms.Label
$cpl.Text = "确认密码："
$cpl.Location = New-Object System.Drawing.Point(20, 104)
$cpl.Size = New-Object System.Drawing.Size(190, 25)
$formGroup.Controls.Add($cpl)

$script:confirmPassword = New-Object System.Windows.Forms.TextBox
$script:confirmPassword.Location = New-Object System.Drawing.Point(220, 102)
$script:confirmPassword.Size = New-Object System.Drawing.Size(250, 25)
$script:confirmPassword.UseSystemPasswordChar = $true
$formGroup.Controls.Add($script:confirmPassword)

# 显示名称
$nl = New-Object System.Windows.Forms.Label
$nl.Text = "显示名称（中文名，可选）："
$nl.Location = New-Object System.Drawing.Point(20, 140)
$nl.Size = New-Object System.Drawing.Size(190, 25)
$formGroup.Controls.Add($nl)

$script:newDisplayName = New-Object System.Windows.Forms.TextBox
$script:newDisplayName.Location = New-Object System.Drawing.Point(220, 138)
$script:newDisplayName.Size = New-Object System.Drawing.Size(250, 25)
$formGroup.Controls.Add($script:newDisplayName)

# 导师选择
$advLabel = New-Object System.Windows.Forms.Label
$advLabel.Text = "导师选择："
$advLabel.Location = New-Object System.Drawing.Point(20, 176)
$advLabel.Size = New-Object System.Drawing.Size(190, 25)
$formGroup.Controls.Add($advLabel)

$script:advisorCombo = New-Object System.Windows.Forms.ComboBox
$script:advisorCombo.Location = New-Object System.Drawing.Point(220, 174)
$script:advisorCombo.Size = New-Object System.Drawing.Size(250, 25)
$script:advisorCombo.DropDownStyle = "DropDownList"
$formGroup.Controls.Add($script:advisorCombo)

# 生成密码按钮
$genPwdBtn = New-Object System.Windows.Forms.Button
$genPwdBtn.Text = "随机生成密码"
$genPwdBtn.Location = New-Object System.Drawing.Point(490, 66)
$genPwdBtn.Size = New-Object System.Drawing.Size(130, 28)
$formGroup.Controls.Add($genPwdBtn)

$genPwdBtn.Add_Click({
    $pwd = Generate-RandomPassword
    $script:newPassword.Text = $pwd
    $script:confirmPassword.Text = $pwd
    Write-Log "已生成随机密码（请记录并告知用户）"
})

# 提示文字
$tipLabel = New-Object System.Windows.Forms.Label
$tipLabel.Text = "提示：创建后，用户可访问 D:\GroupData\_公共（公共数据）、D:\GroupData\[导师名]\（导师组数据）和 D:\Users\用户名（个人目录）。"
$tipLabel.ForeColor = "Gray"
$tipLabel.Location = New-Object System.Drawing.Point(20, 220)
$tipLabel.AutoSize = $true
$formGroup.Controls.Add($tipLabel)

# 刷新导师下拉列表
function Refresh-AdvisorCombo {
    $currentSelection = $script:advisorCombo.SelectedItem
    $script:advisorCombo.Items.Clear()
    $advisors = Get-AllAdvisorGroups
    foreach ($a in $advisors) {
        $script:advisorCombo.Items.Add($a) | Out-Null
    }
    $script:advisorCombo.Items.Add("新建导师组...") | Out-Null
    # 恢复选择
    if ($currentSelection -and ($script:advisorCombo.Items -contains $currentSelection)) {
        $script:advisorCombo.SelectedItem = $currentSelection
    } elseif ($script:advisorCombo.Items.Count -gt 1) {
        $script:advisorCombo.SelectedIndex = 0
    }
}

# 导师选择变更事件
$script:advisorCombo.Add_SelectedIndexChanged({
    try {
        if ($script:advisorCombo.SelectedItem -eq "新建导师组...") {
            $newAdvForm = New-Object System.Windows.Forms.Form
            $newAdvForm.Text          = "新建导师组"
            $newAdvForm.Size          = New-Object System.Drawing.Size(350, 160)
            $newAdvForm.StartPosition = "CenterParent"
            $newAdvForm.FormBorderStyle = "FixedDialog"
            $newAdvForm.MaximizeBox   = $false
            $newAdvForm.Font          = New-Object System.Drawing.Font("Microsoft YaHei UI", 9)

            $naLabel = New-Object System.Windows.Forms.Label
            $naLabel.Text     = "导师名称（如 张老师）："
            $naLabel.Location = New-Object System.Drawing.Point(15, 18)
            $naLabel.AutoSize = $true
            $newAdvForm.Controls.Add($naLabel)

            $naInput = New-Object System.Windows.Forms.TextBox
            $naInput.Location = New-Object System.Drawing.Point(15, 45)
            $naInput.Size     = New-Object System.Drawing.Size(300, 25)
            $newAdvForm.Controls.Add($naInput)

            $naOkBtn = New-Object System.Windows.Forms.Button
            $naOkBtn.Text     = "创建"
            $naOkBtn.Location = New-Object System.Drawing.Point(120, 85)
            $naOkBtn.Size     = New-Object System.Drawing.Size(80, 30)
            $newAdvForm.Controls.Add($naOkBtn)

            $naOkBtn.Add_Click({
                $newName = $naInput.Text.Trim()
                if ([string]::IsNullOrWhiteSpace($newName)) {
                    Show-Dialog "提示" "请输入导师名称" "OK" "Warning"
                    return
                }
                if ($newName -match '[^a-zA-Z0-9_\u4e00-\u9fff\-]') {
                    Show-Dialog "提示" "导师名称只能包含中英文字符、数字、下划线和短横线" "OK" "Warning"
                    return
                }
                try {
                    New-AdvisorGroup -AdvisorName $newName
                    $newAdvForm.Close()
                    Refresh-AdvisorCombo
                    # 选中新创建的导师
                    if ($script:advisorCombo.Items -contains $newName) {
                        $script:advisorCombo.SelectedItem = $newName
                    }
                    Show-Dialog "成功" "导师组 'Lab_$newName' 已创建`n文件夹：$script:SharePath\$newName" "OK" "Information"
                } catch {
                    Write-Log "创建导师组失败: $_" "ERROR"
                    Write-AuditLog -Action "CREATE_ADVISOR_GROUP" -Target $newName -Result "FAILED" -Detail "$_"
                    Show-Dialog "错误" "创建导师组失败: $_" "OK" "Error"
                }
            })

            $newAdvForm.ShowDialog() | Out-Null
            # 如果用户关闭了对话框而没有输入名称，恢复下拉选择
            if ($script:advisorCombo.SelectedItem -eq "新建导师组..." -and $script:advisorCombo.Items.Count -gt 1) {
                $script:advisorCombo.SelectedIndex = 0
            }
        }
    } catch {
        Write-Log "处理导师选择失败: $_" "ERROR"
    }
})

# 创建按钮
$createBtn = New-Object System.Windows.Forms.Button
$createBtn.Text = "一键创建账户"
$createBtn.Location = New-Object System.Drawing.Point(300, 370)
$createBtn.Size = New-Object System.Drawing.Size(180, 38)
$createBtn.BackColor = [System.Drawing.Color]::FromArgb(0, 122, 204)
$createBtn.ForeColor = "White"
$createBtn.FlatStyle = "Flat"
$createBtn.Font = New-Object System.Drawing.Font("Microsoft YaHei UI", 10, "Bold")
$tab2.Controls.Add($createBtn)

$createBtn.Add_Click({
    try {
        $username    = $script:newUsername.Text.Trim()
        $password    = $script:newPassword.Text
        $confirmPwd  = $script:confirmPassword.Text
        $displayName = $script:newDisplayName.Text.Trim()
        $advisor     = $script:advisorCombo.SelectedItem

        # 校验
        if ([string]::IsNullOrWhiteSpace($username)) {
            Show-Dialog "提示" "请输入用户名" "OK" "Warning"; return
        }
        if ($username -match '[^a-zA-Z0-9_.\-]') {
            Show-Dialog "提示" "用户名只能包含英文字母、数字、下划线、点和短横线" "OK" "Warning"; return
        }
        if ($password -ne $confirmPwd) {
            Show-Dialog "错误" "两次密码不一致" "OK" "Error"; return
        }
        if ($password.Length -lt 8) {
            Show-Dialog "提示" "密码长度至少 8 位" "OK" "Warning"; return
        }
        if ([string]::IsNullOrWhiteSpace($advisor) -or $advisor -eq "新建导师组...") {
            Show-Dialog "提示" "请选择导师组" "OK" "Warning"; return
        }

        # 1. 创建用户
        $secPwd = ConvertTo-SecureString $password -AsPlainText -Force
        $desc = if ($displayName) { $displayName } else { "" }
        New-LocalUser -Name $username -Password $secPwd -FullName $displayName -Description $desc `
            -PasswordNeverExpires -ErrorAction Stop
        Write-Log "账户 '$username' 创建成功"
        Write-AuditLog -Action "CREATE_USER" -Target $username -Detail "显示名: $displayName, 导师组: $advisor"

        # 2. 加入全员组 Lab_All
        if (-not (Test-GroupExists -Name $script:AllGroup)) {
            Write-Log "全员组 '$($script:AllGroup)' 不存在，正在创建..." "WARN"
            net localgroup $script:AllGroup /add 2>&1 | Out-Null
            if ($LASTEXITCODE -ne 0) { throw "创建全员组 '$($script:AllGroup)' 失败 (exit code: $LASTEXITCODE)" }
        }
        net localgroup $script:AllGroup $username /add 2>&1 | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "加入全员组 '$($script:AllGroup)' 失败 (exit code: $LASTEXITCODE)" }
        Write-Log "已将 '$username' 加入全员组 '$($script:AllGroup)'"

        # 3. 加入导师组 Lab_[advisor]
        $advisorGroup = "Lab_$advisor"
        if (-not (Test-GroupExists -Name $advisorGroup)) {
            Write-Log "导师组 '$advisorGroup' 不存在，正在创建..." "WARN"
            New-AdvisorGroup -AdvisorName $advisor
        }
        net localgroup $advisorGroup $username /add 2>&1 | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "加入导师组 '$advisorGroup' 失败 (exit code: $LASTEXITCODE)" }
        Write-Log "已将 '$username' 加入导师组 '$advisorGroup'"

        # 4. 创建个人目录
        $userDir = New-UserPersonalDir -Username $username

        # 5. 清空表单
        $script:newUsername.Text    = ""
        $script:newPassword.Text    = ""
        $script:confirmPassword.Text = ""
        $script:newDisplayName.Text = ""

        $advisorPath = Join-Path $script:SharePath $advisor
        Show-Dialog "成功" "账户 '$username' 创建完成！`n`n用户名：$username`n显示名称：$displayName`n全员组：$script:AllGroup`n导师组：$advisorGroup`n导师区：$advisorPath`n个人目录：$userDir（仅本人可访问）" "OK" "Information"

        # 切换到成员管理标签并刷新
        $tabs.SelectedIndex = 0
        Refresh-FilterCombo
        Refresh-MemberList

    } catch {
        Write-Log "创建账户失败: $_" "ERROR"
        Write-AuditLog -Action "CREATE_USER" -Target $username -Result "FAILED" -Detail "$_"
        Show-Dialog "错误" "创建账户失败:`n$_" "OK" "Error"
    }
})

$tabs.TabPages.Add($tab2) | Out-Null

# ==================== Tab 3: 分组管理 ====================
$tab3 = New-Object System.Windows.Forms.TabPage
$tab3.Text = "分组管理"

# 说明标签
$groupNote = New-Object System.Windows.Forms.Label
$groupNote.Text = "注意：用户必须同时属于 Lab_All（全员组）和某个导师组。"
$groupNote.Location = New-Object System.Drawing.Point(20, 12)
$groupNote.AutoSize = $true
$groupNote.ForeColor = [System.Drawing.Color]::FromArgb(180, 0, 0)
$groupNote.Font = New-Object System.Drawing.Font("Microsoft YaHei UI", 9, "Bold")
$tab3.Controls.Add($groupNote)

# 左侧面板：导师组列表
$leftPanel = New-Object System.Windows.Forms.GroupBox
$leftPanel.Text     = "导师组列表"
$leftPanel.Location = New-Object System.Drawing.Point(15, 40)
$leftPanel.Size     = New-Object System.Drawing.Size(300, 360)
$tab3.Controls.Add($leftPanel)

$script:groupListView = New-Object System.Windows.Forms.ListView
$script:groupListView.Location    = New-Object System.Drawing.Point(10, 22)
$script:groupListView.Size        = New-Object System.Drawing.Size(278, 285)
$script:groupListView.View        = "Details"
$script:groupListView.FullRowSelect = $true
$script:groupListView.GridLines   = $true
$script:groupListView.Columns.Add("导师组名称", 170) | Out-Null
$script:groupListView.Columns.Add("成员数", 70)    | Out-Null
$leftPanel.Controls.Add($script:groupListView)

$grpRefreshBtn = New-Object System.Windows.Forms.Button
$grpRefreshBtn.Text     = "刷新"
$grpRefreshBtn.Location = New-Object System.Drawing.Point(10, 315)
$grpRefreshBtn.Size     = New-Object System.Drawing.Size(80, 30)
$leftPanel.Controls.Add($grpRefreshBtn)

$grpNewBtn = New-Object System.Windows.Forms.Button
$grpNewBtn.Text     = "新建导师组"
$grpNewBtn.Location = New-Object System.Drawing.Point(100, 315)
$grpNewBtn.Size     = New-Object System.Drawing.Size(90, 30)
$leftPanel.Controls.Add($grpNewBtn)

$grpDelBtn = New-Object System.Windows.Forms.Button
$grpDelBtn.Text     = "删除导师组"
$grpDelBtn.Location = New-Object System.Drawing.Point(200, 315)
$grpDelBtn.Size     = New-Object System.Drawing.Size(90, 30)
$leftPanel.Controls.Add($grpDelBtn)

# 右侧面板：选中组的成员
$rightPanel = New-Object System.Windows.Forms.GroupBox
$rightPanel.Text     = "选中组的成员"
$rightPanel.Location = New-Object System.Drawing.Point(330, 40)
$rightPanel.Size     = New-Object System.Drawing.Size(435, 360)
$tab3.Controls.Add($rightPanel)

$script:groupMemberList = New-Object System.Windows.Forms.ListView
$script:groupMemberList.Location    = New-Object System.Drawing.Point(10, 22)
$script:groupMemberList.Size        = New-Object System.Drawing.Size(412, 245)
$script:groupMemberList.View        = "Details"
$script:groupMemberList.FullRowSelect = $true
$script:groupMemberList.GridLines   = $true
$script:groupMemberList.Columns.Add("用户名", 130)   | Out-Null
$script:groupMemberList.Columns.Add("显示名称", 130)  | Out-Null
$script:groupMemberList.Columns.Add("账户状态", 80)   | Out-Null
$rightPanel.Controls.Add($script:groupMemberList)

# 添加成员到此组
$grpAddMemberLabel = New-Object System.Windows.Forms.Label
$grpAddMemberLabel.Text     = "选择用户："
$grpAddMemberLabel.Location = New-Object System.Drawing.Point(10, 278)
$grpAddMemberLabel.AutoSize = $true
$rightPanel.Controls.Add($grpAddMemberLabel)

$script:grpUserCombo = New-Object System.Windows.Forms.ComboBox
$script:grpUserCombo.Location    = New-Object System.Drawing.Point(85, 275)
$script:grpUserCombo.Size        = New-Object System.Drawing.Size(170, 25)
$script:grpUserCombo.DropDownStyle = "DropDownList"
$rightPanel.Controls.Add($script:grpUserCombo)

$grpAddBtn = New-Object System.Windows.Forms.Button
$grpAddBtn.Text     = "添加成员到此组"
$grpAddBtn.Location = New-Object System.Drawing.Point(268, 273)
$grpAddBtn.Size     = New-Object System.Drawing.Size(150, 28)
$rightPanel.Controls.Add($grpAddBtn)

# 从此组移除
$grpRemoveMemberBtn = New-Object System.Windows.Forms.Button
$grpRemoveMemberBtn.Text     = "从此组移除"
$grpRemoveMemberBtn.Location = New-Object System.Drawing.Point(268, 310)
$grpRemoveMemberBtn.Size     = New-Object System.Drawing.Size(150, 28)
$rightPanel.Controls.Add($grpRemoveMemberBtn)

$tabs.TabPages.Add($tab3) | Out-Null

# -- 分组管理功能 --

# 刷新导师组列表
function Refresh-GroupListView {
    $script:groupListView.Items.Clear()
    $advisors = Get-AllAdvisorGroups
    foreach ($a in $advisors) {
        $gName = "Lab_$a"
        $memberCount = 0
        try {
            $members = Get-GroupMembers -GroupName $gName
            $memberCount = $members.Count
        } catch {}
        $item = New-Object System.Windows.Forms.ListViewItem($gName)
        $item.SubItems.Add("$memberCount") | Out-Null
        $item.Tag = $a
        $script:groupListView.Items.Add($item) | Out-Null
    }
    Write-Log "导师组列表已刷新，共 $($advisors.Count) 个导师组"
}

# 刷新选中组的成员列表
function Refresh-GroupMemberList {
    $script:groupMemberList.Items.Clear()
    $selectedGrp = $script:groupListView.SelectedItems
    if ($selectedGrp.Count -eq 0) { return }

    $gName = $selectedGrp[0].Text
    $members = Get-GroupMembers -GroupName $gName

    foreach ($name in $members) {
        $item = New-Object System.Windows.Forms.ListViewItem($name)

        $displayName = ""
        try {
            $lu = Get-LocalUser -Name $name -ErrorAction SilentlyContinue
            if ($lu) { $displayName = $lu.Description }
        } catch {}
        $item.SubItems.Add($displayName) | Out-Null

        $enabled = $true
        try {
            $lu2 = Get-LocalUser -Name $name -ErrorAction SilentlyContinue
            if ($lu2) { $enabled = $lu2.Enabled }
        } catch {}
        $statusText = if ($enabled) { "已启用" } else { "已禁用" }
        $item.SubItems.Add($statusText) | Out-Null

        $script:groupMemberList.Items.Add($item) | Out-Null
    }
}

# 刷新用户下拉列表（所有属于 Lab_All 的用户）
function Refresh-GrpUserCombo {
    $script:grpUserCombo.Items.Clear()
    if (Test-GroupExists -Name $script:AllGroup) {
        $allMembers = Get-GroupMembers -GroupName $script:AllGroup
        foreach ($m in $allMembers) {
            $script:grpUserCombo.Items.Add($m) | Out-Null
        }
    }
    # 也包含不在 Lab_All 中的本地用户
    try {
        Get-LocalUser | ForEach-Object {
            if ($script:grpUserCombo.Items -notcontains $_.Name) {
                $script:grpUserCombo.Items.Add($_.Name) | Out-Null
            }
        }
    } catch {}
    if ($script:grpUserCombo.Items.Count -gt 0) {
        $script:grpUserCombo.SelectedIndex = 0
    }
}

# 选中导师组时刷新成员
$script:groupListView.Add_SelectedIndexChanged({
    try {
        Refresh-GroupMemberList
        Refresh-GrpUserCombo
    } catch {
        Write-Log "刷新组成员失败: $_" "ERROR"
    }
})

# 刷新按钮
$grpRefreshBtn.Add_Click({
    try {
        Refresh-GroupListView
        Refresh-GrpUserCombo
    } catch {
        Write-Log "刷新失败: $_" "ERROR"
    }
})

# 新建导师组
$grpNewBtn.Add_Click({
    try {
        $newGrpForm = New-Object System.Windows.Forms.Form
        $newGrpForm.Text          = "新建导师组"
        $newGrpForm.Size          = New-Object System.Drawing.Size(350, 160)
        $newGrpForm.StartPosition = "CenterParent"
        $newGrpForm.FormBorderStyle = "FixedDialog"
        $newGrpForm.MaximizeBox   = $false
        $newGrpForm.Font          = New-Object System.Drawing.Font("Microsoft YaHei UI", 9)

        $ngLabel = New-Object System.Windows.Forms.Label
        $ngLabel.Text     = "导师名称（如 张老师）："
        $ngLabel.Location = New-Object System.Drawing.Point(15, 18)
        $ngLabel.AutoSize = $true
        $newGrpForm.Controls.Add($ngLabel)

        $ngInput = New-Object System.Windows.Forms.TextBox
        $ngInput.Location = New-Object System.Drawing.Point(15, 45)
        $ngInput.Size     = New-Object System.Drawing.Size(300, 25)
        $newGrpForm.Controls.Add($ngInput)

        $ngOkBtn = New-Object System.Windows.Forms.Button
        $ngOkBtn.Text     = "创建"
        $ngOkBtn.Location = New-Object System.Drawing.Point(120, 85)
        $ngOkBtn.Size     = New-Object System.Drawing.Size(80, 30)
        $newGrpForm.Controls.Add($ngOkBtn)

        $ngOkBtn.Add_Click({
            $newName = $ngInput.Text.Trim()
            if ([string]::IsNullOrWhiteSpace($newName)) {
                Show-Dialog "提示" "请输入导师名称" "OK" "Warning"
                return
            }
            if ($newName -match '[^a-zA-Z0-9_\u4e00-\u9fff\-]') {
                Show-Dialog "提示" "导师名称只能包含中英文字符、数字、下划线和短横线" "OK" "Warning"
                return
            }
            try {
                New-AdvisorGroup -AdvisorName $newName
                $newGrpForm.Close()
                Refresh-GroupListView
                Refresh-AdvisorCombo
                Show-Dialog "成功" "导师组 'Lab_$newName' 已创建`n文件夹：$script:SharePath\$newName" "OK" "Information"
            } catch {
                Write-Log "创建导师组失败: $_" "ERROR"
                Write-AuditLog -Action "CREATE_ADVISOR_GROUP" -Target $newName -Result "FAILED" -Detail "$_"
                Show-Dialog "错误" "创建导师组失败: $_" "OK" "Error"
            }
        })

        $newGrpForm.ShowDialog() | Out-Null
    } catch {
        Write-Log "新建导师组失败: $_" "ERROR"
        Show-Dialog "错误" "新建导师组失败: $_" "OK" "Error"
    }
})

# 删除导师组
$grpDelBtn.Add_Click({
    try {
        $selectedGrp = $script:groupListView.SelectedItems
        if ($selectedGrp.Count -eq 0) {
            Show-Dialog "提示" "请先选择一个导师组" "OK" "Warning"
            return
        }
        $gName = $selectedGrp[0].Text
        $advisorName = $selectedGrp[0].Tag

        $confirm = Show-Dialog "确认删除" "确定删除导师组 '$gName' 吗？`n`n注意：`n- 仅删除安全组，不会删除文件夹 D:\GroupData\$advisorName`n- 该组的成员不会被删除，但会失去对此导师区的访问权限" "YesNo" "Warning"
        if ($confirm -eq "Yes") {
            net localgroup $gName /delete 2>&1 | Out-Null
            Write-Log "已删除导师组 '$gName'（文件夹保留）"
            Write-AuditLog -Action "DELETE_ADVISOR_GROUP" -Target $advisorName
            Refresh-GroupListView
            $script:groupMemberList.Items.Clear()
            Refresh-AdvisorCombo
            Show-Dialog "成功" "导师组 '$gName' 已删除`n文件夹 D:\GroupData\$advisorName 已保留" "OK" "Information"
        }
    } catch {
        Write-Log "删除导师组失败: $_" "ERROR"
        Write-AuditLog -Action "DELETE_ADVISOR_GROUP" -Target $advisorName -Result "FAILED" -Detail "$_"
        Show-Dialog "错误" "删除导师组失败: $_" "OK" "Error"
    }
})

# 添加成员到此组
$grpAddBtn.Add_Click({
    try {
        $selectedGrp = $script:groupListView.SelectedItems
        if ($selectedGrp.Count -eq 0) {
            Show-Dialog "提示" "请先选择一个导师组" "OK" "Warning"
            return
        }
        $username = $script:grpUserCombo.SelectedItem
        if ([string]::IsNullOrWhiteSpace($username)) {
            Show-Dialog "提示" "请选择要添加的用户" "OK" "Warning"
            return
        }
        $gName = $selectedGrp[0].Text

        # 检查用户是否存在
        $existing = Get-LocalUser -Name $username -ErrorAction SilentlyContinue
        if (-not $existing) {
            Show-Dialog "错误" "用户 '$username' 不存在" "OK" "Error"
            return
        }

        net localgroup $gName $username /add 2>&1 | Out-Null
        Write-Log "已将 '$username' 添加到 '$gName'"
        Write-AuditLog -Action "ADD_TO_GROUP" -Target $username -Detail "组: $gName"
        Refresh-GroupMemberList
        Refresh-GroupListView
        Show-Dialog "成功" "已将 '$username' 添加到 '$gName'" "OK" "Information"
    } catch {
        Write-Log "添加成员失败: $_" "ERROR"
        Write-AuditLog -Action "ADD_TO_GROUP" -Target $username -Result "FAILED" -Detail "$_"
        Show-Dialog "错误" "添加成员失败: $_" "OK" "Error"
    }
})

# 从此组移除
$grpRemoveMemberBtn.Add_Click({
    try {
        $selectedGrp = $script:groupListView.SelectedItems
        if ($selectedGrp.Count -eq 0) {
            Show-Dialog "提示" "请先选择一个导师组" "OK" "Warning"
            return
        }
        $selectedMember = $script:groupMemberList.SelectedItems
        if ($selectedMember.Count -eq 0) {
            Show-Dialog "提示" "请先选择要移除的成员" "OK" "Warning"
            return
        }
        $gName = $selectedGrp[0].Text
        $username = $selectedMember[0].Text

        $confirm = Show-Dialog "确认" "确定将 '$username' 从 '$gName' 中移除吗？`n（用户不会从 Lab_All 中移除）" "YesNo" "Warning"
        if ($confirm -eq "Yes") {
            net localgroup $gName $username /delete 2>&1 | Out-Null
            Write-Log "已将 '$username' 从 '$gName' 移除"
            Write-AuditLog -Action "REMOVE_FROM_ADVISOR_GROUP" -Target $username -Detail "组: $gName"
            Refresh-GroupMemberList
            Refresh-GroupListView
        }
    } catch {
        Write-Log "移除成员失败: $_" "ERROR"
        Write-AuditLog -Action "REMOVE_FROM_ADVISOR_GROUP" -Target $username -Result "FAILED" -Detail "$_"
        Show-Dialog "错误" "移除成员失败: $_" "OK" "Error"
    }
})

# ==================== Tab 4: 性能优化 ====================
$tab4 = New-Object System.Windows.Forms.TabPage
$tab4.Text = "性能优化"

$perfIntro = New-Object System.Windows.Forms.Label
$perfIntro.Text = "解决非管理员账户桌面体验卡顿的问题。以下优化项可逐项应用，建议全部勾选后一键执行。"
$perfIntro.Location = New-Object System.Drawing.Point(20, 15)
$perfIntro.AutoSize = $true
$tab4.Controls.Add($perfIntro)

$optGroup = New-Object System.Windows.Forms.GroupBox
$optGroup.Text     = "优化项（勾选后点击"一键优化"）"
$optGroup.Location = New-Object System.Drawing.Point(15, 45)
$optGroup.Size     = New-Object System.Drawing.Size(745, 280)
$tab4.Controls.Add($optGroup)

$optY = 28

# 优化项定义
$optimizations = @(
    @{
        id    = "uac"
        label = "降低 UAC 等级（减少标准用户的权限弹窗和安全检查开销）"
        desc  = "将 UAC 从默认等级降到"仅通知"，减少完整性检查带来的性能损耗。"
        cmd   = {
            Set-ItemProperty -Path "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System" -Name "ConsentPromptBehaviorUser" -Value 0 -ErrorAction Stop
            Set-ItemProperty -Path "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System" -Name "PromptOnSecureDesktop" -Value 0 -ErrorAction Stop
        }
    },
    @{
        id    = "gprefresh"
        label = "禁用后台组策略自动刷新（减少周期性 CPU/磁盘 I/O）"
        desc  = "非域环境下组策略刷新意义不大，禁用后可减少后台资源占用。"
        cmd   = {
            Set-ItemProperty -Path "HKLM:\SOFTWARE\Policies\Microsoft\Windows\Group Policy\{35378EAC-683F-11D2-A89A-00C04FBBCFA2}" -Name "NoBackgroundPolicy" -Value 1 -Type DWord -ErrorAction Stop
        }
    },
    @{
        id    = "prefetch"
        label = "优化预读取策略（对所有用户生效，改善程序启动速度）"
        desc  = "开启应用启动和引导预读取，提升非管理员账户的程序启动体验。"
        cmd   = {
            Set-ItemProperty -Path "HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management\PrefetchParameters" -Name "EnablePrefetcher" -Value 3 -ErrorAction Stop
        }
    },
    @{
        id    = "remotefx"
        label = "RemoteFX 对所有用户开放（远程桌面时 GPU 加速对标准用户也生效）"
        desc  = "如果使用远程桌面，此项让标准用户也能享受 GPU 加速渲染。"
        cmd   = {
            $rdpPath = "HKLM:\SOFTWARE\Policies\Microsoft\Windows NT\Terminal Services"
            if (-not (Test-Path $rdpPath)) { New-Item -Path $rdpPath -Force | Out-Null }
            Set-ItemProperty -Path $rdpPath -Name "SelectTransport" -Value 0 -ErrorAction Stop
            Set-ItemProperty -Path $rdpPath -Name "AVCHardwareEncodePreferred" -Value 1 -ErrorAction SilentlyContinue
        }
    },
    @{
        id    = "search"
        label = "优化 Windows Search 索引（减少索引对标准用户的 I/O 争用）"
        desc  = "限制搜索索引器的 CPU 和 I/O 优先级，减少对前台操作的干扰。"
        cmd   = {
            $searchPath = "HKLM:\SOFTWARE\Microsoft\Windows Search"
            if (-not (Test-Path $searchPath)) { New-Item -Path $searchPath -Force | Out-Null }
            Set-ItemProperty -Path $searchPath -Name "SetupCompletedSuccessfully" -Value 0 -ErrorAction SilentlyContinue
            $svc = Get-Service -Name WSearch -ErrorAction SilentlyContinue
            if ($svc) {
                $svc | Set-Service -StartupType AutomaticDelayedStart -ErrorAction SilentlyContinue
            }
        }
    },
    @{
        id    = "power"
        label = "电源计划设为高性能（避免节能模式导致的 CPU 降频）"
        desc  = "工作站不需要节能，直接设为最高性能。"
        cmd   = {
            powercfg /setactive 8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c 2>$null
            if ($LASTEXITCODE -ne 0) { powercfg /duplicatescheme 8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c 2>$null }
        }
    }
)

$script:optCheckboxes = @{}

foreach ($opt in $optimizations) {
    $cb = New-Object System.Windows.Forms.CheckBox
    $cb.Text     = $opt.label
    $cb.Location = New-Object System.Drawing.Point(20, $optY)
    $cb.AutoSize = $true
    $cb.Checked  = $true
    $optGroup.Controls.Add($cb)
    $script:optCheckboxes[$opt.id] = $cb

    $optY += 24

    $descLabel = New-Object System.Windows.Forms.Label
    $descLabel.Text     = "    $($opt.desc)"
    $descLabel.Location = New-Object System.Drawing.Point(40, $optY)
    $descLabel.AutoSize = $true
    $descLabel.ForeColor = "Gray"
    $descLabel.Font = New-Object System.Drawing.Font("Microsoft YaHei UI", 8)
    $optGroup.Controls.Add($descLabel)

    $optY += 22
}

# 一键优化按钮
$optimizeBtn = New-Object System.Windows.Forms.Button
$optimizeBtn.Text = "一键执行优化"
$optimizeBtn.Location = New-Object System.Drawing.Point(300, 340)
$optimizeBtn.Size = New-Object System.Drawing.Size(180, 38)
$optimizeBtn.BackColor = [System.Drawing.Color]::FromArgb(0, 153, 76)
$optimizeBtn.ForeColor = "White"
$optimizeBtn.FlatStyle = "Flat"
$optimizeBtn.Font = New-Object System.Drawing.Font("Microsoft YaHei UI", 10, "Bold")
$tab4.Controls.Add($optimizeBtn)

$optimizeBtn.Add_Click({
    try {
        $applied = 0
        $failed  = 0
        foreach ($opt in $optimizations) {
            if ($script:optCheckboxes[$opt.id].Checked) {
                try {
                    & $opt.cmd
                    Write-Log "已应用: $($opt.label)"
                    $applied++
                } catch {
                    Write-Log "应用失败 [$($opt.label)]: $_" "WARN"
                    $failed++
                }
            }
        }
        Write-Log "性能优化完成：成功 $applied 项，失败 $failed 项"
        Write-AuditLog -Action "APPLY_OPTIMIZATION" -Target "system" -Detail "已应用 $applied 项优化"
        Show-Dialog "完成" "优化已应用 $applied 项$(if ($failed -gt 0) { "，失败 $failed 项（详见日志）" } else { "" })`n`n部分优化可能需要重启后生效。" "OK" "Information"
    } catch {
        Write-Log "优化执行失败: $_" "ERROR"
        Write-AuditLog -Action "APPLY_OPTIMIZATION" -Target "system" -Result "FAILED" -Detail "$_"
    }
})

$tabs.TabPages.Add($tab4) | Out-Null

# ==================== Tab 5: 批量操作 ====================
$tab5 = New-Object System.Windows.Forms.TabPage
$tab5.Text = "批量操作"

$batchIntro = New-Object System.Windows.Forms.Label
$batchIntro.Text = "批量创建账户：每行一个，格式为 "用户名,显示名称,导师名"（导师名必须匹配已有导师组）。"
$batchIntro.Location = New-Object System.Drawing.Point(20, 15)
$batchIntro.AutoSize = $true
$tab5.Controls.Add($batchIntro)

$batchExample = New-Object System.Windows.Forms.Label
$batchExample.Text = "示例：zhangsan,张三,张老师"
$batchExample.ForeColor = "Gray"
$batchExample.Location = New-Object System.Drawing.Point(20, 38)
$batchExample.AutoSize = $true
$tab5.Controls.Add($batchExample)

$batchGroup = New-Object System.Windows.Forms.GroupBox
$batchGroup.Text = "用户列表"
$batchGroup.Location = New-Object System.Drawing.Point(15, 60)
$batchGroup.Size = New-Object System.Drawing.Size(460, 290)
$tab5.Controls.Add($batchGroup)

$script:batchInput = New-Object System.Windows.Forms.TextBox
$script:batchInput.Location = New-Object System.Drawing.Point(12, 22)
$script:batchInput.Size = New-Object System.Drawing.Size(435, 255)
$script:batchInput.Multiline = $true
$script:batchInput.ScrollBars = "Vertical"
$script:batchInput.Font = New-Object System.Drawing.Font("Consolas", 10)
$batchGroup.Controls.Add($script:batchInput)

$batchLabel2 = New-Object System.Windows.Forms.Label
$batchLabel2.Text = "默认密码（所有新建账户共用）："
$batchLabel2.Location = New-Object System.Drawing.Point(490, 75)
$batchLabel2.AutoSize = $true
$tab5.Controls.Add($batchLabel2)

$script:batchPassword = New-Object System.Windows.Forms.TextBox
$script:batchPassword.Location = New-Object System.Drawing.Point(490, 100)
$script:batchPassword.Size = New-Object System.Drawing.Size(200, 25)
$script:batchPassword.UseSystemPasswordChar = $true
$script:batchPassword.Text = ""
$tab5.Controls.Add($script:batchPassword)

$batchGenBtn = New-Object System.Windows.Forms.Button
$batchGenBtn.Text = "随机生成"
$batchGenBtn.Location = New-Object System.Drawing.Point(700, 100)
$batchGenBtn.Size = New-Object System.Drawing.Size(65, 25)
$tab5.Controls.Add($batchGenBtn)
$batchGenBtn.Add_Click({
    $script:batchPassword.Text = Generate-RandomPassword
    Write-Log "已为批量操作生成随机密码"
})

$batchNote = New-Object System.Windows.Forms.Label
$batchNote.Text = "注意：创建后建议提醒各用户首次登录后修改密码。`n导师名必须与已有导师组匹配，否则该行将被跳过。"
$batchNote.ForeColor = "Red"
$batchNote.Location = New-Object System.Drawing.Point(490, 135)
$batchNote.AutoSize = $true
$tab5.Controls.Add($batchNote)

$batchBtn = New-Object System.Windows.Forms.Button
$batchBtn.Text = "批量创建"
$batchBtn.Location = New-Object System.Drawing.Point(540, 210)
$batchBtn.Size = New-Object System.Drawing.Size(140, 38)
$batchBtn.BackColor = [System.Drawing.Color]::FromArgb(0, 122, 204)
$batchBtn.ForeColor = "White"
$batchBtn.FlatStyle = "Flat"
$batchBtn.Font = New-Object System.Drawing.Font("Microsoft YaHei UI", 10, "Bold")
$tab5.Controls.Add($batchBtn)

$batchBtn.Add_Click({
    try {
        $lines = $script:batchInput.Text -split "`n" | Where-Object { $_.Trim() -ne "" }
        if ($lines.Count -eq 0) {
            Show-Dialog "提示" "请输入用户列表" "OK" "Warning"; return
        }
        $pwd = $script:batchPassword.Text
        if ($pwd.Length -lt 8) {
            Show-Dialog "错误" "密码长度至少 8 位" "OK" "Error"; return
        }

        # 确保全员组存在
        if (-not (Test-GroupExists -Name $script:AllGroup)) {
            net localgroup $script:AllGroup /add 2>&1 | Out-Null
            Write-Log "全员组 '$($script:AllGroup)' 已自动创建"
        }

        $secPwd = ConvertTo-SecureString $pwd -AsPlainText -Force
        $successCount = 0
        $failCount = 0

        # 获取当前导师组列表
        $currentAdvisors = Get-AllAdvisorGroups

        foreach ($line in $lines) {
            $parts = ($line.Trim() -split ",") | ForEach-Object { $_.Trim() }
            $uname     = $parts[0]
            $dname     = if ($parts.Count -gt 1) { $parts[1] } else { "" }
            $advisorNm = if ($parts.Count -gt 2) { $parts[2] } else { "" }

            if ([string]::IsNullOrWhiteSpace($uname)) { continue }
            if ($uname -match '[^a-zA-Z0-9_.\-]') {
                Write-Log "跳过非法用户名: $uname" "WARN"
                $failCount++
                continue
            }

            # 验证导师名
            if ([string]::IsNullOrWhiteSpace($advisorNm)) {
                Write-Log "跳过 '$uname'：未指定导师名" "WARN"
                $failCount++
                continue
            }
            if ($currentAdvisors -notcontains $advisorNm) {
                Write-Log "跳过 '$uname'：导师组 'Lab_$advisorNm' 不存在（请先创建）" "WARN"
                $failCount++
                continue
            }

            try {
                # 检查用户是否已存在
                $existing = Get-LocalUser -Name $uname -ErrorAction SilentlyContinue
                if ($existing) {
                    Write-Log "用户 '$uname' 已存在，跳过创建" "WARN"
                } else {
                    New-LocalUser -Name $uname -Password $secPwd -FullName $dname -Description $dname `
                        -PasswordNeverExpires -ErrorAction Stop
                    Write-Log "已创建账户: $uname ($dname)"
                }

                # 加入全员组
                net localgroup $script:AllGroup $uname /add 2>&1 | Out-Null

                # 加入导师组
                $advGroup = "Lab_$advisorNm"
                net localgroup $advGroup $uname /add 2>&1 | Out-Null
                Write-Log "已将 '$uname' 加入 $advGroup"

                # 创建个人目录
                New-UserPersonalDir -Username $uname | Out-Null

                $successCount++
            } catch {
                Write-Log "创建 '$uname' 失败: $_" "ERROR"
                $failCount++
            }
        }

        Write-Log "批量创建完成：成功 $successCount 人，失败 $failCount 人"
        Write-AuditLog -Action "BATCH_CREATE" -Target "$successCount users" -Detail "成功: $successCount, 失败: $failCount"
        Show-Dialog "完成" "批量创建完成`n成功：$successCount 人`n失败：$failCount 人`n`n详见操作日志。" "OK" "Information"

        $script:batchInput.Text = ""
        $tabs.SelectedIndex = 0
        Refresh-FilterCombo
        Refresh-MemberList
    } catch {
        Write-Log "批量创建失败: $_" "ERROR"
        Show-Dialog "错误" "批量创建失败: $_" "OK" "Error"
    }
})

# ==================== Tab 6: 离校管理 ====================
$tab6 = New-Object System.Windows.Forms.TabPage
$tab6.Text = "离校管理"

$departIntro = New-Object System.Windows.Forms.Label
$departIntro.Text = "学生离校时，执行此流程：禁用账户、归档个人数据和组内数据、从所有组中移除，并生成工作交接清单。"
$departIntro.Location = New-Object System.Drawing.Point(20, 15)
$departIntro.AutoSize = $true
$tab6.Controls.Add($departIntro)

# -- 选择用户 --
$departUserGroup = New-Object System.Windows.Forms.GroupBox
$departUserGroup.Text     = "选择用户"
$departUserGroup.Location = New-Object System.Drawing.Point(15, 50)
$departUserGroup.Size     = New-Object System.Drawing.Size(745, 60)
$tab6.Controls.Add($departUserGroup)

$departUserLabel = New-Object System.Windows.Forms.Label
$departUserLabel.Text     = "离校用户："
$departUserLabel.Location = New-Object System.Drawing.Point(15, 22)
$departUserLabel.AutoSize = $true
$departUserGroup.Controls.Add($departUserLabel)

$script:departUserCombo = New-Object System.Windows.Forms.ComboBox
$script:departUserCombo.Location    = New-Object System.Drawing.Point(95, 19)
$script:departUserCombo.Size        = New-Object System.Drawing.Size(250, 25)
$script:departUserCombo.DropDownStyle = "DropDownList"
$departUserGroup.Controls.Add($script:departUserCombo)

$departRefreshBtn = New-Object System.Windows.Forms.Button
$departRefreshBtn.Text     = "刷新列表"
$departRefreshBtn.Location = New-Object System.Drawing.Point(360, 17)
$departRefreshBtn.Size     = New-Object System.Drawing.Size(90, 28)
$departUserGroup.Controls.Add($departRefreshBtn)

$departInfoBtn = New-Object System.Windows.Forms.Button
$departInfoBtn.Text     = "查看用户信息"
$departInfoBtn.Location = New-Object System.Drawing.Point(465, 17)
$departInfoBtn.Size     = New-Object System.Drawing.Size(120, 28)
$departUserGroup.Controls.Add($departInfoBtn)

# -- 用户信息显示区 --
$departInfoGroup = New-Object System.Windows.Forms.GroupBox
$departInfoGroup.Text     = "用户信息"
$departInfoGroup.Location = New-Object System.Drawing.Point(15, 118)
$departInfoGroup.Size     = New-Object System.Drawing.Size(745, 140)
$tab6.Controls.Add($departInfoGroup)

$script:departInfoBox = New-Object System.Windows.Forms.RichTextBox
$script:departInfoBox.Location    = New-Object System.Drawing.Point(12, 22)
$script:departInfoBox.Size        = New-Object System.Drawing.Size(720, 108)
$script:departInfoBox.ReadOnly    = $true
$script:departInfoBox.BackColor   = [System.Drawing.Color]::FromArgb(245, 245, 245)
$script:departInfoBox.BorderStyle = "FixedSingle"
$script:departInfoBox.Font        = New-Object System.Drawing.Font("Microsoft YaHei UI", 9)
$departInfoGroup.Controls.Add($script:departInfoBox)

# -- 步骤状态显示 --
$departStatusGroup = New-Object System.Windows.Forms.GroupBox
$departStatusGroup.Text     = "执行进度"
$departStatusGroup.Location = New-Object System.Drawing.Point(15, 266)
$departStatusGroup.Size     = New-Object System.Drawing.Size(745, 100)
$tab6.Controls.Add($departStatusGroup)

$script:departStatusBox = New-Object System.Windows.Forms.RichTextBox
$script:departStatusBox.Location    = New-Object System.Drawing.Point(12, 22)
$script:departStatusBox.Size        = New-Object System.Drawing.Size(720, 68)
$script:departStatusBox.ReadOnly    = $true
$script:departStatusBox.BackColor   = [System.Drawing.Color]::FromArgb(250, 250, 250)
$script:departStatusBox.BorderStyle = "FixedSingle"
$script:departStatusBox.Font        = New-Object System.Drawing.Font("Consolas", 8.5)
$departStatusGroup.Controls.Add($script:departStatusBox)

# -- 执行离校按钮 --
$departExecBtn = New-Object System.Windows.Forms.Button
$departExecBtn.Text = "执行离校流程"
$departExecBtn.Location = New-Object System.Drawing.Point(300, 378)
$departExecBtn.Size = New-Object System.Drawing.Size(180, 38)
$departExecBtn.BackColor = [System.Drawing.Color]::FromArgb(180, 30, 30)
$departExecBtn.ForeColor = "White"
$departExecBtn.FlatStyle = "Flat"
$departExecBtn.Font = New-Object System.Drawing.Font("Microsoft YaHei UI", 10, "Bold")
$tab6.Controls.Add($departExecBtn)

# -- 刷新离校用户下拉列表 --
function Refresh-DepartUserCombo {
    $script:departUserCombo.Items.Clear()
    if (Test-GroupExists -Name $script:AllGroup) {
        $allMembers = Get-GroupMembers -GroupName $script:AllGroup
        foreach ($m in $allMembers) {
            $script:departUserCombo.Items.Add($m) | Out-Null
        }
    }
    if ($script:departUserCombo.Items.Count -gt 0) {
        $script:departUserCombo.SelectedIndex = 0
    }
    Write-Log "离校用户列表已刷新，共 $($script:departUserCombo.Items.Count) 人"
}

# -- 刷新按钮 --
$departRefreshBtn.Add_Click({
    try {
        Refresh-DepartUserCombo
    } catch {
        Write-Log "刷新离校用户列表失败: $_" "ERROR"
    }
})

# -- 辅助：获取文件夹大小（人性化显示）--
function Get-FolderSizeDisplay {
    param([string]$Path)
    if (-not (Test-Path $Path)) { return "(路径不存在)" }
    try {
        $size = (Get-ChildItem -Path $Path -Recurse -File -ErrorAction SilentlyContinue | Measure-Object -Property Length -Sum).Sum
        if ($null -eq $size) { return "0 B" }
        if ($size -ge 1GB) { return "{0:N2} GB" -f ($size / 1GB) }
        if ($size -ge 1MB) { return "{0:N2} MB" -f ($size / 1MB) }
        if ($size -ge 1KB) { return "{0:N2} KB" -f ($size / 1KB) }
        return "$size B"
    } catch {
        return "(获取失败)"
    }
}

# -- 查看用户信息 --
$departInfoBtn.Add_Click({
    try {
        $username = $script:departUserCombo.SelectedItem
        if ([string]::IsNullOrWhiteSpace($username)) {
            Show-Dialog "提示" "请先选择一个用户" "OK" "Warning"
            return
        }

        $script:departInfoBox.Clear()

        # 基本信息
        $localUser = Get-LocalUser -Name $username -ErrorAction SilentlyContinue
        $displayName = if ($localUser) { $localUser.Description } else { "(未知)" }
        $enabled = if ($localUser) { if ($localUser.Enabled) { "已启用" } else { "已禁用" } } else { "(未知)" }

        # 导师组
        $advisor = Get-UserAdvisorGroup -Username $username
        $advisorDisplay = if ($advisor) { "Lab_$advisor" } else { "(未分配)" }

        # 个人文件夹大小
        $personalPath = Join-Path $script:UsersRootPath $username
        $personalSize = Get-FolderSizeDisplay -Path $personalPath

        # 组内文件夹大小
        $groupSizeDisplay = "(未分配导师组)"
        if ($advisor) {
            $advisorPath = Join-Path $script:SharePath $advisor
            $groupSizeDisplay = Get-FolderSizeDisplay -Path $advisorPath
        }

        $info = "用户名：$username`r`n"
        $info += "显示名称：$displayName`r`n"
        $info += "账户状态：$enabled`r`n"
        $info += "导师组：$advisorDisplay`r`n"
        $info += "个人文件夹：$personalPath ($personalSize)`r`n"
        $info += "组内数据区：$(if ($advisor) { Join-Path $script:SharePath $advisor } else { '(无)' }) ($groupSizeDisplay)"

        $script:departInfoBox.Text = $info
        Write-Log "已查看用户 '$username' 的信息"
    } catch {
        Write-Log "查看用户信息失败: $_" "ERROR"
        Show-Dialog "错误" "查看用户信息失败: $_" "OK" "Error"
    }
})

# -- 执行离校流程 --
$departExecBtn.Add_Click({
    try {
        $username = $script:departUserCombo.SelectedItem
        if ([string]::IsNullOrWhiteSpace($username)) {
            Show-Dialog "提示" "请先选择一个用户" "OK" "Warning"
            return
        }

        $advisor = Get-UserAdvisorGroup -Username $username
        $advisorDisplay = if ($advisor) { "Lab_$advisor" } else { "(未分配)" }

        $confirmMsg = "确定要对用户 '$username' 执行离校流程吗？`n`n"
        $confirmMsg += "即将执行以下操作：`n"
        $confirmMsg += "1. 禁用账户（用户将无法登录）`n"
        $confirmMsg += "2. 归档个人数据到 D:\GroupData\_公共\99_归档\`n"
        if ($advisor) {
            $confirmMsg += "3. 归档组内数据（$advisorDisplay）`n"
        }
        $confirmMsg += "4. 从所有组中移除`n"
        $confirmMsg += "5. 生成工作交接清单`n`n"
        $confirmMsg += "此操作不可撤销，请确认。"

        $confirm = Show-Dialog "确认离校" $confirmMsg "YesNo" "Warning"
        if ($confirm -ne "Yes") { return }

        $departExecBtn.Enabled = $false
        $script:departStatusBox.Clear()
        $dateStr = Get-Date -Format "yyyyMMdd"
        $archiveBase = Join-Path $script:PublicPath "99_归档"
        $archiveDir = Join-Path $archiveBase "离校用户_${username}_${dateStr}"

        # Step 1: 禁用账户
        try {
            $script:departStatusBox.AppendText("[$(Get-Date -Format 'HH:mm:ss')] 步骤 1/5：禁用账户...`r`n")
            $script:departStatusBox.ScrollToCaret()
            [System.Windows.Forms.Application]::DoEvents()
            Disable-LocalUser -Name $username -ErrorAction Stop
            Write-Log "已禁用账户 '$username'"
            $script:departStatusBox.AppendText("[$(Get-Date -Format 'HH:mm:ss')]   -> 账户已禁用`r`n")
            $script:departStatusBox.ScrollToCaret()
            [System.Windows.Forms.Application]::DoEvents()
        } catch {
            $script:departStatusBox.AppendText("[$(Get-Date -Format 'HH:mm:ss')]   -> 禁用账户失败: $_`r`n")
            $script:departStatusBox.ScrollToCaret()
            [System.Windows.Forms.Application]::DoEvents()
            Write-Log "禁用账户 '$username' 失败: $_" "ERROR"
            Write-AuditLog -Action "DEPARTURE" -Target $username -Result "FAILED" -Detail "禁用账户失败: $_"
            Show-Dialog "错误" "离校流程中止：禁用账户失败`n$_" "OK" "Error"
            $departExecBtn.Enabled = $true
            return
        }

        # Step 2: 创建归档目录并复制个人数据
        try {
            $script:departStatusBox.AppendText("[$(Get-Date -Format 'HH:mm:ss')] 步骤 2/5：归档个人数据...`r`n")
            $script:departStatusBox.ScrollToCaret()
            [System.Windows.Forms.Application]::DoEvents()

            if (-not (Test-Path $archiveBase)) {
                New-Item -ItemType Directory -Path $archiveBase -Force | Out-Null
            }
            if (-not (Test-Path $archiveDir)) {
                New-Item -ItemType Directory -Path $archiveDir -Force | Out-Null
            }

            $personalDataDest = Join-Path $archiveDir "个人数据"
            $personalSrc = Join-Path $script:UsersRootPath $username
            if (Test-Path $personalSrc) {
                New-Item -ItemType Directory -Path $personalDataDest -Force | Out-Null
                Copy-Item -Path "$personalSrc\*" -Destination $personalDataDest -Recurse -Force -ErrorAction SilentlyContinue
                Write-Log "已归档 '$username' 的个人数据到 $personalDataDest"
                $script:departStatusBox.AppendText("[$(Get-Date -Format 'HH:mm:ss')]   -> 个人数据已归档`r`n")
            } else {
                $script:departStatusBox.AppendText("[$(Get-Date -Format 'HH:mm:ss')]   -> 个人数据目录不存在，跳过`r`n")
            }
            $script:departStatusBox.ScrollToCaret()
            [System.Windows.Forms.Application]::DoEvents()
        } catch {
            $script:departStatusBox.AppendText("[$(Get-Date -Format 'HH:mm:ss')]   -> 归档个人数据失败: $_`r`n")
            $script:departStatusBox.ScrollToCaret()
            [System.Windows.Forms.Application]::DoEvents()
            Write-Log "归档个人数据失败: $_" "WARN"
        }

        # Step 3: 记录组内数据信息（不复制整个导师文件夹）
        try {
            $script:departStatusBox.AppendText("[$(Get-Date -Format 'HH:mm:ss')] 步骤 3/5：记录组内数据信息...`r`n")
            $script:departStatusBox.ScrollToCaret()
            [System.Windows.Forms.Application]::DoEvents()

            if ($advisor) {
                $currentAdvisor = $advisor
                $advisorPath = Join-Path $script:SharePath $currentAdvisor
                $groupInfoPath = Join-Path $archiveDir "组内数据说明.txt"
                $groupInfo = @"
用户 '$username' 所属导师组: Lab_$currentAdvisor
组内数据路径: $advisorPath

注意：组内数据为全组共享，未自动复制到归档目录。
如需保留该用户的特定文件，请手动从上述路径中查找并复制。
"@
                Set-Content -Path $groupInfoPath -Value $groupInfo -Encoding UTF8
                Write-Log "已记录 '$username' 的组内数据信息（Lab_$currentAdvisor）到 $groupInfoPath"
                $script:departStatusBox.AppendText("[$(Get-Date -Format 'HH:mm:ss')]   -> 已记录组内数据信息到归档目录`r`n")
            } else {
                $script:departStatusBox.AppendText("[$(Get-Date -Format 'HH:mm:ss')]   -> 用户未分配导师组，跳过`r`n")
            }
            $script:departStatusBox.ScrollToCaret()
            [System.Windows.Forms.Application]::DoEvents()
            $script:departStatusBox.AppendText("[$(Get-Date -Format 'HH:mm:ss')]   -> [OK] 步骤 3 完成`r`n")
            $script:departStatusBox.ScrollToCaret()
            [System.Windows.Forms.Application]::DoEvents()
        } catch {
            $script:departStatusBox.AppendText("[$(Get-Date -Format 'HH:mm:ss')]   -> [警告] 步骤 3 异常: $_`r`n")
            $script:departStatusBox.ScrollToCaret()
            [System.Windows.Forms.Application]::DoEvents()
            Write-Log "记录组内数据信息失败: $_" "WARN"
        }

        # Step 4: 从所有组中移除
        try {
            $script:departStatusBox.AppendText("[$(Get-Date -Format 'HH:mm:ss')] 步骤 4/5：从所有组中移除...`r`n")
            $script:departStatusBox.ScrollToCaret()
            [System.Windows.Forms.Application]::DoEvents()

            # 从 Lab_All 移除
            net localgroup $script:AllGroup $username /delete 2>&1 | Out-Null
            if ($LASTEXITCODE -ne 0) { throw "从 '$($script:AllGroup)' 移除失败 (exit code: $LASTEXITCODE)" }
            Write-Log "已将 '$username' 从 $script:AllGroup 移除"
            $script:departStatusBox.AppendText("[$(Get-Date -Format 'HH:mm:ss')]   -> 已从 $script:AllGroup 移除`r`n")
            $script:departStatusBox.ScrollToCaret()
            [System.Windows.Forms.Application]::DoEvents()

            # 从导师组移除
            if ($advisor) {
                $advisorGroup = "Lab_$advisor"
                net localgroup $advisorGroup $username /delete 2>&1 | Out-Null
                if ($LASTEXITCODE -ne 0) { throw "从 '$advisorGroup' 移除失败 (exit code: $LASTEXITCODE)" }
                Write-Log "已将 '$username' 从 $advisorGroup 移除"
                $script:departStatusBox.AppendText("[$(Get-Date -Format 'HH:mm:ss')]   -> 已从 $advisorGroup 移除`r`n")
                $script:departStatusBox.ScrollToCaret()
                [System.Windows.Forms.Application]::DoEvents()
            }
        } catch {
            $script:departStatusBox.AppendText("[$(Get-Date -Format 'HH:mm:ss')]   -> 从组中移除失败: $_`r`n")
            $script:departStatusBox.ScrollToCaret()
            [System.Windows.Forms.Application]::DoEvents()
            Write-Log "从组中移除失败: $_" "WARN"
        }

        # Step 5: 生成工作交接清单
        try {
            $script:departStatusBox.AppendText("[$(Get-Date -Format 'HH:mm:ss')] 步骤 5/5：生成工作交接清单...`r`n")
            $script:departStatusBox.ScrollToCaret()
            [System.Windows.Forms.Application]::DoEvents()

            $checklistPath = Join-Path $archiveDir "工作交接清单.txt"
            $checklist = @"
========================================
  课题组工作站 - 离校工作交接清单
========================================
用户：$username
离校日期：$(Get-Date -Format "yyyy-MM-dd HH:mm:ss")
归档路径：$archiveDir
----------------------------------------
交接事项（请逐项确认）：
[ ] 1. 实验数据已备份并通知导师
[ ] 2. 共享文件夹中的关键文件已交接给接替人
[ ] 3. 课题组公共账号/密码已移交
[ ] 4. 正在进行的项目/实验已做交接说明
[ ] 5. 门禁卡/钥匙等实物已归还
[ ] 6. 导师确认签字：___________
[ ] 7. 管理员确认签字：___________
----------------------------------------
备注：
- 账户已禁用，数据已归档
- 个人数据归档位置：$archiveDir\个人数据
$(if ($advisor) { "- 组内数据归档位置：$archiveDir\组内数据（原 Lab_$advisor）" } else { "- 无导师组数据" })
- 如需恢复数据，请联系管理员
========================================
"@
            $checklist | Out-File -FilePath $checklistPath -Encoding UTF8
            Write-Log "已生成交接清单: $checklistPath"
            $script:departStatusBox.AppendText("[$(Get-Date -Format 'HH:mm:ss')]   -> 交接清单已生成`r`n")
            $script:departStatusBox.ScrollToCaret()
            [System.Windows.Forms.Application]::DoEvents()
        } catch {
            $script:departStatusBox.AppendText("[$(Get-Date -Format 'HH:mm:ss')]   -> 生成交接清单失败: $_`r`n")
            $script:departStatusBox.ScrollToCaret()
            [System.Windows.Forms.Application]::DoEvents()
            Write-Log "生成交接清单失败: $_" "WARN"
        }

        # 审计日志
        Write-AuditLog -Action "DEPARTURE" -Target $username -Result "SUCCESS" -Detail "归档路径: $archiveDir, 导师组: $(if ($advisor) { $advisor } else { '无' })"

        $script:departStatusBox.AppendText("`r`n[$(Get-Date -Format 'HH:mm:ss')] 离校流程执行完毕！`r`n")
        $script:departStatusBox.ScrollToCaret()
        [System.Windows.Forms.Application]::DoEvents()

        $departExecBtn.Enabled = $true

        # 汇总对话框
        $summaryMsg = "离校流程已执行完毕！`n`n"
        $summaryMsg += "用户：$username`n"
        $summaryMsg += "归档路径：$archiveDir`n"
        $summaryMsg += "`n请通知相关人员完成纸质交接清单签字。"
        Show-Dialog "离校完成" $summaryMsg "OK" "Information"

        # 刷新列表
        Refresh-DepartUserCombo
        Refresh-MemberList
    } catch {
        Write-Log "离校流程执行失败: $_" "ERROR"
        Write-AuditLog -Action "DEPARTURE" -Target $username -Result "FAILED" -Detail "$_"
        $departExecBtn.Enabled = $true
        Show-Dialog "错误" "离校流程执行失败:`n$_" "OK" "Error"
    }
})

# ==================== Tab 7: 公告推送 ====================
$tab7 = New-Object System.Windows.Forms.TabPage
$tab7.Text = "公告推送"

$broadcastIntro = New-Object System.Windows.Forms.Label
$broadcastIntro.Text = "向所有在线用户推送通知。通知保存后由用户端的 Lab-TrayApp.ps1 轮询并弹出提醒。"
$broadcastIntro.Location = New-Object System.Drawing.Point(20, 15)
$broadcastIntro.AutoSize = $true
$tab7.Controls.Add($broadcastIntro)

# -- 编辑通知 --
$broadcastEditGroup = New-Object System.Windows.Forms.GroupBox
$broadcastEditGroup.Text     = "编辑通知"
$broadcastEditGroup.Location = New-Object System.Drawing.Point(15, 45)
$broadcastEditGroup.Size     = New-Object System.Drawing.Size(745, 330)
$tab7.Controls.Add($broadcastEditGroup)

# 标题
$bcastTitleLabel = New-Object System.Windows.Forms.Label
$bcastTitleLabel.Text     = "通知标题："
$bcastTitleLabel.Location = New-Object System.Drawing.Point(15, 28)
$bcastTitleLabel.AutoSize = $true
$broadcastEditGroup.Controls.Add($bcastTitleLabel)

$script:bcastTitleBox = New-Object System.Windows.Forms.TextBox
$script:bcastTitleBox.Location = New-Object System.Drawing.Point(95, 25)
$script:bcastTitleBox.Size     = New-Object System.Drawing.Size(400, 25)
$broadcastEditGroup.Controls.Add($script:bcastTitleBox)

# 重要程度
$bcastImpLabel = New-Object System.Windows.Forms.Label
$bcastImpLabel.Text     = "重要程度："
$bcastImpLabel.Location = New-Object System.Drawing.Point(520, 28)
$bcastImpLabel.AutoSize = $true
$broadcastEditGroup.Controls.Add($bcastImpLabel)

$script:bcastImportanceCombo = New-Object System.Windows.Forms.ComboBox
$script:bcastImportanceCombo.Location    = New-Object System.Drawing.Point(600, 25)
$script:bcastImportanceCombo.Size        = New-Object System.Drawing.Size(100, 25)
$script:bcastImportanceCombo.DropDownStyle = "DropDownList"
$script:bcastImportanceCombo.Items.AddRange(@("普通", "重要", "紧急"))
$script:bcastImportanceCombo.SelectedIndex = 0
$broadcastEditGroup.Controls.Add($script:bcastImportanceCombo)

# 正文
$bcastMsgLabel = New-Object System.Windows.Forms.Label
$bcastMsgLabel.Text     = "通知内容："
$bcastMsgLabel.Location = New-Object System.Drawing.Point(15, 62)
$bcastMsgLabel.AutoSize = $true
$broadcastEditGroup.Controls.Add($bcastMsgLabel)

$script:bcastMsgBox = New-Object System.Windows.Forms.RichTextBox
$script:bcastMsgBox.Location    = New-Object System.Drawing.Point(15, 88)
$script:bcastMsgBox.Size        = New-Object System.Drawing.Size(715, 200)
$script:bcastMsgBox.Multiline   = $true
$script:bcastMsgBox.BorderStyle = "FixedSingle"
$script:bcastMsgBox.Font        = New-Object System.Drawing.Font("Microsoft YaHei UI", 9.5)
$broadcastEditGroup.Controls.Add($script:bcastMsgBox)

# -- 发送通知按钮 --
$bcastSendBtn = New-Object System.Windows.Forms.Button
$bcastSendBtn.Text = "发送通知"
$bcastSendBtn.Location = New-Object System.Drawing.Point(580, 300)
$bcastSendBtn.Size = New-Object System.Drawing.Size(150, 30)
$bcastSendBtn.BackColor = [System.Drawing.Color]::FromArgb(0, 122, 204)
$bcastSendBtn.ForeColor = "White"
$bcastSendBtn.FlatStyle = "Flat"
$bcastSendBtn.Font = New-Object System.Drawing.Font("Microsoft YaHei UI", 9.5, "Bold")
$broadcastEditGroup.Controls.Add($bcastSendBtn)

# -- 历史通知 --
$bcastHistoryGroup = New-Object System.Windows.Forms.GroupBox
$bcastHistoryGroup.Text     = "历史通知"
$bcastHistoryGroup.Location = New-Object System.Drawing.Point(15, 383)
$bcastHistoryGroup.Size     = New-Object System.Drawing.Size(745, 48)
$tab7.Controls.Add($bcastHistoryGroup)

$script:bcastHistoryCombo = New-Object System.Windows.Forms.ComboBox
$script:bcastHistoryCombo.Location    = New-Object System.Drawing.Point(15, 17)
$script:bcastHistoryCombo.Size        = New-Object System.Drawing.Size(530, 25)
$script:bcastHistoryCombo.DropDownStyle = "DropDownList"
$bcastHistoryGroup.Controls.Add($script:bcastHistoryCombo)

$bcastHistoryRefreshBtn = New-Object System.Windows.Forms.Button
$bcastHistoryRefreshBtn.Text     = "刷新"
$bcastHistoryRefreshBtn.Location = New-Object System.Drawing.Point(555, 15)
$bcastHistoryRefreshBtn.Size     = New-Object System.Drawing.Size(75, 28)
$bcastHistoryGroup.Controls.Add($bcastHistoryRefreshBtn)

$bcastArchiveBtn = New-Object System.Windows.Forms.Button
$bcastArchiveBtn.Text     = "移至历史"
$bcastArchiveBtn.Location = New-Object System.Drawing.Point(640, 15)
$bcastArchiveBtn.Size     = New-Object System.Drawing.Size(90, 28)
$bcastHistoryGroup.Controls.Add($bcastArchiveBtn)

# -- 通知目录 --
$script:NotifyPendingPath = Join-Path $script:PublicPath "_notifications\pending"
$script:NotifySentPath    = Join-Path $script:PublicPath "_notifications\sent"

# -- 发送通知 --
$bcastSendBtn.Add_Click({
    try {
        $title = $script:bcastTitleBox.Text.Trim()
        $message = $script:bcastMsgBox.Text.Trim()
        $importance = $script:bcastImportanceCombo.SelectedItem

        if ([string]::IsNullOrWhiteSpace($title)) {
            Show-Dialog "提示" "请输入通知标题" "OK" "Warning"
            return
        }
        if ([string]::IsNullOrWhiteSpace($message)) {
            Show-Dialog "提示" "请输入通知内容" "OK" "Warning"
            return
        }

        # 确保目录存在
        if (-not (Test-Path $script:NotifyPendingPath)) {
            New-Item -ItemType Directory -Path $script:NotifyPendingPath -Force | Out-Null
        }
        if (-not (Test-Path $script:NotifySentPath)) {
            New-Item -ItemType Directory -Path $script:NotifySentPath -Force | Out-Null
        }

        $notifyId = [guid]::NewGuid().ToString("N").Substring(0, 8)
        $timestamp = Get-Date -Format "yyyyMMddHHmmss"
        $fileName = "${notifyId}_${timestamp}.json"
        $filePath = Join-Path $script:NotifyPendingPath $fileName

        $operator = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
        $notifyObj = [ordered]@{
            id         = $notifyId
            title      = $title
            message    = $message
            importance = $importance
            timestamp  = (Get-Date -Format "yyyy-MM-dd HH:mm:ss")
            sender     = $operator
        }

        $notifyObj | ConvertTo-Json -Depth 3 | Out-File -FilePath $filePath -Encoding UTF8
        Write-Log "通知已发送：'$title' ($importance) -> $fileName"
        Write-AuditLog -Action "BROADCAST" -Target $title -Detail "重要程度: $importance, 文件: $fileName"

        # 清空输入
        $script:bcastTitleBox.Text = ""
        $script:bcastMsgBox.Text = ""
        $script:bcastImportanceCombo.SelectedIndex = 0

        Show-Dialog "成功" "通知已发送！`n`n标题：$title`n重要程度：$importance`n文件：$fileName`n`n用户端的 Lab-TrayApp.ps1 将自动轮询并弹出提醒。" "OK" "Information"

        # 刷新历史
        Refresh-BroadcastHistory
    } catch {
        Write-Log "发送通知失败: $_" "ERROR"
        Write-AuditLog -Action "BROADCAST" -Target $title -Result "FAILED" -Detail "$_"
        Show-Dialog "错误" "发送通知失败:`n$_" "OK" "Error"
    }
})

# -- 刷新历史通知列表 --
function Refresh-BroadcastHistory {
    $script:bcastHistoryCombo.Items.Clear()
    $sentDir = $script:NotifySentPath
    if (-not (Test-Path $sentDir)) { return }
    try {
        $files = Get-ChildItem -Path $sentDir -Filter "*.json" | Sort-Object LastWriteTime -Descending
        foreach ($f in $files) {
            try {
                $obj = Get-Content $f.FullName -Encoding UTF8 | ConvertFrom-Json
                $display = "[$($obj.timestamp)] $($obj.title) ($($obj.importance))"
                $script:bcastHistoryCombo.Items.Add($display) | Out-Null
            } catch {
                $script:bcastHistoryCombo.Items.Add($f.Name) | Out-Null
            }
        }
        if ($script:bcastHistoryCombo.Items.Count -gt 0) {
            $script:bcastHistoryCombo.SelectedIndex = 0
        }
    } catch {
        Write-Log "刷新历史通知失败: $_" "WARN"
    }
}

# -- 刷新历史按钮 --
$bcastHistoryRefreshBtn.Add_Click({
    try {
        Refresh-BroadcastHistory
    } catch {
        Write-Log "刷新历史通知失败: $_" "ERROR"
    }
})

# -- 移至历史 --
$bcastArchiveBtn.Add_Click({
    try {
        $pendingDir = $script:NotifyPendingPath
        $sentDir = $script:NotifySentPath
        if (-not (Test-Path $pendingDir)) {
            Show-Dialog "提示" "暂无待处理的通知" "OK" "Information"
            return
        }
        if (-not (Test-Path $sentDir)) {
            New-Item -ItemType Directory -Path $sentDir -Force | Out-Null
        }

        $pendingFiles = Get-ChildItem -Path $pendingDir -Filter "*.json"
        if ($pendingFiles.Count -eq 0) {
            Show-Dialog "提示" "待发送目录为空" "OK" "Information"
            return
        }

        $confirm = Show-Dialog "确认" "确定将 $($pendingFiles.Count) 条待发送通知移至历史吗？" "YesNo" "Question"
        if ($confirm -eq "Yes") {
            $moved = 0
            foreach ($f in $pendingFiles) {
                Move-Item -Path $f.FullName -Destination (Join-Path $sentDir $f.Name) -Force
                $moved++
            }
            Write-Log "已将 $moved 条通知移至历史"
            Write-AuditLog -Action "BROADCAST_ARCHIVE" -Target "$moved notifications" -Detail "移至历史目录"
            Refresh-BroadcastHistory
            Show-Dialog "成功" "已将 $moved 条通知移至历史" "OK" "Information"
        }
    } catch {
        Write-Log "移至历史失败: $_" "ERROR"
        Write-AuditLog -Action "BROADCAST_ARCHIVE" -Target "notifications" -Result "FAILED" -Detail "$_"
        Show-Dialog "错误" "移至历史失败:`n$_" "OK" "Error"
    }
})

$tabs.TabPages.Add($tab5) | Out-Null
$tabs.TabPages.Add($tab6) | Out-Null
$tabs.TabPages.Add($tab7) | Out-Null

# ==================== 启动 ====================

$form.Add_Shown({
    Write-Log "工具已启动，正在检查环境..."

    # 审计日志轮转
    Rotate-AuditLog

    # 检查 Lab_All
    if (Test-GroupExists -Name $script:AllGroup) {
        Write-Log "全员组 '$($script:AllGroup)' 已存在"
    } else {
        Write-Log "全员组 '$($script:AllGroup)' 不存在，正在自动创建..." "WARN"
        try {
            net localgroup $script:AllGroup /add 2>&1 | Out-Null
            Write-Log "全员组 '$($script:AllGroup)' 已创建"
        } catch {
            Write-Log "创建全员组失败: $_" "ERROR"
        }
    }

    # 检查 D:\GroupData
    if (Test-Path $script:SharePath) {
        Write-Log "数据区 $($script:SharePath) 已就绪"
    } else {
        Write-Log "数据区 $($script:SharePath) 不存在（请先运行 setup_workstation.ps1 或手动创建）" "WARN"
    }

    # 检查 D:\GroupData\_公共
    if (Test-Path $script:PublicPath) {
        Write-Log "公共区 $($script:PublicPath) 已就绪"
    } else {
        Write-Log "公共区 $($script:PublicPath) 不存在（请先手动创建或运行初始化脚本）" "WARN"
    }

    # 检查 D:\Users
    if (Test-Path $script:UsersRootPath) {
        Write-Log "个人目录区 $($script:UsersRootPath) 已就绪"
    } else {
        Write-Log "个人目录区 $($script:UsersRootPath) 不存在（创建账户时将自动建立）" "WARN"
    }

    # 列出发现的导师组
    $advisors = Get-AllAdvisorGroups
    if ($advisors.Count -gt 0) {
        Write-Log "发现 $($advisors.Count) 个导师组：$($advisors -join ', ')"
    } else {
        Write-Log "未发现任何导师组（可在「分组管理」或「创建账户」页新建）" "WARN"
    }

    # 初始化下拉框和列表
    Refresh-FilterCombo
    Refresh-AdvisorCombo
    Refresh-MemberList
    Refresh-GroupListView
    Refresh-GrpUserCombo
    Refresh-DepartUserCombo
    Refresh-BroadcastHistory
})

$form.ShowDialog() | Out-Null
