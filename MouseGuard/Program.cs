using System.Windows;

namespace MouseGuard;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => WriteCrash("AppDomain", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            WriteCrash("Task", e.Exception);
            e.SetObserved();
        };

        try
        {
            var app = new App();
            app.InitializeComponent();
            app.Run();
        }
        catch (Exception ex)
        {
            WriteCrash("Main", ex);
            try
            {
                MessageBox.Show(ex.ToString(), "鼠标卫士启动失败", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch
            {
            }
        }
    }

    private static void WriteCrash(string source, Exception? ex)
    {
        if (ex is null)
            return;

        var text = $"{DateTime.Now:O} [{source}]{Environment.NewLine}{ex}{Environment.NewLine}";
        try
        {
            var overrideDir = Environment.GetEnvironmentVariable("MOUSEGUARD_DATA_DIR");
            var directory = !string.IsNullOrWhiteSpace(overrideDir)
                ? overrideDir
                : Path.Combine(Path.GetTempPath(), "MouseGuard");
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "crash.log"), text);
        }
        catch
        {
        }
    }
}
