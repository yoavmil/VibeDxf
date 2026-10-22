using System.IO;
using System.Text.Json;
using System.Windows;

namespace PackageViewer;

public partial class App : Application
{
    private async void OnStartup(object sender, StartupEventArgs args)
    {
        bool smoke = args.Args.Contains("--smoke");
        string? report = null;
        int reportIndex = Array.IndexOf(args.Args, "--report");
        if (reportIndex >= 0) report = args.Args[reportIndex + 1];
        try
        {
            var window = new MainWindow(smoke);
            MainWindow = window;
            window.Closed += (_, _) => { if (!smoke) Shutdown(); };
            window.Show();
            string? path = args.Args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));
            if (path != null) await window.LoadDrawingAsync(path);
            if (smoke)
            {
                var results = await window.VerifyPackageAsync();
                if (report != null) File.WriteAllText(report, JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
                window.Close(); Shutdown();
            }
        }
        catch (Exception ex)
        {
            if (report != null) File.WriteAllText(report, JsonSerializer.Serialize(new { Passed = false, Error = ex.ToString() }));
            if (!smoke) MessageBox.Show(ex.Message, "Package viewer", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }
}
