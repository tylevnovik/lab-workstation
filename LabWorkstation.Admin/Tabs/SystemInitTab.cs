using LabWorkstation.Common.Configuration;
using LabWorkstation.Common.LocalAccounts;
using LabWorkstation.Common.Desktop;

// 注意：LabWorkstation.Common.System 命名空间会遮蔽全局 System 命名空间，
// 因此用别名引用，调用时写 SysAdmin.XxxManager。不直接 using 该命名空间。
using SysAdmin = LabWorkstation.Common.System;

namespace LabWorkstation.Admin.Tabs;

/// <summary>
/// 系统初始化面板。把原 PowerShell 脚本的分步行为回归到 C# 应用：
/// 目录/权限、全员组、桌面环境、SMB 共享、系统策略、守护程序部署。
/// 提供「分步执行」与「一键全量初始化」两种入口。
/// </summary>
public class SystemInitTab : UserControl
{
    private readonly IAppShell _shell;

    public SystemInitTab(IAppShell shell)
    {
        _shell = shell;
        Text = "系统初始化";

        // 整体可滚动容器
        var content = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            Padding = new Padding(0, 0, 20, 0)
        };
        Controls.Add(content);

        var intro = new Label
        {
            Text = "系统初始化：分步执行各模块以替代原 PowerShell 脚本。首次部署建议直接点击底部「一键初始化全部」。",
            Location = new Point(15, 10),
            MaximumSize = new Size(730, 0),
            AutoSize = true,
            ForeColor = Color.FromArgb(0, 102, 179),
            Font = new Font("Microsoft YaHei UI", 9F)
        };
        content.Controls.Add(intro);

        int y = 45;

        // ── GroupBox 1: 目录结构与权限 ──────────────────────────
        var gb1 = new GroupBox
        {
            Text = "目录结构与权限",
            Location = new Point(15, y),
            Size = new Size(745, 0),
            Font = new Font("Microsoft YaHei UI", 9.5F, FontStyle.Bold)
        };
        content.Controls.Add(gb1);
        int by = 28;
        by = AddAction(gb1, "初始化目录骨架",
            "创建数据区根/公共/通知/归档/Users 等基础目录",
            by, (_, _) => BtnInitDirectorySkeleton());
        by = AddAction(gb1, "配置数据区权限",
            "初始化根/公共/个人目录根 ACL（Lab_All 隔离）",
            by, (_, _) => BtnInitDataAreaAcl());
        by = AddAction(gb1, "创建全员组",
            $"创建 {LabConfig.AllGroup} 全员组（已存在则跳过）",
            by, (_, _) => BtnCreateAllGroup());
        by = AddAction(gb1, "配置Profile目录到D盘",
            "将系统 ProfilesDirectory 设为 D:\\Users，新建用户配置文件存放数据盘",
            by, (_, _) => BtnConfigureProfilesDir());
        by = AddAction(gb1, "收紧硬盘根目录权限",
            "C:\\ 清理非内置账户残留；D:\\ 将 Authenticated Users 降为只读（子目录不受影响）",
            by, (_, _) => BtnHardenDriveRoots());
        gb1.Height = by + 12;
        y += gb1.Height + 10;

        // ── GroupBox 2: 桌面环境 ────────────────────────────────
        var gb2 = new GroupBox
        {
            Text = "桌面环境",
            Location = new Point(15, y),
            Size = new Size(745, 0),
            Font = new Font("Microsoft YaHei UI", 9.5F, FontStyle.Bold)
        };
        content.Controls.Add(gb2);
        by = 28;
        by = AddAction(gb2, "部署壁纸+锁定",
            "部署壁纸文件，设置 Default User/当前会话壁纸并锁定组策略",
            by, (_, _) => BtnDeployWallpaper());
        by = AddAction(gb2, "创建桌面快捷方式",
            "创建 4 个公共桌面快捷方式与使用须知文件",
            by, (_, _) => BtnCreateShortcuts());
        by = AddAction(gb2, "保护桌面只读",
            "将公共桌面设为只读，防止标准用户删除桌面项目",
            by, (_, _) => BtnProtectDesktop());
        gb2.Height = by + 12;
        y += gb2.Height + 10;

