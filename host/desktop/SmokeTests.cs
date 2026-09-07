using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Frlg.Trade.Desktop;

public static class SmokeTests
{
    public static bool Active { get; private set; }
    private static int checks;
    private static void Require(bool condition, string message)
    { checks++; if (!condition) throw new InvalidOperationException(message); }

    public static async Task Run(MainWindow window)
    {
        Active = true;
        var stateFile = Path.Combine(Paths.Local, "party.json");
        var settingsFile = Path.Combine(Paths.Local, "desktop.json");
        byte[]? previous = File.Exists(stateFile) ? File.ReadAllBytes(stateFile) : null;
        byte[]? previousSettings = File.Exists(settingsFile) ? File.ReadAllBytes(settingsFile) : null;
        string output = Path.Combine(Paths.Local, "ui-checks");
        Directory.CreateDirectory(output);
        try
        {
            for (int i = 0; i < 6; i++) window.LocalParty.Slots[i].Set(null);
            window.LocalParty.Slots[0].Set(PokemonData.Parse(DefaultAssets.Party("mewtwo")));
            window.LocalParty.Slots[1].Set(PokemonData.Parse(DefaultAssets.Party("deoxys")));
            for (int spriteId = 1; spriteId <= 386; spriteId++)
            {
                using var sprite = DefaultAssets.Open($"sprites.{spriteId}.png");
                Require(BitmapFrame.Create(sprite, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).PixelWidth > 0,
                    $"Embedded sprite {spriteId} decodes");
            }
            window.LocalParty.Select(1);
            Require(window.OpponentSlots.All(s => !s.Occupied), "Disconnected opponents must be blank");
            Require(window.ConnectButton.IsEnabled && !window.DisconnectButton.IsEnabled, "Initial button state");
            Require(window.LocalParty.Slots[0].Pokemon?.Species == 150, "Default Mewtwo");
            Require(window.LocalParty.Slots[1].Pokemon?.Species == 386, "Internal Deoxys ID must become national ID 386");
            Require(window.LocalParty.Slots.Take(2).All(s => s.Sprite != null), "Default sprites");
            await Capture(window, output, "disconnected");

            var pk = window.LocalParty.Slots[0].Pokemon!;
            var trainer = new TrainerIdentity(12345, 54321, "ALICE", 1);
            var edited = PokemonData.WithTrainer(pk, trainer);
            var readback = PokemonData.Parse(PokemonData.Export(edited));
            Require(TrainerIdentity.From(readback) == trainer && readback.ChecksumValid, "Trainer edit roundtrip");
            Require(readback.PID == pk.PID && readback.Species == pk.Species && readback.IV32 == pk.IV32, "OT update preserves unrelated fields");
            var damaged = PokemonData.Export(pk); damaged[40] ^= 0x77;
            try { PokemonData.Parse(damaged); throw new Exception("Damaged PK3 accepted"); }
            catch (InvalidDataException) { checks++; }
            window.SetState(ConnectionState.Connecting);
            Require(!window.ConnectButton.IsEnabled && window.DisconnectButton.IsEnabled, "Connecting is cancellable");
            using (var ready = JsonDocument.Parse("{\"event\":\"phase\",\"message\":\"正在认证\"}"))
                window.HandleEvent(ready.RootElement);
            Require(window.DisconnectButton.IsEnabled, "Native authentication remains cancellable");
            window.SetState(ConnectionState.Connected);
            var party = new string[6];
            ushort[] species = [386, 5, 15, 12, 41, 386];
            string[] nicknames = ["DEOXYS", "CHARMELEON", "BEEDRILL", "BUTTERFREE", "ZUBAT", "DEOXYS"];
            for (int i = 0; i < 6; i++)
            {
                var sample = pk.Clone(); sample.Species = species[i]; sample.Nickname = nicknames[i]; sample.CurrentLevel = (byte)(i == 0 ? 100 : 8 + i);
                sample = PokemonData.WithTrainer(sample, i < 4 ? trainer : trainer with { Sid = 11111 });
                party[i] = Convert.ToHexString(PokemonData.Export(sample));
            }
            var auto = window.AutoOt.IsChecked; window.AutoOt.IsChecked = false;
            using (var doc = JsonDocument.Parse(JsonSerializer.Serialize(new { @event = "opponent_party", name = "ezwd", party })))
                window.HandleEvent(doc.RootElement);
            Require(window.OpponentSlots.All(s => s.Occupied && s.Sprite != null), "Live event fills all six sprites");
            Require(window.GetTrainers().Length == 2, "OT identity dedup includes SID");
            var snapshot = window.LocalParty.Snapshot();
            window.LocalParty.ApplyTrainer(trainer); window.MarkChanged();
            Require(PokemonData.Parse(Convert.FromHexString(snapshot[0]!)).OriginalTrainerName == pk.OriginalTrainerName, "Connected snapshot remains unchanged");
            Require(window.PendingChanges && window.PendingText.Text.Contains("下次连接"), "Connected edits staged");
            Require(window.LocalParty.Slots.Where(s => s.Occupied).All(s => TrainerIdentity.From(s.Pokemon!) == trainer), "OT updates every local occupied slot");
            await Capture(window, output, "connected");
            window.Width = 900; window.Height = 700;
            await Capture(window, output, "compact");
            var dropped = Path.Combine(output, "drop.pk3"); File.WriteAllBytes(dropped, PokemonData.Export(edited));
            window.ImportFiles(window.LocalParty.Slots[5], [dropped]);
            Require(window.LocalParty.Slots[5].Occupied, "Import reaches sixth slot");
            window.SetState(ConnectionState.Disconnected);
            Require(window.OpponentSlots.All(s => !s.Occupied) && window.ConnectButton.IsEnabled && !window.DisconnectButton.IsEnabled, "Unexpected exit resets party and buttons");
            var client = new BridgeClient();
            var exited = new TaskCompletionSource<int>();
            client.Exited += code => { window.HandleExit(code); exited.TrySetResult(code); };
            window.SetState(ConnectionState.Connecting);
            try
            {
                // Invalid party exercises the native worker failure path before serial access.
                await client.StartAsync("COM6", new string?[6], 0);
                Require(await exited.Task.WaitAsync(TimeSpan.FromSeconds(10)) != 0, "Backend failure is observed");
                Require(window.State == ConnectionState.Disconnected && window.ConnectButton.IsEnabled && !window.DisconnectButton.IsEnabled,
                    "Real backend exit restores controls");
            }
            finally { await client.StopAsync(); }
            window.AutoOt.IsChecked = auto;
            File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new { checks, passed = true, pkhex = "26.8.26" }));
        }
        finally
        {
            if (previous is null) { if (File.Exists(stateFile)) File.Delete(stateFile); }
            else File.WriteAllBytes(stateFile, previous);
            if (previousSettings is null) { if (File.Exists(settingsFile)) File.Delete(settingsFile); }
            else File.WriteAllBytes(settingsFile, previousSettings);
        }
    }
    private static async Task Capture(MainWindow window, string directory, string name)
    {
        await window.Dispatcher.InvokeAsync(() => { window.UpdateLayout(); }, DispatcherPriority.ApplicationIdle);
        await Task.Delay(120);
        var root = (FrameworkElement)window.Content;
        foreach (var grid in new[] { window.OpponentGrid, window.LocalGrid })
        {
            var bounds = new List<Rect>();
            for (int i = 0; i < 6; i++)
            {
                var element = (FrameworkElement)grid.ItemContainerGenerator.ContainerFromIndex(i);
                Require(element.ActualWidth > 100 && element.ActualHeight > 100, "Stable tile dimensions");
                var rect = element.TransformToAncestor(root).TransformBounds(new Rect(element.RenderSize));
                Require(bounds.All(other => !Rect.Intersect(other, rect).HasArea()), "Tiles do not overlap");
                bounds.Add(rect);
            }
        }
        var bitmap = new RenderTargetBitmap((int)(root.ActualWidth + root.Margin.Left + root.Margin.Right),
            (int)(root.ActualHeight + root.Margin.Top + root.Margin.Bottom), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        byte[] pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        Require(pixels.Distinct().Count() > 100, "Rendered image contains content");
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(directory, name + ".png")); encoder.Save(file);
    }
    private static bool HasArea(this Rect rect) => !rect.IsEmpty && rect.Width > 0.1 && rect.Height > 0.1;
}
