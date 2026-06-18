# ============================================================
# 课题组工作站初始化脚本（多导师分组版）
# 用途：一键创建文件夹结构 + 配置安全组 + 设置 NTFS/SMB 权限
# 使用方法：以管理员身份运行 PowerShell，执行本脚本
# ============================================================

# ============ 配置区（根据实际情况修改） ============

# 共享数据根路径
$RootPath       = "D:\GroupData"

# 跨组公共区域（所有导师组均可见）
$PublicPath     = "D:\GroupData\_公共"

# 个人目录根路径（每个账户独享，互相隔离）
$UsersRootPath  = "D:\Users"

# 伞安全组（所有用户均应加入此组）
$AllGroup       = "Lab_All"

# 管理员账户
$AdminAccount   = "Administrators"

# 导师列表（按需增减，脚本会自动创建对应的安全组和文件夹）
$AdvisorGroups  = @("张老师")

# 每个导师组下的标准分类子文件夹
$GroupCategories = @(
    "01_人才类数据",
    "02_温故知新数据",
    "03_科技报告",
    "04_资政报告",
    "05_项目资料",
    "99_归档"
)

# 公共区域 —— 工具与模板下的子文件夹
$ToolSubFolders = @(
    "报告模板",
    "数据清洗脚本",
    "可视化工具"
)

# ============ 辅助函数 ============

function Ensure-Directory {
    param([string]$Path, [string]$Label)
    if (-not (Test-Path $Path)) {
        New-Item -ItemType Directory -Path $Path -Force | Out-Null
        Write-Host "[OK] 创建${Label}: $Path" -ForegroundColor Green
    } else {
        Write-Host "[跳过] ${Label}已存在: $Path" -ForegroundColor Yellow
    }
}

function Ensure-LocalGroup {
    param([string]$GroupName)
    $existing = Get-LocalGroup -Name $GroupName -ErrorAction SilentlyContinue
    if ($existing) {
        Write-Host "[跳过] 安全组已存在: $GroupName" -ForegroundColor Yellow
        return $true
    }
    try {
        New-LocalGroup -Name $GroupName -Description "课题组自动创建" | Out-Null
        Write-Host "[OK] 创建安全组: $GroupName" -ForegroundColor Green
        return $true
    } catch {
        Write-Host "[错误] 无法创建安全组 '$GroupName': $_" -ForegroundColor Red
        return $false
    }
}

function Apply-Acl {
    param(
        [string]$Path,
        [System.Security.AccessControl.FileSystemAccessRule[]]$Rules,
        [bool]$BreakInheritance = $true,
        [bool]$Recurse = $false
    )
    $acl = Get-Acl $Path
    if ($BreakInheritance) {
        $acl.SetAccessRuleProtection($true, $false)
    }
    # 清除现有非继承规则，重新添加
    $acl.Access | ForEach-Object { $acl.RemoveAccessRule($_) | Out-Null }
    foreach ($r in $Rules) {
        $acl.AddAccessRule($r)
    }
    Set-Acl -Path $Path -AclObject $acl

    if ($Recurse) {
        Get-ChildItem -Path $Path -Recurse -Directory | ForEach-Object {
            $subAcl = Get-Acl $_.FullName
            $subAcl.SetAccessRuleProtection($true, $false)
            $subAcl.Access | ForEach-Object { $subAcl.RemoveAccessRule($_) | Out-Null }
            foreach ($r in $Rules) {
                $subAcl.AddAccessRule($r)
            }
            Set-Acl -Path $_.FullName -AclObject $subAcl
        }
    }
}

function New-FsRule {
    param(
        [string]$Identity,
        [string]$Rights,
        [string]$Inheritance = "ContainerInherit,ObjectInherit"
    )
    New-Object System.Security.AccessControl.FileSystemAccessRule(
        $Identity, $Rights, $Inheritance, "None", "Allow"
    )
}

# ============================================================
#  第一步：创建文件夹结构
# ============================================================

Write-Host "========================================" -ForegroundColor Cyan
Write-Host " 课题组工作站初始化脚本（多导师分组版）" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# 检查根路径所在磁盘是否存在
$DriveLetter = ($RootPath -split ":")[0] + ":"
if (-not (Test-Path $DriveLetter)) {
    Write-Host "[错误] 磁盘 $DriveLetter 不存在，请检查配置。" -ForegroundColor Red
    exit 1
}

# --- 根目录 ---
Ensure-Directory -Path $RootPath -Label "根目录"

# --- 公共区域 ---
Write-Host ""
Write-Host "---- 公共区域（_公共）----" -ForegroundColor Cyan

Ensure-Directory -Path $PublicPath -Label "公共根目录"
Ensure-Directory -Path (Join-Path $PublicPath "_使用手册") -Label "使用手册目录"
Ensure-Directory -Path (Join-Path $PublicPath "跨组共享数据") -Label "跨组共享数据目录"

