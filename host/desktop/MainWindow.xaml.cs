using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PKHeX.Core;
using Frlg.Trade.Core;

namespace Frlg.Trade.Desktop;

public enum ConnectionState { Disconnected, Connecting, Connected, Disconnecting }

public partial class MainWindow : Window
{
    public PartyStore LocalParty { get; } = new();
    public ObservableCollection<PokemonSlot> OpponentSlots { get; } = new(Enumerable.Range(0, 6).Select(i => new PokemonSlot(i, true)));
    public ConnectionState State { get; private set; }
    public bool PendingChanges { get; private set; }
    private readonly BridgeClient bridge = new();
    private bool initialized, closing, failed;
    private string? runPath;
    private TrainerIdentity[] trainers = [];
    private string trainerSignature = "";
    private string SettingsPath => Path.Combine(Paths.Local, "desktop.json");

    public MainWindow()
    {
        InitializeComponent();
        Directory.CreateDirectory(Paths.Local);
        LocalParty.Load();
        DataContext = this;
        RefreshPortList();
        if (File.Exists(SettingsPath))
        {
            using var settings = JsonDocument.Parse(File.ReadAllText(SettingsPath));
            var root = settings.RootElement;
            if (root.TryGetProperty("port", out var port) && PortBox.Items.Contains(port.GetString())) PortBox.SelectedItem = port.GetString();
            AutoOt.IsChecked = root.TryGetProperty("autoOt", out var auto) && auto.GetBoolean();
        }
        bridge.Message += message => Dispatcher.InvokeAsync(() => HandleEvent(message));
        bridge.Exited += code => Dispatcher.InvokeAsync(() => HandleExit(code));
        initialized = true;
        SetState(ConnectionState.Disconnected);
    }

    internal void HandleExit(int code)
    {
        SetState(ConnectionState.Disconnected);
        if (!failed) StatusText.Text = code == 0 ? "连接已关闭" : "连接意外结束，串口已释放";
    }

    internal void SetState(ConnectionState state)
    {
        State = state;
        ConnectButton.IsEnabled = state == ConnectionState.Disconnected;
        DisconnectButton.IsEnabled = state is ConnectionState.Connected or ConnectionState.Connecting;
        DisconnectLabel.Text = state == ConnectionState.Connecting ? "取消连接" : "断开";
        PortBox.IsEnabled = RefreshPorts.IsEnabled = state == ConnectionState.Disconnected;
        if (state == ConnectionState.Disconnected)
        {
            foreach (var slot in OpponentSlots) slot.Set(null);
            OpponentName.Text = "对方";
            trainers = []; trainerSignature = ""; SyncOt.IsEnabled = false;
            if (PendingChanges) PendingText.Text = "待用队伍已保存，将用于下一次连接";
        }
    }

