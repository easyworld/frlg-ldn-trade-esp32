using System.IO;
using System.Windows;

namespace Frlg.Trade.Desktop;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            var window = new MainWindow();
            MainWindow = window;
            window.Show();
            if (e.Args.Contains("--connect"))
                window.ConnectButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            if (e.Args.Contains("--smoke-test"))
            {
                await SmokeTests.Run(window);
                Shutdown(0);
            }
        }
        catch (Exception error)
        {
            Directory.CreateDirectory(Paths.Local);
            File.WriteAllText(Path.Combine(Paths.Local, "desktop-error.log"), error.ToString());
            if (!e.Args.Contains("--smoke-test")) MessageBox.Show(error.Message, "启动失败");
            Shutdown(1);
        }
    }
}
