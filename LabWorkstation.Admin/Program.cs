using LabWorkstation.Common.Configuration;

namespace LabWorkstation.Admin;

static class Program
{
    /// <summary>
    ///  The main entry point for the application.
    ///  传入 --test 参数进入测试模式：所有操作仅作用于内存，不修改真实系统。
    /// </summary>
    [STAThread]
    static void Main(string[] args)
    {
        if (args.Contains("--test", StringComparer.OrdinalIgnoreCase))
        {
            LabConfig.TestMode = true;
            Console.WriteLine("[测试模式] 已启用，所有操作仅作用于内存模拟状态。");
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}