$toolsRoot = Join-Path $PublicPath "工具与模板"
Ensure-Directory -Path $toolsRoot -Label "工具与模板目录"

foreach ($sub in $ToolSubFolders) {
    Ensure-Directory -Path (Join-Path $toolsRoot $sub) -Label "工具子目录"
}

# 通知系统目录
Ensure-Directory "$PublicPath\_notifications\pending" -Label "通知待推送目录"
Ensure-Directory "$PublicPath\_notifications\sent" -Label "通知已推送目录"
# 归档目录（离校流程使用）
Ensure-Directory "$PublicPath\99_归档" -Label "公共归档目录"

# --- 各导师组文件夹 ---
Write-Host ""
Write-Host "---- 导师组文件夹 ----" -ForegroundColor Cyan

foreach ($advisor in $AdvisorGroups) {
    $advisorPath = Join-Path $RootPath $advisor
    Ensure-Directory -Path $advisorPath -Label "导师组目录"

    foreach ($cat in $GroupCategories) {
        Ensure-Directory -Path (Join-Path $advisorPath $cat) -Label "分类子目录"
    }
}

# --- 个人目录根 ---
Write-Host ""
Write-Host "---- 个人目录 ----" -ForegroundColor Cyan
Ensure-Directory -Path $UsersRootPath -Label "个人目录根文件夹"

Write-Host ""
Write-Host "文件夹结构创建完成！" -ForegroundColor Green

# ============================================================
#  第二步：创建安全组
# ============================================================

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host " 创建安全组" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

$allGroupOk = Ensure-LocalGroup -GroupName $AllGroup

$advisorGroupStatus = @{}
foreach ($advisor in $AdvisorGroups) {
    $gName = "Lab_$advisor"
    $advisorGroupStatus[$advisor] = Ensure-LocalGroup -GroupName $gName
}

# ============================================================
#  第三步：设置 NTFS 权限
# ============================================================

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host " 配置 NTFS 权限" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# --- 3.1 D:\GroupData 根目录 ---
# Lab_All: Traverse + ReadAttributes（可浏览但不改内容）
# Administrators / SYSTEM: FullControl
Write-Host "[...] 配置根目录权限: $RootPath" -ForegroundColor Cyan

$rootRules = @(
    (New-FsRule $AdminAccount   "FullControl"),
    (New-FsRule "SYSTEM"        "FullControl"),
    (New-FsRule $AllGroup       "Traverse,ReadAttributes" "ContainerInherit")
)
Apply-Acl -Path $RootPath -Rules $rootRules -BreakInheritance $true -Recurse $false
Write-Host "[OK] 根目录权限已设置" -ForegroundColor Green

# --- 3.2 D:\GroupData\_公共 ---
# Lab_All: Modify（递归），Administrators: FullControl，SYSTEM: FullControl
Write-Host "[...] 配置公共区域权限: $PublicPath（递归）" -ForegroundColor Cyan

$publicRules = @(
    (New-FsRule $AdminAccount "FullControl"),
    (New-FsRule "SYSTEM"      "FullControl"),
    (New-FsRule $AllGroup     "Modify")
)
Apply-Acl -Path $PublicPath -Rules $publicRules -BreakInheritance $true -Recurse $true
Write-Host "[OK] 公共区域权限已设置（递归）" -ForegroundColor Green

# --- 3.3 D:\GroupData\[导师名] ---
# 仅对应导师组 Lab_[导师名] 拥有 Modify，Administrators: FullControl
foreach ($advisor in $AdvisorGroups) {
    $advisorPath = Join-Path $RootPath $advisor
    $groupName   = "Lab_$advisor"

    if (-not $advisorGroupStatus[$advisor]) {
        Write-Host "[跳过] 安全组 '$groupName' 未就绪，跳过权限设置: $advisorPath" -ForegroundColor Yellow
        continue
    }

    Write-Host "[...] 配置导师组权限: $advisorPath（递归，组: $groupName）" -ForegroundColor Cyan

    $advisorRules = @(
        (New-FsRule $AdminAccount "FullControl"),
        (New-FsRule "SYSTEM"      "FullControl"),
        (New-FsRule $groupName    "Modify")
    )
    Apply-Acl -Path $advisorPath -Rules $advisorRules -BreakInheritance $true -Recurse $true
    Write-Host "[OK] 导师组 '$advisor' 权限已设置（递归）" -ForegroundColor Green
}

# --- 3.4 D:\Users ---
# Administrators: FullControl，SYSTEM: FullControl，Lab_All: Traverse only
Write-Host "[...] 配置个人目录根权限: $UsersRootPath" -ForegroundColor Cyan

$usersRules = @(
    (New-FsRule $AdminAccount "FullControl"),
    (New-FsRule "SYSTEM"      "FullControl"),
    (New-FsRule $AllGroup     "Traverse,ReadAttributes" "ContainerInherit")
)
Apply-Acl -Path $UsersRootPath -Rules $usersRules -BreakInheritance $true -Recurse $false
Write-Host "[OK] 个人目录根权限已设置（各账户互相隔离）" -ForegroundColor Green

