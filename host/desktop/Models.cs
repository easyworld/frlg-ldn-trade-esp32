using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PKHeX.Core;

namespace Frlg.Trade.Desktop;

public static class Paths
{
    public static readonly string Root = AppContext.BaseDirectory;
    public static string Local => Path.Combine(Root, "local");
}

public static class DefaultAssets
{
    public static Stream Open(string name) => typeof(DefaultAssets).Assembly.GetManifestResourceStream("Assets." + name)
        ?? throw new FileNotFoundException($"缺少内置资源：{name}");

    public static byte[] Party(string name)
    {
        using var stream = Open($"party.{name}.pk3");
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        return bytes.ToArray();
    }
}

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}

public sealed record TrainerIdentity(ushort Tid, ushort Sid, string Name, byte Gender)
{
    public static TrainerIdentity From(PK3 pk) => new(pk.TID16, pk.SID16, pk.OriginalTrainerName, pk.OriginalTrainerGender);
    public string Label => $"{Name}  ·  {(Gender == 0 ? "男" : "女")}  ·  TID {Tid:D5}  /  SID {Sid:D5}";
}

public static class PokemonData
{
    public static PK3 Parse(byte[] bytes)
    {
        if (bytes.Length is not (80 or 100)) throw new InvalidDataException("PK3 必须为 80 或 100 字节。");
        var pk = new PK3(bytes.ToArray());
        if (!pk.ChecksumValid || pk.Species is 0 or > 386 || pk.FlagIsBadEgg)
            throw new InvalidDataException("PK3 校验失败，或不是有效的第三世代宝可梦。");
        if (bytes.Length == 80 || pk.Stat_Level == 0) pk.ResetPartyStats();
        return pk;
    }
    public static byte[] Export(PK3 pk)
    {
        var copy = pk.Clone();
        copy.RefreshChecksum();
        return copy.Data.ToArray();
    }
    public static PK3 WithTrainer(PK3 pk, TrainerIdentity trainer)
    {
        var copy = pk.Clone();
        copy.TID16 = trainer.Tid;
        copy.SID16 = trainer.Sid;
        copy.OriginalTrainerTrash.Fill(0xFF);
        copy.OriginalTrainerName = trainer.Name;
        copy.OriginalTrainerGender = trainer.Gender;
        if (copy.OriginalTrainerName != trainer.Name)
            throw new InvalidDataException($"{pk.Nickname} 的语言无法完整保存初训家名称“{trainer.Name}”。");
        copy.RefreshChecksum();
        return copy;
    }
}

public sealed class PokemonSlot(int index, bool opponent) : Observable
{
    private PK3? pokemon;
    private bool selected;
    public int Index { get; } = index;
    public bool IsOpponent { get; } = opponent;
    public PK3? Pokemon => pokemon;
    public bool Occupied => pokemon != null;
    public string Nickname => pokemon?.Nickname ?? "";
    public string Level => pokemon is null ? "" : $"Lv. {pokemon.CurrentLevel}";
    public string Gender => pokemon?.Gender switch { 0 => "♂", 1 => "♀", 2 => "-", _ => "" };
    public Brush GenderBrush => pokemon?.Gender == 1 ? Brushes.LightPink : Brushes.LightCyan;
    public string Selection => selected && Occupied ? "提供槽位" : "";
    public Brush Outline => selected && Occupied ? new SolidColorBrush(Color.FromRgb(255, 207, 70)) : new SolidColorBrush(Color.FromArgb(90, 118, 181, 230));
    public string Details => pokemon is null ? "" : $"{pokemon.Nickname} · #{pokemon.Species:D3}\n{TrainerIdentity.From(pokemon).Label}\n个体值 {pokemon.IV_HP}/{pokemon.IV_ATK}/{pokemon.IV_DEF}/{pokemon.IV_SPA}/{pokemon.IV_SPD}/{pokemon.IV_SPE}\nPK3 校验正常";
    public ImageSource? Sprite { get; private set; }
    public void Set(PK3? value)
    {
        pokemon = value;
        Sprite = null;
        if (value != null)
        {
            using var stream = DefaultAssets.Open($"sprites.{value.Species}.png");
            var bitmap = new BitmapImage();
            bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze(); Sprite = CropSprite(bitmap);
        }
        foreach (var name in new[] { nameof(Pokemon), nameof(Occupied), nameof(Nickname), nameof(Level), nameof(Gender), nameof(GenderBrush), nameof(Selection), nameof(Outline), nameof(Details), nameof(Sprite) }) Notify(name);
    }
    public void Select(bool value) { selected = value; Notify(nameof(Outline)); Notify(nameof(Selection)); }
    private static BitmapSource CropSprite(BitmapSource source)
    {
        var rgba = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        int width = rgba.PixelWidth, height = rgba.PixelHeight;
        var bytes = new byte[width * height * 4]; rgba.CopyPixels(bytes, width * 4, 0);
        int left = width, right = 0, top = height, bottom = 0;
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
            if (bytes[(y * width + x) * 4 + 3] != 0)
            { left = Math.Min(left, x); right = Math.Max(right, x); top = Math.Min(top, y); bottom = Math.Max(bottom, y); }
        if (left > right || top > bottom) return source;
        var crop = new CroppedBitmap(source, new System.Windows.Int32Rect(left, top, right - left + 1, bottom - top + 1));
        crop.Freeze(); return crop;
    }
}

public sealed class PartyStore
{
    public ObservableCollection<PokemonSlot> Slots { get; } = new(Enumerable.Range(0, 6).Select(i => new PokemonSlot(i, false)));
    public int Selected { get; private set; } = 1;
    private string FilePath => Path.Combine(Paths.Local, "party.json");
    public void Load()
    {
        if (File.Exists(FilePath))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(FilePath));
            var entries = doc.RootElement.GetProperty("slots").EnumerateArray().ToArray();
            if (entries.Length != 6) throw new InvalidDataException("队伍记录必须有六个槽位。");
            for (int i = 0; i < 6; i++)
                Slots[i].Set(entries[i].ValueKind == JsonValueKind.Null ? null : PokemonData.Parse(Convert.FromHexString(entries[i].GetString()!)));
            Selected = doc.RootElement.GetProperty("selected").GetInt32();
        }
        else
        {
            Slots[0].Set(PokemonData.Parse(DefaultAssets.Party("mewtwo")));
            Slots[1].Set(PokemonData.Parse(DefaultAssets.Party("deoxys")));
        }
        if (Selected < 0 || Selected > 5 || !Slots[Selected].Occupied)
            Selected = Slots.FirstOrDefault(s => s.Occupied)?.Index ?? 0;
        Select(Selected);
    }
    public void Select(int index)
    {
        Selected = index;
        foreach (var slot in Slots) slot.Select(slot.Index == index);
    }
    public string?[] Snapshot() => Slots.Select(s => s.Pokemon is null ? null : Convert.ToHexString(PokemonData.Export(s.Pokemon))).ToArray();
    public void Save()
    {
        Directory.CreateDirectory(Paths.Local);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(new { selected = Selected, slots = Snapshot() }));
        File.Move(temp, FilePath, true);
    }
    public void ApplyTrainer(TrainerIdentity trainer)
    {
        var updated = Slots.Select(s => s.Pokemon is null ? null : PokemonData.WithTrainer(s.Pokemon, trainer)).ToArray();
        for (int i = 0; i < 6; i++) Slots[i].Set(updated[i]);
        Save();
    }
}
