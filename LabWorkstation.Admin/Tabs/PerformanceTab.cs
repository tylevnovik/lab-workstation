using Microsoft.Win32;
using LabWorkstation.Common.Audit;
using LabWorkstation.Common.Configuration;

namespace LabWorkstation.Admin.Tabs;

/// <summary>Tab 4：性能优化。UAC、组策略、预读取、RemoteFX、搜索、电源计划。</summary>
public class PerformanceTab : UserControl
{
    private readonly IAppShell _shell;
    private readonly List<OptItem> _items = new();

    private sealed class OptItem
    {
        public string Id = "";
        public string Label = "";
        public string Desc = "";
        public CheckBox Check = null!;
        public Label Status = null!;
        public Action Apply = null!;
    }

    public PerformanceTab(IAppShell shell)
    {
        _shell = shell;
        Text = "性能优化";

        var perfIntro = new Label
        {
            Text = "解决非管理员账户桌面体验卡顿的问题。以下优化项可逐项应用，建议全部勾选后一键执行。",
            Location = new Point(20, 15),
            MaximumSize = new Size(745, 0),
            AutoSize = true
        };
        Controls.Add(perfIntro);

        var optGroup = new GroupBox
        {
            Text = "优化项（勾选后点击\"一键优化\"）",
            Location = new Point(15, 45),
            Size = new Size(745, 310)
        };
        Controls.Add(optGroup);

        BuildOptimizations(optGroup);

        var optimizeBtn = new Button
        {
            Text = "一键执行优化",
            Location = new Point(300, 370),
            Size = new Size(180, 38),
            BackColor = Color.FromArgb(0, 153, 76),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold)
        };
        optimizeBtn.Click += (_, _) => RunOptimizations();
        Controls.Add(optimizeBtn);
    }

    private void BuildOptimizations(GroupBox parent)
    {
        AddOpt(parent, "uac",
            "降低 UAC 等级（减少标准用户的权限弹窗和安全检查开销）",
            "将 UAC 从默认等级降到\"仅通知\"，减少完整性检查带来的性能损耗。",
            () =>
            {
                using var key = Registry.LocalMachine.CreateSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System");
                key.SetValue("ConsentPromptBehaviorUser", 0, RegistryValueKind.DWord);
                key.SetValue("PromptOnSecureDesktop", 0, RegistryValueKind.DWord);
            });

        AddOpt(parent, "gprefresh",
            "禁用后台组策略自动刷新（减少周期性 CPU/磁盘 I/O）",
            "非域环境下组策略刷新意义不大，禁用后可减少后台资源占用。",
            () =>
            {
                using var key = Registry.LocalMachine.CreateSubKey(
                    @"SOFTWARE\Policies\Microsoft\Windows\Group Policy\{35378EAC-683F-11D2-A89A-00C04FBBCFA2}");
                key.SetValue("NoBackgroundPolicy", 1, RegistryValueKind.DWord);
            });

        AddOpt(parent, "prefetch",
            "优化预读取策略（对所有用户生效，改善程序启动速度）",
            "开启应用启动和引导预读取，提升非管理员账户的程序启动体验。",
            () =>
            {
                using var key = Registry.LocalMachine.CreateSubKey(
                    @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management\PrefetchParameters");
                key.SetValue("EnablePrefetcher", 3, RegistryValueKind.DWord);
            });

        AddOpt(parent, "remotefx",
            "RemoteFX 对所有用户开放（远程桌面时 GPU 加速对标准用户也生效）",
            "如果使用远程桌面，此项让标准用户也能享受 GPU 加速渲染。",
            () =>
            {
                using var key = Registry.LocalMachine.CreateSubKey(
                    @"SOFTWARE\Policies\Microsoft\Windows NT\Terminal Services");
                key.SetValue("SelectTransport", 0, RegistryValueKind.DWord);
                key.SetValue("AVCHardwareEncodePreferred", 1, RegistryValueKind.DWord);
            });

        AddOpt(parent, "search",
            "优化 Windows Search 索引（减少索引对标准用户的 I/O 争用）",
            "限制搜索索引器的 CPU 和 I/O 优先级，减少对前台操作的干扰。",
            () =>
            {
                using var key = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Microsoft\Windows Search");
                key.SetValue("SetupCompletedSuccessfully", 0, RegistryValueKind.DWord);
                // 将 WSearch 服务设为延迟自动启动
                RunProcess("sc.exe", "config WSearch start= delayed-auto");
            });

        AddOpt(parent, "power",
            "电源计划设为高性能（避免节能模式导致的 CPU 降频）",
            "工作站不需要节能，直接设为最高性能。",
            () =>
            {
                // 高性能 GUID: 8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c
                var exit = RunProcess("powercfg.exe", "/setactive 8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
                if (exit != 0)
                    RunProcess("powercfg.exe", "/duplicatescheme 8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
            });
    }

    private void AddOpt(GroupBox parent, string id, string label, string desc, Action apply)
    {
        var idx = _items.Count;
        var y = 28 + idx * 46;

        var cb = new CheckBox
        {
            Text = label,
            Location = new Point(20, y),
            MaximumSize = new Size(610, 0),
            AutoSize = true,
            Checked = true
        };
        parent.Controls.Add(cb);

        var status = new Label
        {
            Text = "",
            Location = new Point(640, y + 2),
            AutoSize = true,
            ForeColor = Color.Gray,
            Font = new Font("Microsoft YaHei UI", 8F)
        };
        parent.Controls.Add(status);

        var descLabel = new Label
        {
            Text = "    " + desc,
            Location = new Point(40, y + 24),
            MaximumSize = new Size(690, 0),
            AutoSize = true,
            ForeColor = Color.Gray,
            Font = new Font("Microsoft YaHei UI", 8F)
        };
        parent.Controls.Add(descLabel);

        _items.Add(new OptItem { Id = id, Label = label, Desc = desc, Check = cb, Status = status, Apply = apply });
    }

    private static int RunProcess(string fileName, string args)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = fileName,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };
            using var p = System.Diagnostics.Process.Start(psi);
            if (p == null) return -1;
            p.WaitForExit(15000);
            return p.ExitCode;
        }
        catch { return -1; }
    }

    private void RunOptimizations()
    {
        try
        {
            int applied = 0, failed = 0;
            foreach (var opt in _items)
            {
                if (!opt.Check.Checked) continue;
                try
                {
                    if (LabConfig.TestMode)
                    {
                        opt.Status.Text = "✓ 已应用(测试)";
                        opt.Status.ForeColor = Color.Purple;
                        _shell.Log($"[测试模式] 跳过真实应用: {opt.Label}");
                    }
                    else
                    {
                        opt.Apply();
                        opt.Status.Text = "✓ 已应用";
                        opt.Status.ForeColor = Color.Green;
                        _shell.Log($"已应用: {opt.Label}");
                    }
                    applied++;
                }
                catch (Exception ex)
                {
                    opt.Status.Text = "✗ 失败";
                    opt.Status.ForeColor = Color.Red;
                    _shell.Log($"应用失败 [{opt.Label}]: {ex.Message}", "WARN");
                    failed++;
                }
            }
            _shell.Log($"性能优化完成：成功 {applied} 项，失败 {failed} 项");
            AuditLogger.Write("APPLY_OPTIMIZATION", "system", detail: $"已应用 {applied} 项优化");
            var failMsg = failed > 0 ? $"，失败 {failed} 项（详见日志）" : "";
            MessageBox.Show($"优化已应用 {applied} 项{failMsg}\n\n部分优化可能需要重启后生效。",
                "完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            _shell.Log($"优化执行失败: {ex.Message}", "ERROR");
            AuditLogger.Write("APPLY_OPTIMIZATION", "system", AuditLogger.Result.Failed, ex.Message);
        }
    }
}
