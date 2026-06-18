<#
.SYNOPSIS
    课题组工作站 · 悬浮导航 + 系统托盘
.DESCRIPTION
    桌面悬浮快捷导航窗口 + 系统托盘图标双模式。
    悬浮窗：始终置顶、可拖拽移动、不可关闭（无最小/最大/关闭按钮）。
    托盘图标：右键菜单提供额外操作（关于、退出等）。
.NOTES
    由 Deploy-TrayApp.ps1 自动部署到开机启动。
    手动测试：powershell -WindowStyle Hidden -File Lab-TrayApp.ps1
#>

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

# ==================== 配置 ====================
$script:SharePath     = "D:\GroupData"
$script:PublicPath    = "D:\GroupData\_公共"
$script:UsersRootPath = "D:\Users"

# ==================== 检测当前用户所属导师组 ====================
function Get-UserAdvisorGroup {
    $currentUser = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
    $shortName   = ($currentUser -split '\\')[-1]
    $advisorName = $null

    try {
        $groups = net localgroup 2>&1 | Where-Object { $_ -match '^\*Lab_' -and $_ -notmatch 'Lab_All' }
        foreach ($g in $groups) {
            $groupName = $g.TrimStart('*').Trim()
            $members = @(net localgroup $groupName 2>&1)
            foreach ($m in $members) {
                if ($m.Trim() -eq $shortName) {
                    $advisorName = $groupName -replace '^Lab_', ''
                    break
                }
            }
            if ($advisorName) { break }
        }
    } catch {}

    return @{
        AdvisorName = $advisorName
        UserName    = $shortName
        GroupFolder = if ($advisorName) { Join-Path $script:SharePath $advisorName } else { "" }
        UserFolder  = Join-Path $script:UsersRootPath $shortName
    }
}

$script:user = Get-UserAdvisorGroup
$script:shownNotifications = @()

# ==================== 绘制托盘图标 ====================
$iconBmp = New-Object System.Drawing.Bitmap(32, 32)
$ig = [System.Drawing.Graphics]::FromImage($iconBmp)
$ig.SmoothingMode = "AntiAlias"
$ig.FillRectangle((New-Object System.Drawing.SolidBrush(
    [System.Drawing.Color]::FromArgb(30, 30, 46))), 0, 0, 32, 32)
$fb = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(137, 180, 250))
$ig.FillPolygon($fb, @(
    (New-Object System.Drawing.PointF(5, 11)), (New-Object System.Drawing.PointF(13, 11)),
    (New-Object System.Drawing.PointF(15, 8)),  (New-Object System.Drawing.PointF(27, 8)),
    (New-Object System.Drawing.PointF(27, 24)), (New-Object System.Drawing.PointF(5, 24))))
$ig.Dispose()
$script:trayIcon = [System.Drawing.Icon]::FromHandle($iconBmp.GetHicon())

# ==================== 辅助：打开文件夹 ====================
function Open-Folder($path) {
    if ($path -and (Test-Path $path)) { Invoke-Item $path }
}

# ==================== 构建托盘右键菜单 ====================
$script:trayMenu = New-Object System.Windows.Forms.ContextMenuStrip
$script:trayMenu.Font = New-Object System.Drawing.Font("Microsoft YaHei UI", 9)

$hdr = $script:trayMenu.Items.Add(
    "$($script:user.UserName) · $(if ($script:user.AdvisorName) { $script:user.AdvisorName + '组' } else { '未分组' })")
$hdr.Enabled = $false; $hdr.ForeColor = "Gray"
$script:trayMenu.Items.Add("-") | Out-Null

$m1 = $script:trayMenu.Items.Add("显示/隐藏悬浮窗")
$m1.Add_Click({ $script:widget.Visible = -not $script:widget.Visible })

$mSelfService = $script:trayMenu.Items.Add("我的账户")
$mSelfService.Add_Click({ Show-SelfServiceDialog })

$script:trayMenu.Items.Add("-") | Out-Null

$m2 = $script:trayMenu.Items.Add("查看使用须知")
$m2.Add_Click({ Show-NoticePopup })

$m3 = $script:trayMenu.Items.Add("关于此工作站")
$m3.Add_Click({
    [System.Windows.Forms.MessageBox]::Show(
        "课题组公共工作站`n`n" +
        "当前用户：$($script:user.UserName)`n" +
        "所属组：$(if ($script:user.AdvisorName) { $script:user.AdvisorName } else { '未分组' })`n`n" +
        "数据存放规则：`n  个人 → D:\Users\你的用户名\`n  组内 → D:\GroupData\导师名\`n  跨组 → D:\GroupData\_公共\`n`n如有疑问请联系管理员。",
        "课题组工作站", "OK", "Information")
})

$script:trayMenu.Items.Add("-") | Out-Null

$m3 = $script:trayMenu.Items.Add("退出导航工具")
$m3.ForeColor = [System.Drawing.Color]::FromArgb(180, 0, 0)
$m3.Add_Click({
    $script:notifyIcon.Visible = $false
    $script:notifyIcon.Dispose()
    $script:widget.Close()
    [System.Windows.Forms.Application]::Exit()
})

# ==================== 托盘图标 ====================
$script:notifyIcon = New-Object System.Windows.Forms.NotifyIcon
$script:notifyIcon.Icon = $script:trayIcon
$script:notifyIcon.Text = "课题组工作站 · $($script:user.UserName)"
$script:notifyIcon.Visible = $true
$script:notifyIcon.ContextMenuStrip = $script:trayMenu
$script:notifyIcon.Add_DoubleClick({ $script:widget.Visible = -not $script:widget.Visible })

