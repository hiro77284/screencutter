namespace ScreenCutter;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(true, @"Local\ScreenCutter.SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show("ScreenCutter はすでに起動しています。タスクトレイを確認してください。",
                "ScreenCutter", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new TrayApp());
    }
}