Write-Host ""
Write-Host "NTFS 权限配置完成！" -ForegroundColor Green

# ============================================================
#  第四步：设置 SMB 共享
# ============================================================

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host " 配置 SMB 共享" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

if ($allGroupOk) {
    # 移除可能存在的旧共享
    $existingShare = Get-SmbShare -Name "GroupData" -ErrorAction SilentlyContinue
    if ($existingShare) {
        Remove-SmbShare -Name "GroupData" -Force
        Write-Host "[OK] 移除旧的 SMB 共享" -ForegroundColor Green
    }

    # 创建 SMB 共享：Lab_All 拥有更改权限，Administrators 拥有完全控制
    New-SmbShare -Name "GroupData" -Path $RootPath `
        -ChangeAccess $AllGroup `
        -FullAccess $AdminAccount `
        -Description "课题组数据共享（多导师分组）" | Out-Null
    Write-Host "[OK] 创建 SMB 共享: GroupData -> $RootPath" -ForegroundColor Green
    Write-Host "    共享权限: $AllGroup = 更改, $AdminAccount = 完全控制" -ForegroundColor Gray
} else {
    Write-Host "[跳过] 安全组 '$AllGroup' 未就绪，无法创建 SMB 共享" -ForegroundColor Yellow
    Write-Host "  请手动创建安全组后运行: net localgroup $AllGroup /add" -ForegroundColor Yellow
}

# ============================================================
#  第五步：检查 Python 工具 (uv)
# ============================================================

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host " 检查 Python 工具 (uv)" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

$uvExists = Get-Command uv -ErrorAction SilentlyContinue
if ($uvExists) {
    $uvVer = uv --version
    Write-Host "[OK] uv 已安装: $uvVer" -ForegroundColor Green
    # 检查 uv 管理的 Python
    $pythonList = uv python list 2>$null
    if ($pythonList) {
        Write-Host "[OK] uv 可管理的 Python 版本:" -ForegroundColor Green
        uv python list | Select-Object -First 5 | ForEach-Object {
            Write-Host "    $_" -ForegroundColor Gray
        }
    }
} else {
    Write-Host "[未安装] 安装 uv（一站式 Python 管理工具）:" -ForegroundColor Yellow
    Write-Host '  powershell -ExecutionPolicy ByPass -c "irm https://astral.sh/uv/install.ps1 | iex"' -ForegroundColor Yellow
    Write-Host ""
    Write-Host "  安装后可通过 uv python install 3.11 等方式安装任意 Python 版本" -ForegroundColor Yellow
}

# ============================================================
#  完成 —— 后续步骤提示
# ============================================================

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host " 初始化完成！" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "共享路径: \\$env:COMPUTERNAME\GroupData" -ForegroundColor White
Write-Host ""

Write-Host "文件夹结构总览:" -ForegroundColor White
Write-Host "  $RootPath" -ForegroundColor Gray
Write-Host "  ├── _公共\                      ← 所有组可见" -ForegroundColor Gray
Write-Host "  │   ├── _使用手册\" -ForegroundColor Gray
Write-Host "  │   ├── 工具与模板\" -ForegroundColor Gray
Write-Host "  │   ├── 跨组共享数据\" -ForegroundColor Gray
Write-Host "  │   ├── _notifications\pending\     ← 待推送通知" -ForegroundColor Gray
Write-Host "  │   ├── _notifications\sent\        ← 已推送通知" -ForegroundColor Gray
Write-Host "  │   └── 99_归档\                    ← 离校用户数据归档" -ForegroundColor Gray
foreach ($advisor in $AdvisorGroups) {
    Write-Host "  ├── $advisor\                   ← 仅 Lab_$advisor 可见" -ForegroundColor Gray
}
Write-Host "  $UsersRootPath\                      ← 个人账户（互相隔离）" -ForegroundColor Gray
Write-Host ""

Write-Host "安全组总览:" -ForegroundColor White
Write-Host "  $AllGroup          —— 伞组，所有用户均应加入" -ForegroundColor Gray
foreach ($advisor in $AdvisorGroups) {
    Write-Host "  Lab_$advisor     —— 仅 '$advisor' 导师组成员加入" -ForegroundColor Gray
}
Write-Host ""

Write-Host "后续步骤:" -ForegroundColor White
Write-Host "  1. 将所有成员账户加入 '$AllGroup' 安全组" -ForegroundColor White
foreach ($advisor in $AdvisorGroups) {
    Write-Host "  2. 将 '$advisor' 导师组成员加入 'Lab_$advisor' 安全组" -ForegroundColor White
}
Write-Host "  3. 将《工作站使用手册》复制到: $PublicPath\_使用手册\" -ForegroundColor White
Write-Host "  4. 考虑启用 BitLocker 加密数据盘" -ForegroundColor White
Write-Host "  5. 配置定期备份任务" -ForegroundColor White
Write-Host ""