    private void RefreshPortList()
    {
        var previous = PortBox.SelectedItem as string;
        var ports = SerialPort.GetPortNames().Order(StringComparer.OrdinalIgnoreCase).ToArray();
        PortBox.ItemsSource = ports;
        PortBox.SelectedItem = ports.Contains(previous) ? previous : ports.FirstOrDefault(p => p == "COM6") ?? ports.FirstOrDefault();
    }
    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshPortList();
    private void Port_Changed(object sender, SelectionChangedEventArgs e) { if (initialized) SaveSettings(); }
    private void SaveSettings() => File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new { port = PortBox.SelectedItem as string, autoOt = AutoOt.IsChecked == true }));

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (State != ConnectionState.Disconnected) return;
        if (PortBox.SelectedItem is not string port) { ShowError("没有可用串口。"); return; }
        if (LocalParty.Slots.Count(s => s.Occupied) < 2 || !LocalParty.Slots[LocalParty.Selected].Occupied)
        { ShowError("队伍需要至少两只宝可梦，并选中一个有效的提供槽位。"); return; }
        try
        {
            while (true)
            {
                try { KeyFile.LoadDefault(); break; }
                catch (MissingKeysException missing) { if (!KeysPrompt.Show(this, missing)) return; }
            }
            LocalParty.Save(); SaveSettings();
            PendingChanges = false; PendingText.Text = ""; failed = false;
            SetState(ConnectionState.Connecting);
            StatusText.Text = "正在打开串口";
            await bridge.StartAsync(port, LocalParty.Snapshot(), LocalParty.Selected);
        }
        catch (Exception error) { SetState(ConnectionState.Disconnected); ShowError(error.Message); }
    }
    private async void Disconnect_Click(object sender, RoutedEventArgs e)
    {
        SetState(ConnectionState.Disconnecting);
        StatusText.Text = "正在断开连接";
        await bridge.StopAsync();
        SetState(ConnectionState.Disconnected);
        if (!failed) StatusText.Text = "连接已关闭";
    }

    internal void HandleEvent(JsonElement message)
    {
        if (!message.TryGetProperty("event", out var kind)) return;
        switch (kind.GetString())
        {
            case "device":
                DeviceLabel.ToolTip = "协议 v1 · " + message.GetProperty("model").GetString(); break;
            case "run":
                runPath = message.GetProperty("path").GetString(); break;
            case "phase":
                StatusText.Text = message.GetProperty("message").GetString(); break;
            case "connected":
                if (State == ConnectionState.Disconnecting) break;
                SetState(ConnectionState.Connected);
                StatusText.Text = "已连接，等待对方队伍";
                break;
            case "opponent_party":
                if (State is ConnectionState.Disconnected or ConnectionState.Disconnecting) break;
                var values = message.GetProperty("party").EnumerateArray().ToArray();
                if (values.Length != 6) { ShowError("收到的队伍槽位数量异常。"); break; }
                try
                {
                    var parsed = values.Select(v => v.ValueKind == JsonValueKind.Null ? null : PokemonData.Parse(Convert.FromHexString(v.GetString()!))).ToArray();
                    for (int i = 0; i < 6; i++) OpponentSlots[i].Set(parsed[i]);
                    OpponentName.Text = message.GetProperty("name").GetString() ?? "Switch";
                    StatusText.Text = "已读取对方队伍";
                    trainers = GetTrainers(); SyncOt.IsEnabled = trainers.Length > 0;
                    var signature = JsonSerializer.Serialize(trainers);
                    if (signature != trainerSignature)
                    {
                        trainerSignature = signature;
                        if (AutoOt.IsChecked == true) SyncTrainer();
                    }
                }
                catch (Exception error) { ShowError(error.Message); }
                break;
            case "received":
                try
                {
                    var received = PokemonData.Parse(Convert.FromHexString(message.GetProperty("pk3").GetString()!));
                    int index = message.GetProperty("slot").GetInt32();
                    if (!PendingChanges && index is >= 0 and < 6) { LocalParty.Slots[index].Set(received); LocalParty.Save(); }
                    StatusText.Text = $"交易完成，已收到 {received.Nickname}";
                }
                catch (Exception error) { ShowError(error.Message); }
                break;
            case "error":
                failed = true; ShowError(message.GetProperty("message").GetString() ?? "连接失败"); break;
            case "log":
                if (runPath != null) File.AppendAllText(Path.Combine(runPath, "desktop.log"), message.GetProperty("message").GetString() + "\n");
                break;
            // The worker completion callback fires after serial teardown.
            case "disconnected": break;
        }
    }

    internal TrainerIdentity[] GetTrainers() => OpponentSlots.Where(s => s.Pokemon != null)
        .Select(s => TrainerIdentity.From(s.Pokemon!)).Distinct().OrderBy(t => t.Name).ThenBy(t => t.Tid).ThenBy(t => t.Sid).ThenBy(t => t.Gender).ToArray();

    private void AutoOt_Changed(object sender, RoutedEventArgs e)
    {
        if (!initialized) return;
        SaveSettings();
        if (AutoOt.IsChecked == true && trainers.Length > 0) SyncTrainer();
    }
    private void SyncOt_Click(object sender, RoutedEventArgs e) => SyncTrainer();
    private void SyncTrainer()
    {
        if (trainers.Length == 0) return;
        var trainer = trainers.Length == 1 ? trainers[0] : TrainerPicker.Pick(this, trainers);
        if (trainer == null) return;
        try
        {
            LocalParty.ApplyTrainer(trainer);
            OtStatus.Text = trainer.Label;
            MarkChanged();
        }
        catch (Exception error) { ShowError(error.Message); }
    }
    internal void MarkChanged()
    {
        PendingChanges = State != ConnectionState.Disconnected;
        PendingText.Text = PendingChanges ? "右侧为待用队伍 · 修改将在下次连接生效" : "队伍已保存";
        LocalParty.Save();
    }
    private void ShowError(string message) => StatusText.Text = message;

    private void Slot_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not PokemonSlot slot || slot.IsOpponent) return;
        if (!slot.Occupied) { ImportFiles(slot, null); return; }
        LocalParty.Select(slot.Index); MarkChanged();
    }
    private void Slot_DragOver(object sender, DragEventArgs e)
    {
        var slot = (sender as FrameworkElement)?.Tag as PokemonSlot;
        var paths = e.Data.GetData(DataFormats.FileDrop) as string[];
        e.Effects = slot is { IsOpponent: false } && paths?.All(IsPk3) == true ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }
    private static bool IsPk3(string path) => Path.GetExtension(path).Equals(".pk3", StringComparison.OrdinalIgnoreCase);
    private void Slot_Drop(object sender, DragEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is PokemonSlot { IsOpponent: false } slot && e.Data.GetData(DataFormats.FileDrop) is string[] paths)
            ImportFiles(slot, paths);
        e.Handled = true;
    }
    internal void ImportFiles(PokemonSlot slot, string[]? paths)
    {
        if (slot.IsOpponent) return;
        if (paths == null)
        {
            var picker = new Microsoft.Win32.OpenFileDialog { Filter = "第三世代宝可梦 (*.pk3)|*.pk3", Multiselect = true };
            if (picker.ShowDialog(this) != true) return;
            paths = picker.FileNames;
        }
        try
        {
            if (paths.Length == 0 || slot.Index + paths.Length > 6 || !paths.All(IsPk3)) throw new InvalidDataException("文件数量超出剩余槽位，或文件不是 PK3。");
            var loaded = paths.Select(p => PokemonData.Parse(File.ReadAllBytes(p))).ToArray();
            for (int i = 0; i < loaded.Length; i++) LocalParty.Slots[slot.Index + i].Set(loaded[i]);
            if (!LocalParty.Slots[LocalParty.Selected].Occupied) LocalParty.Select(slot.Index);
            MarkChanged(); StatusText.Text = $"已载入 {loaded.Length} 只宝可梦";
        }
        catch (Exception error) { ShowError(error.Message); }
    }
    private static PokemonSlot? MenuSlot(object sender) => (sender as MenuItem)?.Parent is ContextMenu { PlacementTarget: FrameworkElement element } ? element.Tag as PokemonSlot : null;
    private void SlotMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu { PlacementTarget: FrameworkElement { Tag: PokemonSlot slot } } menu) return;
        ((MenuItem)menu.Items[0]).IsEnabled = !slot.IsOpponent;
        ((MenuItem)menu.Items[1]).IsEnabled = ((MenuItem)menu.Items[2]).IsEnabled = slot.Occupied;
        ((MenuItem)menu.Items[4]).IsEnabled = !slot.IsOpponent && slot.Occupied;
    }
    private void Import_Click(object sender, RoutedEventArgs e) { if (MenuSlot(sender) is { } slot) ImportFiles(slot, null); }
    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (MenuSlot(sender)?.Pokemon is not { } pk) return;
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "PK3 (*.pk3)|*.pk3", FileName = $"{pk.Species:D3}.pk3" };
        if (dialog.ShowDialog(this) == true)
        {
            try { File.WriteAllBytes(dialog.FileName, PokemonData.Export(pk)); }
            catch (IOException error) { ShowError(error.Message); }
        }
    }
    private void Details_Click(object sender, RoutedEventArgs e)
    { if (MenuSlot(sender) is { Occupied: true } slot) MessageBox.Show(this, slot.Details, slot.Nickname); }
    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (MenuSlot(sender) is not { IsOpponent: false } slot) return;
        slot.Set(null);
        if (slot.Index == LocalParty.Selected) LocalParty.Select(LocalParty.Slots.FirstOrDefault(s => s.Occupied)?.Index ?? 0);
        MarkChanged();
    }
    private void OpenLogs_Click(object sender, RoutedEventArgs e)
        => Process.Start(new ProcessStartInfo(runPath ?? Paths.Local) { UseShellExecute = true });
    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (SmokeTests.Active) return;
        if (closing) return;
        if (bridge.Running)
        {
            e.Cancel = true;
            SetState(ConnectionState.Disconnecting);
            await bridge.StopAsync(); closing = true; Close();
        }
        else { LocalParty.Save(); SaveSettings(); }
    }
}

public static class TrainerPicker
{
    public static TrainerIdentity? Pick(Window owner, TrainerIdentity[] identities)
    {
        var choices = new ListBox { ItemsSource = identities, DisplayMemberPath = nameof(TrainerIdentity.Label), SelectedIndex = 0, Margin = new Thickness(0, 12, 0, 18), MinHeight = 100 };
        var panel = new StackPanel { Margin = new Thickness(22) };
        panel.Children.Add(new TextBlock { Text = "选择对方初训家", FontSize = 19, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(choices);
        var confirm = new Button { Content = "使用此初训家", HorizontalAlignment = HorizontalAlignment.Right, IsDefault = true };
        panel.Children.Add(confirm);
        var window = new Window { Owner = owner, Title = "初训家", Width = 600, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = panel, Background = System.Windows.Media.Brushes.White };
        confirm.Click += (_, _) => window.DialogResult = true;
        return window.ShowDialog() == true ? choices.SelectedItem as TrainerIdentity : null;
    }
}