# ==================== 通知轮询 ====================
$script:notifTimer = New-Object System.Windows.Forms.Timer
$script:notifTimer.Interval = 30000  # 30 seconds
$script:notifTimer.Add_Tick({ Check-Notifications })
$script:notifTimer.Start()

# ==================== 悬浮窗主体 ====================

# 颜色和字体
$bgColor       = [System.Drawing.Color]::FromArgb(30, 30, 46)       # 深色背景
$accentColor   = [System.Drawing.Color]::FromArgb(137, 180, 250)    # 蓝色强调
$textColor     = [System.Drawing.Color]::FromArgb(205, 214, 244)    # 浅灰文字
$subtleColor   = [System.Drawing.Color]::FromArgb(147, 153, 178)    # 次要文字
$btnBg         = [System.Drawing.Color]::FromArgb(49, 50, 68)       # 按钮背景
$btnHover      = [System.Drawing.Color]::FromArgb(69, 71, 90)       # 按钮悬停
$btnText       = [System.Drawing.Color]::FromArgb(205, 214, 244)    # 按钮文字

$fontUser  = New-Object System.Drawing.Font("Microsoft YaHei UI", 9.5, "Bold")
$fontGroup = New-Object System.Drawing.Font("Microsoft YaHei UI", 8)
$fontBtn   = New-Object System.Drawing.Font("Microsoft YaHei UI", 8)

# 窗体
$script:widget = New-Object System.Windows.Forms.Form
$script:widget.FormBorderStyle = "None"           # 无标题栏、无三大键
$script:widget.TopMost        = $true             # 始终置顶
$script:widget.ShowInTaskbar  = $false            # 不显示在任务栏
$script:widget.StartPosition  = "Manual"
$script:widget.Size           = New-Object System.Drawing.Size(250, 100)
$script:widget.BackColor      = $bgColor
$script:widget.DoubleBuffered = $true
$script:widget.KeyPreview     = $true

# 初始位置：屏幕右下角（任务栏上方）
$screen = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
$script:widget.Location = New-Object System.Drawing.Point(
    ($screen.Right - 260), ($screen.Bottom - 105))

# ---- 拖拽逻辑 ----
$script:isDragging = $false
$script:dragStart  = New-Object System.Drawing.Point(0, 0)

$script:widget.Add_MouseDown({
    param($sender, $e)
    if ($e.Button -eq "Left") {
        $script:isDragging = $true
        $script:dragStart  = $e.Location
    }
})
$script:widget.Add_MouseMove({
    param($sender, $e)
    if ($script:isDragging) {
        $newX = $script:widget.Location.X + ($e.X - $script:dragStart.X)
        $newY = $script:widget.Location.Y + ($e.Y - $script:dragStart.Y)
        $script:widget.Location = New-Object System.Drawing.Point($newX, $newY)
    }
})
$script:widget.Add_MouseUp({
    param($sender, $e)
    $script:isDragging = $false
})

# ---- 屏蔽 Alt+F4 和所有关闭快捷键 ----
$script:widget.Add_KeyDown({
    param($sender, $e)
    if (($e.Alt -and $e.KeyCode -eq "F4") -or ($e.Control -and $e.KeyCode -eq "W")) {
        $e.Handled = $true
    }
})

# ---- 阻止 WM_CLOSE 消息 ----
$script:widget.Add_FormClosing({
    param($sender, $e)
    # 只允许程序内部调用 Close() 时关闭（CloseReason = ApplicationExitCall）
    if ($e.CloseReason -ne "ApplicationExitCall") {
        $e.Cancel = $true
    }
})

# ---- 防止获得焦点（点击时不抢夺当前窗口） ----
$script:widget.Add_Shown({
    # 使用 WS_EX_NOACTIVATE 样式，使窗口不抢夺焦点
    $hwnd = $script:widget.Handle
    $style = [System.Runtime.InteropServices.Marshal]::ReadInt32(
        [IntPtr]($hwnd.ToInt64() - 20))  # GWL_EXSTYLE = -20
    # WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOOLWINDOW = 0x00000080
    $style = $style -bor 0x08000080
    # 不直接设置，因为可能引发问题；通过 ShowInTaskbar=false + FormBorderStyle=None 已经足够
})

# ==================== 自定义绘制 ====================

# 路径存在性缓存（避免 Paint 中重复 I/O）
$script:pathExistsCache = @{}

# 按钮定义
$script:buttons = @(
    @{ Text = "个人"; Path = $script:user.UserFolder;  Rect = New-Object System.Drawing.Rectangle(10, 60, 70, 32) },
    @{ Text = "组内"; Path = $script:user.GroupFolder;  Rect = New-Object System.Drawing.Rectangle(90, 60, 70, 32) },
    @{ Text = "公共"; Path = $script:PublicPath;        Rect = New-Object System.Drawing.Rectangle(170, 60, 70, 32) }
)
$script:hoveredBtn = -1
$script:tagRect    = New-Object System.Drawing.Rectangle(0, 0, 0, 0)
$script:selfServiceRect = New-Object System.Drawing.Rectangle(0, 0, 0, 0)

