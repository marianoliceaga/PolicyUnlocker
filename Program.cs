using System.Security.Principal;
using System.Windows.Forms;

namespace PolicyUnlocker;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // The manifest already requests elevation; this is a safety net in case the exe is run unelevated.
        if (!IsAdministrator())
        {
            MessageBox.Show(
                "PolicyUnlocker must run as administrator. Right-click it and choose 'Run as administrator'.",
                "PolicyUnlocker", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        Application.Run(new MainForm());
    }

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
