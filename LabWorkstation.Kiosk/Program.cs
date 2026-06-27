using LabWorkstation.Kiosk;

namespace LabWorkstation.Kiosk;

static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new KioskForm());
    }
}