# 圆角矩形路径辅助函数
function New-RoundedRect {
    param([System.Drawing.Rectangle]$Rect, [int]$Radius)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $Radius * 2
    $path.AddArc($Rect.X, $Rect.Y, $d, $d, 180, 90)
    $path.AddArc($Rect.Right - $d, $Rect.Y, $d, $d, 270, 90)
    $path.AddArc($Rect.Right - $d, $Rect.Bottom - $d, $d, $d, 0, 90)
    $path.AddArc($Rect.X, $Rect.Bottom - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}

# 绘制整个窗口
$script:widget.Add_Paint({
    param($sender, $e)
    $g = $e.Graphics
    $g.SmoothingMode   = "AntiAlias"
    $g.TextRenderingHint = "AntiAlias"

    $disposables = @()
    try {
        # -- 窗口圆角背景 --
        $fullRect = New-Object System.Drawing.Rectangle(0, 0, $script:widget.Width - 1, $script:widget.Height - 1)
        $roundPath = New-RoundedRect $fullRect 8
        $disposables += $roundPath
        $bgBrush = New-Object System.Drawing.SolidBrush($bgColor)
        $disposables += $bgBrush
        $g.FillPath($bgBrush, $roundPath)

        # 边框
        $borderPen = New-Object System.Drawing.Pen(
            [System.Drawing.Color]::FromArgb(69, 71, 90), 1)
        $disposables += $borderPen
        $g.DrawPath($borderPen, $roundPath)

        # -- 顶部拖拽提示条 --
        $dragBar = New-Object System.Drawing.Rectangle(115, 4, 20, 3)
        $dragBrush = New-Object System.Drawing.SolidBrush(
            [System.Drawing.Color]::FromArgb(88, 91, 112))
        $disposables += $dragBrush
        $g.FillRectangle($dragBrush, $dragBar)

        # -- 用户名 + 组名 --
        $userText = "$($script:user.UserName)"
        $userBrush = New-Object System.Drawing.SolidBrush($accentColor)
        $disposables += $userBrush
        $g.DrawString($userText, $fontUser, $userBrush, 14, 14)

        $groupText = if ($script:user.AdvisorName) { "$($script:user.AdvisorName)组" } else { "未分组" }
        $groupBrush = New-Object System.Drawing.SolidBrush($subtleColor)
        $disposables += $groupBrush
        $g.DrawString($groupText, $fontGroup, $groupBrush, 14, 33)

        # -- "我的账户" 自助服务链接 --
        $ssText = "我的账户 →"
        $ssSize = $g.MeasureString($ssText, $fontBtn)
        $ssRect = New-Object System.Drawing.Rectangle(14, 44, ([int]$ssSize.Width + 4), 16)
        $script:selfServiceRect = $ssRect
        $ssBrush = New-Object System.Drawing.SolidBrush($accentColor)
        $disposables += $ssBrush
        $g.DrawString($ssText, $fontBtn, $ssBrush, 14, 44)

        # -- "公共工作站" 标签（右侧，可点击查看须知） --
        $tagText = "公共工作站"
        $tagSize = $g.MeasureString($tagText, $fontBtn)
        $tagRect = New-Object System.Drawing.Rectangle(
            ($script:widget.Width - [int]$tagSize.Width - 18), 16,
            ([int]$tagSize.Width + 8), 18)
        $script:tagRect = $tagRect
        $tagPath = New-RoundedRect $tagRect 4
        $disposables += $tagPath
        $tagBgBrush = New-Object System.Drawing.SolidBrush(
            [System.Drawing.Color]::FromArgb(49, 50, 68))
        $disposables += $tagBgBrush
        $g.FillPath($tagBgBrush, $tagPath)
        $tagTextBrush = New-Object System.Drawing.SolidBrush($subtleColor)
        $disposables += $tagTextBrush
        $g.DrawString($tagText, $fontBtn, $tagTextBrush,
            ($tagRect.X + 4), ($tagRect.Y + 2))

        # -- 三个按钮 --
        for ($i = 0; $i -lt $script:buttons.Count; $i++) {
            $btn = $script:buttons[$i]
            $color = if ($i -eq $script:hoveredBtn) { $btnHover } else { $btnBg }
            $btnPath = New-RoundedRect $btn.Rect 6
            $disposables += $btnPath
            $btnFillBrush = New-Object System.Drawing.SolidBrush($color)
            $disposables += $btnFillBrush
            $g.FillPath($btnFillBrush, $btnPath)

            # 如果路径不存在，按钮文字变灰（使用缓存避免 I/O）
            $pathKey = $btn.Path
            if (-not $script:pathExistsCache.ContainsKey($pathKey)) {
                $script:pathExistsCache[$pathKey] = ($pathKey -and (Test-Path $pathKey))
            }
            $textColor = if ($script:pathExistsCache[$pathKey]) { $btnText } else { $subtleColor }
            $textSize = $g.MeasureString($btn.Text, $fontBtn)
            $tx = $btn.Rect.X + ($btn.Rect.Width  - [int]$textSize.Width)  / 2
            $ty = $btn.Rect.Y + ($btn.Rect.Height - [int]$textSize.Height) / 2
            $btnTextBrush = New-Object System.Drawing.SolidBrush($textColor)
            $disposables += $btnTextBrush
            $g.DrawString($btn.Text, $fontBtn, $btnTextBrush, $tx, $ty)
        }
    } finally {
        foreach ($d in $disposables) {
            if ($d -is [IDisposable]) { $d.Dispose() }
        }
    }
})

# ---- 按钮悬停高亮 ----
$script:widget.Add_MouseMove({
    param($sender, $e)
    $oldHover = $script:hoveredBtn
    $script:hoveredBtn = -1
    for ($i = 0; $i -lt $script:buttons.Count; $i++) {
        if ($script:buttons[$i].Rect.Contains($e.Location)) {
            $script:hoveredBtn = $i
            break
        }
    }
    if ($oldHover -ne $script:hoveredBtn) {
        $script:widget.Invalidate()
    }
    # 拖拽也要处理
    if ($script:isDragging) {
        $newX = $script:widget.Location.X + ($e.X - $script:dragStart.X)
        $newY = $script:widget.Location.Y + ($e.Y - $script:dragStart.Y)
        $script:widget.Location = New-Object System.Drawing.Point($newX, $newY)
    }
})

$script:widget.Add_MouseLeave({
    param($sender, $e)
    if ($script:hoveredBtn -ne -1) {
        $script:hoveredBtn = -1
        $script:widget.Invalidate()
    }
})

# ---- 按钮点击 ----
$script:widget.Add_MouseUp({
    param($sender, $e)
    if ($e.Button -eq "Left") {
        # 检查是否点击了"我的账户"自助服务链接
        if ($script:selfServiceRect.Contains($e.Location)) {
            Show-SelfServiceDialog
            return
        }
        # 检查是否点击了"公共工作站"标签
        if ($script:tagRect.Contains($e.Location)) {
            Show-NoticePopup
            return
        }
        for ($i = 0; $i -lt $script:buttons.Count; $i++) {
            if ($script:buttons[$i].Rect.Contains($e.Location)) {
                Open-Folder $script:buttons[$i].Path
                $script:pathExistsCache = @{}
                break
            }
        }
    }
})

# ---- 右键菜单（复用托盘菜单） ----
$script:widget.Add_MouseClick({
    param($sender, $e)
    if ($e.Button -eq "Right") {
        # 检查是否点击在按钮上（右键不打开文件夹，显示菜单）
        $script:trayMenu.Show($script:widget, $e.Location)
    }
})

# ---- 窗口区域设为圆角（让圆角外的点击穿透） ----
$script:widget.Add_Load({
    $region = New-Object System.Drawing.Region(
        (New-RoundedRect (New-Object System.Drawing.Rectangle(
            0, 0, $script:widget.Width, $script:widget.Height)) 8))
    $script:widget.Region = $region
})

# ==================== 告示弹窗 ====================
function Show-NoticePopup {
    $noticeForm = New-Object System.Windows.Forms.Form
    $noticeForm.Text          = "课题组工作站 · 使用须知"
    $noticeForm.Size          = New-Object System.Drawing.Size(480, 420)
    $noticeForm.StartPosition = "CenterScreen"
    $noticeForm.FormBorderStyle = "FixedDialog"
    $noticeForm.MaximizeBox   = $false
    $noticeForm.MinimizeBox   = $false
    $noticeForm.TopMost       = $true
    $noticeForm.BackColor     = [System.Drawing.Color]::FromArgb(30, 30, 46)
    $noticeForm.Font          = New-Object System.Drawing.Font("Microsoft YaHei UI", 9.5)

    # 标题
    $title = New-Object System.Windows.Forms.Label
    $title.Text = "数据存放规则"
    $title.ForeColor = [System.Drawing.Color]::FromArgb(137, 180, 250)
    $title.Font = New-Object System.Drawing.Font("Microsoft YaHei UI", 14, "Bold")
    $title.Location = New-Object System.Drawing.Point(20, 18)
    $title.AutoSize = $true
    $noticeForm.Controls.Add($title)

    # 内容区（使用 RichTextBox 做格式化显示）
    $content = New-Object System.Windows.Forms.RichTextBox
    $content.Location = New-Object System.Drawing.Point(20, 55)
    $content.Size     = New-Object System.Drawing.Size(430, 290)
    $content.ReadOnly = $true
    $content.BackColor = [System.Drawing.Color]::FromArgb(40, 42, 58)
    $content.ForeColor = [System.Drawing.Color]::FromArgb(205, 214, 244)
    $content.BorderStyle = "None"
    $content.Font = New-Object System.Drawing.Font("Microsoft YaHei UI", 10)
    $content.ScrollBars = "Vertical"

    $content.Text = @"
个人数据（草稿、私人文件）
  D:\Users\你的用户名\
  仅自己可见，其他人无法访问

组内数据（本组的项目、报告）
  D:\GroupData\你的导师名\对应类别\
  仅本组成员可见

跨组数据（需要多组共享的数据）
  D:\GroupData\_公共\
  所有成员可见

软件安装
  公共软件 → 找管理员装到 Program Files
  个人工具 → 装到 D:\Users\你的用户名\Tools\
  禁止往 GroupData 里装程序

注意事项
  不要把私人数据放在 GroupData 里
  不要在 C 盘存大文件
  用完远程桌面请注销
  跑耗时任务请限制资源占用
"@

    $noticeForm.Controls.Add($content)

    # 关闭按钮
    $closeBtn = New-Object System.Windows.Forms.Button
    $closeBtn.Text = "我知道了"
    $closeBtn.Size = New-Object System.Drawing.Size(120, 35)
    $closeBtn.Location = New-Object System.Drawing.Point(175, 360)
    $closeBtn.BackColor = [System.Drawing.Color]::FromArgb(137, 180, 250)
    $closeBtn.ForeColor = [System.Drawing.Color]::FromArgb(30, 30, 46)
    $closeBtn.FlatStyle = "Flat"
    $closeBtn.Font = New-Object System.Drawing.Font("Microsoft YaHei UI", 10, "Bold")
    $closeBtn.Add_Click({ $noticeForm.Close() })
    $noticeForm.Controls.Add($closeBtn)

    $noticeForm.ShowDialog() | Out-Null
    $noticeForm.Dispose()
}

# ==================== 自助服务弹窗 ====================
function Show-SelfServiceDialog {
    $form = New-Object System.Windows.Forms.Form
    $form.Text          = "我的账户"
    $form.Size          = New-Object System.Drawing.Size(500, 620)
    $form.StartPosition = "CenterScreen"
    $form.FormBorderStyle = "FixedDialog"
    $form.MaximizeBox   = $false
    $form.MinimizeBox   = $false
    $form.TopMost       = $true
    $form.BackColor     = [System.Drawing.Color]::FromArgb(30, 30, 46)
    $form.Font          = New-Object System.Drawing.Font("Microsoft YaHei UI", 9)

    $yPos = 15

    # -- 标题 --
    $title = New-Object System.Windows.Forms.Label
    $title.Text = "我的账户"
    $title.ForeColor = [System.Drawing.Color]::FromArgb(137, 180, 250)
    $title.Font = New-Object System.Drawing.Font("Microsoft YaHei UI", 14, "Bold")
    $title.Location = New-Object System.Drawing.Point(20, $yPos)
    $title.AutoSize = $true
    $form.Controls.Add($title)
    $yPos += 35

    # -- 用户信息区 --
    $infoPanel = New-Object System.Windows.Forms.Panel
    $infoPanel.Location = New-Object System.Drawing.Point(20, $yPos)
    $infoPanel.Size = New-Object System.Drawing.Size(440, 65)
    $infoPanel.BackColor = [System.Drawing.Color]::FromArgb(40, 42, 58)

    $lblUser = New-Object System.Windows.Forms.Label
    $lblUser.Text = "用户名：$($script:user.UserName)"
    $lblUser.ForeColor = [System.Drawing.Color]::FromArgb(205, 214, 244)
    $lblUser.Font = New-Object System.Drawing.Font("Microsoft YaHei UI", 10)
    $lblUser.Location = New-Object System.Drawing.Point(12, 8)
    $lblUser.AutoSize = $true
    $infoPanel.Controls.Add($lblUser)

    $lblAdvisor = New-Object System.Windows.Forms.Label
    $lblAdvisor.Text = "导师组：$(if ($script:user.AdvisorName) { $script:user.AdvisorName + '组' } else { '未分组' })"
    $lblAdvisor.ForeColor = [System.Drawing.Color]::FromArgb(147, 153, 178)
    $lblAdvisor.Font = New-Object System.Drawing.Font("Microsoft YaHei UI", 9)
    $lblAdvisor.Location = New-Object System.Drawing.Point(12, 30)
    $lblAdvisor.AutoSize = $true
    $infoPanel.Controls.Add($lblAdvisor)

    try {
        $lastLogon = (Get-Date).ToString("yyyy-MM-dd HH:mm:ss")
        $proc = Get-Process -IncludeUserName -ErrorAction SilentlyContinue |
            Where-Object { $_.UserName -like "*$($script:user.UserName)*" } |
            Sort-Object StartTime | Select-Object -First 1
        if ($proc -and $proc.StartTime) { $lastLogon = $proc.StartTime.ToString("yyyy-MM-dd HH:mm:ss") }
    } catch {
        $lastLogon = "未知"
    }
    $lblLogon = New-Object System.Windows.Forms.Label
    $lblLogon.Text = "最近登录：$lastLogon"
    $lblLogon.ForeColor = [System.Drawing.Color]::FromArgb(147, 153, 178)
    $lblLogon.Font = New-Object System.Drawing.Font("Microsoft YaHei UI", 9)
    $lblLogon.Location = New-Object System.Drawing.Point(230, 8)
    $lblLogon.AutoSize = $true
    $infoPanel.Controls.Add($lblLogon)

    $form.Controls.Add($infoPanel)
    $yPos += 75

    # -- 修改密码区 --
    $secPwd = New-Object System.Windows.Forms.Label
    $secPwd.Text = "修改密码"
    $secPwd.ForeColor = [System.Drawing.Color]::FromArgb(137, 180, 250)
    $secPwd.Font = New-Object System.Drawing.Font("Microsoft YaHei UI", 10, "Bold")
    $secPwd.Location = New-Object System.Drawing.Point(20, $yPos)
    $secPwd.AutoSize = $true
    $form.Controls.Add($secPwd)
    $yPos += 25

    $lblOld = New-Object System.Windows.Forms.Label
    $lblOld.Text = "原密码："
    $lblOld.ForeColor = [System.Drawing.Color]::FromArgb(205, 214, 244)
    $lblOld.Location = New-Object System.Drawing.Point(20, $yPos)
    $lblOld.AutoSize = $true
    $form.Controls.Add($lblOld)
    $txtOldPwd = New-Object System.Windows.Forms.TextBox
    $txtOldPwd.Location = New-Object System.Drawing.Point(90, $yPos)
    $txtOldPwd.Size = New-Object System.Drawing.Size(250, 24)
    $txtOldPwd.UseSystemPasswordChar = $true
    $txtOldPwd.BackColor = [System.Drawing.Color]::FromArgb(49, 50, 68)
    $txtOldPwd.ForeColor = [System.Drawing.Color]::FromArgb(205, 214, 244)
    $form.Controls.Add($txtOldPwd)
    $yPos += 30

    $lblNew = New-Object System.Windows.Forms.Label
    $lblNew.Text = "新密码："
    $lblNew.ForeColor = [System.Drawing.Color]::FromArgb(205, 214, 244)
    $lblNew.Location = New-Object System.Drawing.Point(20, $yPos)
    $lblNew.AutoSize = $true
    $form.Controls.Add($lblNew)
    $txtNewPwd = New-Object System.Windows.Forms.TextBox
    $txtNewPwd.Location = New-Object System.Drawing.Point(90, $yPos)
    $txtNewPwd.Size = New-Object System.Drawing.Size(250, 24)
    $txtNewPwd.UseSystemPasswordChar = $true
    $txtNewPwd.BackColor = [System.Drawing.Color]::FromArgb(49, 50, 68)
    $txtNewPwd.ForeColor = [System.Drawing.Color]::FromArgb(205, 214, 244)
    $form.Controls.Add($txtNewPwd)
    $yPos += 30

    $lblConfirm = New-Object System.Windows.Forms.Label
    $lblConfirm.Text = "确认密码："
    $lblConfirm.ForeColor = [System.Drawing.Color]::FromArgb(205, 214, 244)
    $lblConfirm.Location = New-Object System.Drawing.Point(20, $yPos)
    $lblConfirm.AutoSize = $true
    $form.Controls.Add($lblConfirm)
    $txtConfirmPwd = New-Object System.Windows.Forms.TextBox
    $txtConfirmPwd.Location = New-Object System.Drawing.Point(90, $yPos)
    $txtConfirmPwd.Size = New-Object System.Drawing.Size(250, 24)
    $txtConfirmPwd.UseSystemPasswordChar = $true
    $txtConfirmPwd.BackColor = [System.Drawing.Color]::FromArgb(49, 50, 68)
    $txtConfirmPwd.ForeColor = [System.Drawing.Color]::FromArgb(205, 214, 244)
    $form.Controls.Add($txtConfirmPwd)
    $yPos += 30

    $lblPwdStatus = New-Object System.Windows.Forms.Label
    $lblPwdStatus.Text = ""
    $lblPwdStatus.ForeColor = [System.Drawing.Color]::FromArgb(250, 179, 135)
    $lblPwdStatus.Location = New-Object System.Drawing.Point(90, $yPos)
    $lblPwdStatus.AutoSize = $true
    $form.Controls.Add($lblPwdStatus)

    $btnChangePwd = New-Object System.Windows.Forms.Button
    $btnChangePwd.Text = "修改密码"
    $btnChangePwd.Size = New-Object System.Drawing.Size(90, 28)
    $btnChangePwd.Location = New-Object System.Drawing.Point(350, ($yPos - 5))
    $btnChangePwd.BackColor = [System.Drawing.Color]::FromArgb(137, 180, 250)
    $btnChangePwd.ForeColor = [System.Drawing.Color]::FromArgb(30, 30, 46)
    $btnChangePwd.FlatStyle = "Flat"
    $btnChangePwd.Font = New-Object System.Drawing.Font("Microsoft YaHei UI", 9, "Bold")
    $btnChangePwd.Add_Click({
        $oldPwd = $txtOldPwd.Text
        $newPwd = $txtNewPwd.Text
        $confirmPwd = $txtConfirmPwd.Text

        if ([string]::IsNullOrWhiteSpace($newPwd)) {
            $lblPwdStatus.ForeColor = [System.Drawing.Color]::FromArgb(243, 139, 168)
            $lblPwdStatus.Text = "请输入新密码"; return
        }
        if ($newPwd.Length -lt 8) {
            $lblPwdStatus.ForeColor = [System.Drawing.Color]::FromArgb(243, 139, 168)
            $lblPwdStatus.Text = "密码长度至少8位"; return
        }
        if ($newPwd -ne $confirmPwd) {
            $lblPwdStatus.ForeColor = [System.Drawing.Color]::FromArgb(243, 139, 168)
            $lblPwdStatus.Text = "两次输入的密码不一致"; return
        }

        try {
            $username = $script:user.UserName
            $result = net user $username $newPwd 2>&1
            if ($LASTEXITCODE -eq 0) {
                $lblPwdStatus.ForeColor = [System.Drawing.Color]::FromArgb(166, 227, 161)
                $lblPwdStatus.Text = "密码修改成功"
                $txtOldPwd.Text = ""
                $txtNewPwd.Text = ""
                $txtConfirmPwd.Text = ""
            } else {
                $lblPwdStatus.ForeColor = [System.Drawing.Color]::FromArgb(243, 139, 168)
                $lblPwdStatus.Text = "修改失败：$result"
            }
        } catch {
            $lblPwdStatus.ForeColor = [System.Drawing.Color]::FromArgb(243, 139, 168)
            $lblPwdStatus.Text = "修改失败：$_"
        }
    })
    $form.Controls.Add($btnChangePwd)
    $yPos += 40

    # -- 存储用量区 --
    $secStorage = New-Object System.Windows.Forms.Label
    $secStorage.Text = "存储用量"
    $secStorage.ForeColor = [System.Drawing.Color]::FromArgb(137, 180, 250)
    $secStorage.Font = New-Object System.Drawing.Font("Microsoft YaHei UI", 10, "Bold")
    $secStorage.Location = New-Object System.Drawing.Point(20, $yPos)
    $secStorage.AutoSize = $true
    $form.Controls.Add($secStorage)
    $yPos += 25

    $lblPersonalSize = New-Object System.Windows.Forms.Label
    $personalPath = "D:\Users\$($script:user.UserName)\"
    try {
        $pBytes = (Get-ChildItem -Path $personalPath -Recurse -File -ErrorAction SilentlyContinue |
            Measure-Object -Property Length -Sum).Sum
        if ($null -eq $pBytes) { $pBytes = 0 }
        $personalGB = "{0:N1} GB" -f ($pBytes / 1GB)
    } catch { $personalGB = "无法计算" }
    $lblPersonalSize.Text = "个人文件夹 ($personalPath)：$personalGB"
    $lblPersonalSize.ForeColor = [System.Drawing.Color]::FromArgb(205, 214, 244)
    $lblPersonalSize.Location = New-Object System.Drawing.Point(30, $yPos)
    $lblPersonalSize.AutoSize = $true
    $form.Controls.Add($lblPersonalSize)
    $yPos += 22

    $lblGroupSize = New-Object System.Windows.Forms.Label
    if ($script:user.GroupFolder -and (Test-Path $script:user.GroupFolder)) {
        try {
            $gBytes = (Get-ChildItem -Path $script:user.GroupFolder -Recurse -File -ErrorAction SilentlyContinue |
                Measure-Object -Property Length -Sum).Sum
            if ($null -eq $gBytes) { $gBytes = 0 }
            $groupGB = "{0:N1} GB" -f ($gBytes / 1GB)
        } catch { $groupGB = "无法计算" }
        $lblGroupSize.Text = "组内文件夹 ($($script:user.GroupFolder)\)：$groupGB"
    } else {
        $lblGroupSize.Text = "组内文件夹：未分配"
    }
    $lblGroupSize.ForeColor = [System.Drawing.Color]::FromArgb(205, 214, 244)
    $lblGroupSize.Location = New-Object System.Drawing.Point(30, $yPos)
    $lblGroupSize.AutoSize = $true
    $form.Controls.Add($lblGroupSize)
    $yPos += 28

    $btnRefresh = New-Object System.Windows.Forms.Button
    $btnRefresh.Text = "刷新"
    $btnRefresh.Size = New-Object System.Drawing.Size(70, 26)
    $btnRefresh.Location = New-Object System.Drawing.Point(40, $yPos)
    $btnRefresh.BackColor = [System.Drawing.Color]::FromArgb(49, 50, 68)
    $btnRefresh.ForeColor = [System.Drawing.Color]::FromArgb(205, 214, 244)
    $btnRefresh.FlatStyle = "Flat"
    $btnRefresh.Add_Click({
        $lblPersonalSize.Text = "个人文件夹：计算中..."
        $form.Update()
        try {
            $pBytes = (Get-ChildItem -Path $personalPath -Recurse -File -ErrorAction SilentlyContinue |
                Measure-Object -Property Length -Sum).Sum
            $lblPersonalSize.Text = "个人文件夹 ($personalPath)：$("{0:N1} GB" -f ($pBytes / 1GB))"
        } catch { $lblPersonalSize.Text = "个人文件夹：无法计算" }
        if ($script:user.GroupFolder -and (Test-Path $script:user.GroupFolder)) {
            $lblGroupSize.Text = "组内文件夹：计算中..."
            $form.Update()
            try {
                $gBytes = (Get-ChildItem -Path $script:user.GroupFolder -Recurse -File -ErrorAction SilentlyContinue |
                    Measure-Object -Property Length -Sum).Sum
                $lblGroupSize.Text = "组内文件夹 ($($script:user.GroupFolder)\)：$("{0:N1} GB" -f ($gBytes / 1GB))"
            } catch { $lblGroupSize.Text = "组内文件夹：无法计算" }
        }
    })
    $form.Controls.Add($btnRefresh)
    $yPos += 38

    # -- 最近审计日志 --
    $secAudit = New-Object System.Windows.Forms.Label
    $secAudit.Text = "最近操作记录"
    $secAudit.ForeColor = [System.Drawing.Color]::FromArgb(137, 180, 250)
    $secAudit.Font = New-Object System.Drawing.Font("Microsoft YaHei UI", 10, "Bold")
    $secAudit.Location = New-Object System.Drawing.Point(20, $yPos)
    $secAudit.AutoSize = $true
    $form.Controls.Add($secAudit)
    $yPos += 25

    $auditBox = New-Object System.Windows.Forms.RichTextBox
    $auditBox.Location = New-Object System.Drawing.Point(20, $yPos)
    $auditBox.Size = New-Object System.Drawing.Size(440, 100)
    $auditBox.ReadOnly = $true
    $auditBox.BackColor = [System.Drawing.Color]::FromArgb(40, 42, 58)
    $auditBox.ForeColor = [System.Drawing.Color]::FromArgb(205, 214, 244)
    $auditBox.BorderStyle = "None"
    $auditBox.Font = New-Object System.Drawing.Font("Microsoft YaHei UI", 8.5)
    $auditBox.ScrollBars = "Vertical"
    $logPath = "D:\GroupData\_公共\_使用手册\admin_operations.log"
    try {
        if (Test-Path $logPath) {
            $allLines = Get-Content $logPath -ErrorAction SilentlyContinue
            $userLines = $allLines | Where-Object { $_ -match $script:user.UserName } |
                Select-Object -Last 20
            $auditBox.Text = ($userLines -join "`r`n")
        } else {
            $auditBox.Text = "日志文件不存在"
        }
    } catch {
        $auditBox.Text = "无法读取日志"
    }
    $form.Controls.Add($auditBox)
    $yPos += 110

    # -- 关闭按钮 --
    $closeBtn = New-Object System.Windows.Forms.Button
    $closeBtn.Text = "关闭"
    $closeBtn.Size = New-Object System.Drawing.Size(100, 32)
    $closeBtn.Location = New-Object System.Drawing.Point(195, $yPos)
    $closeBtn.BackColor = [System.Drawing.Color]::FromArgb(137, 180, 250)
    $closeBtn.ForeColor = [System.Drawing.Color]::FromArgb(30, 30, 46)
    $closeBtn.FlatStyle = "Flat"
    $closeBtn.Font = New-Object System.Drawing.Font("Microsoft YaHei UI", 10, "Bold")
    $closeBtn.Add_Click({ $form.Close() })
    $form.Controls.Add($closeBtn)

    $form.ShowDialog() | Out-Null
    $form.Dispose()
}

# ==================== 通知轮询与弹窗 ====================
function Check-Notifications {
    $notifPath = "D:\GroupData\_公共\_notifications\pending"
    try {
        if (-not (Test-Path $notifPath)) { return }
        $files = Get-ChildItem -Path $notifPath -Filter "*.json" -ErrorAction SilentlyContinue
        foreach ($file in $files) {
            $notifId = $file.BaseName
            if ($script:shownNotifications -contains $notifId) { continue }

            try {
                $json = Get-Content $file.FullName -Raw -ErrorAction Stop | ConvertFrom-Json
            } catch { continue }

            $title      = if ($json.title)      { $json.title }      else { "通知" }
            $message    = if ($json.message)    { $json.message }    else { "" }
            $importance = if ($json.importance) { $json.importance } else { "普通" }

            # 系统托盘气泡通知
            $tipIcon = switch ($importance) {
                "紧急" { "Error" }
                "重要" { "Warning" }
                default { "Info" }
            }
            $script:notifyIcon.ShowBalloonTip(5000, $title, $message, $tipIcon)

            # 紧急通知额外弹窗
            if ($importance -eq "紧急") {
                Show-NotificationPopup -Title $title -Message $message -Importance $importance
            }

            $script:shownNotifications += $notifId
        }
    } catch {
        # 静默处理错误（文件夹不存在等）
    }
}

function Show-NotificationPopup {
    param(
        [string]$Title = "通知",
        [string]$Message = "",
        [string]$Importance = "普通"
    )

    $popup = New-Object System.Windows.Forms.Form
    $popup.Text          = $Title
    $popup.Size          = New-Object System.Drawing.Size(380, 220)
    $popup.StartPosition = "Manual"
    $popup.FormBorderStyle = "FixedDialog"
    $popup.MaximizeBox   = $false
    $popup.MinimizeBox   = $false
    $popup.TopMost       = $true
    $popup.BackColor     = [System.Drawing.Color]::FromArgb(30, 30, 46)
    $popup.Font          = New-Object System.Drawing.Font("Microsoft YaHei UI", 9.5)

    # 位置：右下角
    $screen = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
    $popup.Location = New-Object System.Drawing.Point(
        ($screen.Right - 400), ($screen.Bottom - 240))

    # 重要程度颜色
    $impColor = switch ($Importance) {
        "紧急" { [System.Drawing.Color]::FromArgb(243, 139, 168) }  # 红色
        "重要" { [System.Drawing.Color]::FromArgb(249, 226, 175) }  # 黄色
        default { [System.Drawing.Color]::FromArgb(147, 153, 178) } # 灰色
    }

    # 标题（带重要程度标识）
    $titleLabel = New-Object System.Windows.Forms.Label
    $titleLabel.Text = "[$Importance] $Title"
    $titleLabel.ForeColor = $impColor
    $titleLabel.Font = New-Object System.Drawing.Font("Microsoft YaHei UI", 12, "Bold")
    $titleLabel.Location = New-Object System.Drawing.Point(15, 15)
    $titleLabel.AutoSize = $true
    $popup.Controls.Add($titleLabel)

    # 消息内容
    $msgLabel = New-Object System.Windows.Forms.Label
    $msgLabel.Text = $Message
    $msgLabel.ForeColor = [System.Drawing.Color]::FromArgb(205, 214, 244)
    $msgLabel.Font = New-Object System.Drawing.Font("Microsoft YaHei UI", 10)
    $msgLabel.Location = New-Object System.Drawing.Point(15, 50)
    $msgLabel.MaximumSize = New-Object System.Drawing.Size(330, 80)
    $msgLabel.AutoSize = $true
    $popup.Controls.Add($msgLabel)

    # 关闭按钮
    $closeBtn = New-Object System.Windows.Forms.Button
    $closeBtn.Text = "我知道了"
    $closeBtn.Size = New-Object System.Drawing.Size(100, 30)
    $closeBtn.Location = New-Object System.Drawing.Point(135, 145)
    $closeBtn.BackColor = [System.Drawing.Color]::FromArgb(137, 180, 250)
    $closeBtn.ForeColor = [System.Drawing.Color]::FromArgb(30, 30, 46)
    $closeBtn.FlatStyle = "Flat"
    $closeBtn.Font = New-Object System.Drawing.Font("Microsoft YaHei UI", 9, "Bold")
    $closeBtn.Add_Click({ $popup.Close() })
    $popup.Controls.Add($closeBtn)

    # 非紧急通知30秒自动关闭
    $autoCloseTimer = $null
    if ($Importance -ne "紧急") {
        $autoCloseTimer = New-Object System.Windows.Forms.Timer
        $autoCloseTimer.Interval = 30000
        $autoCloseTimer.Add_Tick({
            if (-not $popup.IsDisposed) { $popup.Close() }
            $autoCloseTimer.Stop()
        })
        $autoCloseTimer.Start()
    }

    $popup.ShowDialog() | Out-Null
    if ($autoCloseTimer) { $autoCloseTimer.Stop(); $autoCloseTimer.Dispose() }
    $popup.Dispose()
}

# ==================== 启动 ====================

$script:notifyIcon.ShowBalloonTip(3000,
    "课题组工作站",
    "悬浮导航已就绪。拖拽移动，点击按钮打开文件夹。`n$($script:user.UserName)$(if ($script:user.AdvisorName) { " · $($script:user.AdvisorName)组" } else { "" })",
    "Info")

# 用悬浮窗作为主消息循环（替代之前的隐藏窗体）
[System.Windows.Forms.Application]::Run($script:widget)