        // ── GroupBox 3: 共享与系统策略 ──────────────────────────
        var gb3 = new GroupBox
        {
            Text = "共享与系统策略",
            Location = new Point(15, y),
            Size = new Size(745, 0),
            Font = new Font("Microsoft YaHei UI", 9.5F, FontStyle.Bold)
        };
        content.Controls.Add(gb3);
        by = 28;
        by = AddAction(gb3, "创建SMB共享",
            $"确保共享 '{LabConfig.SmbShareName}' 指向 {LabConfig.SharePath}",
            by, (_, _) => BtnCreateSmbShare());
        by = AddAction(gb3, "修复RDP授权",
            "改为每用户授权模式 + 清除宽限期时间炸弹（解决RDP许可证错误）",
            by, (_, _) => BtnFixRdpLicensing());
        by = AddAction(gb3, "应用系统策略",
            "RDP 超时 / Windows Update / 禁用安装 / 磁盘配额",
            by, (_, _) => BtnApplySystemPolicy());
        gb3.Height = by + 12;
        y += gb3.Height + 10;

        // ── GroupBox 4: 守护程序部署 ────────────────────────────
        var gb4 = new GroupBox
        {
            Text = "守护程序部署",
            Location = new Point(15, y),
            Size = new Size(745, 0),
            Font = new Font("Microsoft YaHei UI", 9.5F, FontStyle.Bold)
        };
        content.Controls.Add(gb4);
        by = 28;
        by = AddAction(gb4, "部署资源监控",
            "部署 LabWorkstation.Monitor 并注册开机自启计划任务",
            by, (_, _) => BtnDeployMonitor());
        by = AddAction(gb4, "部署悬浮导航",
            "部署 LabWorkstation.TrayApp 并设为开机自启",
            by, (_, _) => BtnDeployTrayApp());
        by = AddAction(gb4, "部署Kiosk自助开户",
            "部署开户终端应用+创建kiosk账户+配置自定义Shell+开机自动登录",
            by, (_, _) => BtnDeployKiosk());
        gb4.Height = by + 12;
        y += gb4.Height + 10;

