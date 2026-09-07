using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Frlg.Trade.Core;

namespace Frlg.Trade.Desktop;

internal static class KeysPrompt
{
    public static bool Show(Window owner, MissingKeysException error)
    {
        var panel = new StackPanel { Margin = new Thickness(22) };
        panel.Children.Add(new TextBlock { Text = "未找到 prod.keys", FontSize = 19, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = "请将文件放到程序所在目录后重试。", Margin = new Thickness(0, 12, 0, 8) });
        panel.Children.Add(new TextBlock { Text = error.DirectoryPath, TextWrapping = TextWrapping.Wrap });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        var open = new Button { Content = "打开目录", Margin = new Thickness(0, 0, 10, 0) };
        var retry = new Button { Content = "重试", IsDefault = true };
        buttons.Children.Add(open); buttons.Children.Add(retry); panel.Children.Add(buttons);
        var window = new Window { Owner = owner, Title = "密钥文件", Width = 540, SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = panel };
        open.Click += (_, _) => Process.Start(new ProcessStartInfo(error.DirectoryPath) { UseShellExecute = true });
        retry.Click += (_, _) => window.DialogResult = true;
        return window.ShowDialog() == true;
    }
}