        // ── 底部：一键全量初始化 ────────────────────────────────
        var initAllBtn = new Button
        {
            Text = "一键初始化全部",
            Location = new Point(15, y),
            Size = new Size(430, 50),
            BackColor = Color.FromArgb(0, 102, 179),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Microsoft YaHei UI", 11F, FontStyle.Bold)
        };
        initAllBtn.Click += (_, _) => InitAll();
        content.Controls.Add(initAllBtn);
    }

    /// <summary>在 GroupBox 内追加一行「按钮 + 说明」，返回下一行的起始 Y。</summary>
    private int AddAction(GroupBox parent, string text, string desc, int y, EventHandler onClick)
    {
        var btn = new Button
        {
            Text = text,
            Location = new Point(15, y),
            Size = new Size(180, 34),
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Microsoft YaHei UI", 9F)
        };
        btn.Click += onClick;
        parent.Controls.Add(btn);

        var descLabel = new Label
        {
            Text = desc,
            Location = new Point(205, y + 9),
            MaximumSize = new Size(520, 0),
            AutoSize = true,
            ForeColor = Color.Gray,
            Font = new Font("Microsoft YaHei UI", 8.5F)
        };
        parent.Controls.Add(descLabel);

        return y + 46;
    }

    // ── 分步按钮 Click 处理（每个都 try/catch 包裹，成功/失败都写日志）──────

    private void BtnInitDirectorySkeleton()
    {
        try
        {
            NtfsAclHelper.CreateDirectorySkeleton();
            _shell.Log("目录骨架初始化完成");
        }
        catch (Exception ex) { _shell.Log($"目录骨架初始化失败: {ex.Message}", "ERROR"); }
    }

    private void BtnInitDataAreaAcl()
    {
        try
        {
            NtfsAclHelper.InitShareRootAcl();
            NtfsAclHelper.InitPublicAreaAcl();
            NtfsAclHelper.InitUsersRootAcl();
            _shell.Log("数据区权限配置完成（根/公共/个人目录根）");
        }
        catch (Exception ex) { _shell.Log($"数据区权限配置失败: {ex.Message}", "ERROR"); }
    }

    private void BtnCreateAllGroup()
    {
        try
        {
            if (GroupManager.GroupExists(LabConfig.AllGroup))
            {
                _shell.Log($"全员组 '{LabConfig.AllGroup}' 已存在，跳过创建");
            }
            else
            {
                GroupManager.CreateGroup(LabConfig.AllGroup);
                _shell.Log($"全员组 '{LabConfig.AllGroup}' 已创建");
            }

            // 将 Lab_All 加入 Remote Desktop Users 组，使所有课题组成员可通过 RDP 登录
            try
            {
                const string rdpGroup = "Remote Desktop Users";
                if (GroupManager.GroupExists(rdpGroup) && !GroupManager.IsMember(rdpGroup, LabConfig.AllGroup))
                {
                    GroupManager.AddMember(rdpGroup, LabConfig.AllGroup);
                    _shell.Log($"全员组已加入 '{rdpGroup}'（课题组成员可通过 RDP 登录）");
                }
            }
            catch (Exception ex) { _shell.Log($"加入 Remote Desktop Users 组失败: {ex.Message}", "WARN"); }
        }
        catch (Exception ex) { _shell.Log($"创建全员组失败: {ex.Message}", "ERROR"); }
    }

    private void BtnConfigureProfilesDir()
    {
        try
        {
            SysAdmin.SystemPolicyManager.ConfigureProfilesDirectory();
            _shell.Log($"ProfilesDirectory 已配置为 {LabConfig.DesiredProfilesDirectory}（新建用户 Profile 将存放在数据盘）");
        }
        catch (Exception ex) { _shell.Log($"配置 ProfilesDirectory 失败: {ex.Message}", "ERROR"); }
    }

    private void BtnHardenDriveRoots()
    {
        try
        {
            NtfsAclHelper.HardenDriveRoots();
            _shell.Log("硬盘根目录权限已收紧（C:\\ 清理残留；D:\\ 普通用户只读）");
        }
        catch (Exception ex) { _shell.Log($"收紧硬盘根目录权限失败: {ex.Message}", "ERROR"); }
    }

    private void BtnDeployWallpaper()
    {
        try
        {
            WallpaperManager.DeployWallpaperFile();
            WallpaperManager.SetDefaultUserWallpaper();
            WallpaperManager.LockWallpaperPolicy();
            WallpaperManager.SetCurrentWallpaper(LabConfig.WallpaperPath);
            _shell.Log("壁纸部署并锁定完成（当前会话 + Default User + 策略）");
        }
        catch (Exception ex) { _shell.Log($"壁纸部署失败: {ex.Message}", "ERROR"); }
    }

    private void BtnCreateShortcuts()
    {
        try
        {
            ShortcutHelper.CreateCommonDesktopShortcuts();
            ShortcutHelper.CreateDesktopReadme();
            _shell.Log("桌面快捷方式与使用须知文件已创建");
        }
        catch (Exception ex) { _shell.Log($"创建桌面快捷方式失败: {ex.Message}", "ERROR"); }
    }

    private void BtnProtectDesktop()
    {
        try
        {
            DesktopProtector.ProtectCommonDesktop();
            _shell.Log("公共桌面已设为只读保护");
        }
        catch (Exception ex) { _shell.Log($"保护桌面只读失败: {ex.Message}", "ERROR"); }
    }

    private void BtnCreateSmbShare()
    {
        try
        {
            SysAdmin.SmbShareManager.EnsureShare();
            _shell.Log($"SMB 共享 '{LabConfig.SmbShareName}' 已确保存在");
        }
        catch (Exception ex) { _shell.Log($"创建 SMB 共享失败: {ex.Message}", "ERROR"); }
    }

    private void BtnFixRdpLicensing()
    {
        try
        {
            SysAdmin.SystemPolicyManager.FixRdpLicensing();
            _shell.Log("RDP 授权已修复（每用户模式 + 宽限期已清除），重启 TermService 后生效");
        }
        catch (Exception ex) { _shell.Log($"修复 RDP 授权失败: {ex.Message}", "ERROR"); }
    }

    private void BtnApplySystemPolicy()
    {
        try
        {
            SysAdmin.SystemPolicyManager.ApplyAll();
            _shell.Log("系统策略已全部应用（RDP/Update/安装/配额）");
        }
        catch (Exception ex) { _shell.Log($"应用系统策略失败: {ex.Message}", "ERROR"); }
    }

    private void BtnDeployMonitor()
    {
        try
        {
            SysAdmin.MonitorDeployer.Deploy();
            _shell.Log("资源监控守护程序已部署并注册开机自启");
        }
        catch (Exception ex) { _shell.Log($"部署资源监控失败: {ex.Message}", "ERROR"); }
    }

    private void BtnDeployTrayApp()
    {
        try
        {
            SysAdmin.TrayAppDeployer.Deploy();
            _shell.Log("悬浮导航已部署并设为开机自启");
        }
        catch (Exception ex) { _shell.Log($"部署悬浮导航失败: {ex.Message}", "ERROR"); }
    }

    private void BtnDeployKiosk()
    {
        try
        {
            SysAdmin.KioskDeployer.Deploy();
            _shell.Log("Kiosk 自助开户系统已部署（kiosk账户+自定义Shell+自动登录）");
            _shell.Log("重启后系统将自动以 kiosk 身份登录并显示自助开户界面");
            _shell.Log("管理员可通过远程桌面以管理员身份登录（会覆盖自动登录）", "WARN");
        }
        catch (Exception ex) { _shell.Log($"部署 Kiosk 失败: {ex.Message}", "ERROR"); }
    }

    // ── 一键全量初始化 ──────────────────────────────────────────
    /// <summary>
    /// 按依赖顺序执行全部初始化步骤。每步通过 <paramref name="log"/> 回调写进度，
    /// 单步失败立即中断（后续步骤可能依赖前序产物），并提示用户逐项重试。
    /// 顺序：目录骨架 → 全员组 → 数据区权限 → SMB共享 → 壁纸 →
    ///       桌面快捷方式 → 桌面保护 → 系统策略 → Monitor部署 → TrayApp部署。
    /// </summary>
    private void InitAll()
    {
        Action<string> log = msg => _shell.Log(msg);

        log("========== 开始一键初始化 ==========");
        int ok = 0, fail = 0;

        void Step(string name, Action action)
        {
            log($"→ 开始: {name}");
            try
            {
                action();
                log($"  ✓ {name} 完成");
                ok++;
            }
            catch (Exception ex)
            {
                _shell.Log($"  ✗ {name} 失败: {ex.Message}", "ERROR");
                fail++;
                throw new InvalidOperationException($"步骤『{name}』失败", ex);
            }
        }

        try
        {
            Step("初始化目录骨架", () => NtfsAclHelper.CreateDirectorySkeleton());

            Step("创建全员组", () =>
            {
                if (!GroupManager.GroupExists(LabConfig.AllGroup))
                    GroupManager.CreateGroup(LabConfig.AllGroup);
                // 将 Lab_All 加入 Remote Desktop Users，使课题组成员可通过 RDP 登录
                const string rdpGroup = "Remote Desktop Users";
                if (GroupManager.GroupExists(rdpGroup) && !GroupManager.IsMember(rdpGroup, LabConfig.AllGroup))
                    GroupManager.AddMember(rdpGroup, LabConfig.AllGroup);
            });

            Step("配置Profile目录到D盘", () => SysAdmin.SystemPolicyManager.ConfigureProfilesDirectory());

            Step("收紧硬盘根目录权限", () => NtfsAclHelper.HardenDriveRoots());

            Step("配置数据区权限", () =>
            {
                NtfsAclHelper.InitShareRootAcl();
                NtfsAclHelper.InitPublicAreaAcl();
                NtfsAclHelper.InitUsersRootAcl();
            });

            Step("创建SMB共享", () => SysAdmin.SmbShareManager.EnsureShare());

            Step("修复RDP授权", () => SysAdmin.SystemPolicyManager.FixRdpLicensing());

            Step("部署壁纸", () =>
            {
                WallpaperManager.DeployWallpaperFile();
                WallpaperManager.SetDefaultUserWallpaper();
                WallpaperManager.LockWallpaperPolicy();
                WallpaperManager.SetCurrentWallpaper(LabConfig.WallpaperPath);
            });

            Step("创建桌面快捷方式", () =>
            {
                ShortcutHelper.CreateCommonDesktopShortcuts();
                ShortcutHelper.CreateDesktopReadme();
            });

            Step("保护桌面只读", () => DesktopProtector.ProtectCommonDesktop());

            Step("应用系统策略", () => SysAdmin.SystemPolicyManager.ApplyAll());

            Step("部署资源监控", () => SysAdmin.MonitorDeployer.Deploy());

            Step("部署悬浮导航", () => SysAdmin.TrayAppDeployer.Deploy());

            Step("部署Kiosk自助开户", () => SysAdmin.KioskDeployer.Deploy());

            log($"========== 一键初始化完成（成功 {ok} 步）==========");
            MessageBox.Show("一键初始化全部完成，详见操作日志。", "完成",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            log($"========== 一键初始化中断（成功 {ok} 步，失败 {fail} 步）==========");
            MessageBox.Show(
                $"初始化中断：{ex.Message}\n\n已完成 {ok} 步，请查看日志后逐项重试失败步骤。",
                "中断", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
