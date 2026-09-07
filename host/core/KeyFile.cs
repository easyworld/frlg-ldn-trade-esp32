namespace Frlg.Trade.Core;

public sealed class MissingKeysException(string directory) : FileNotFoundException(
    $"未找到 prod.keys，请将文件放到程序所在目录后重试。\n{directory}")
{ public string DirectoryPath { get; } = directory; }

public sealed class KeyFile
{
    private readonly Dictionary<string, byte[]> values = new(StringComparer.OrdinalIgnoreCase);
    public string SourcePath { get; }
    public KeyFile(string path)
    {
        SourcePath = path;
        int number = 0;
        foreach (var raw in File.ReadLines(path))
        {
            number++;
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';')) continue;
            var parts = line.Split('=', 2, StringSplitOptions.TrimEntries);
            if (parts.Length != 2) throw new InvalidDataException($"prod.keys 第 {number} 行格式错误：{path}");
            try
            {
                if (!values.TryAdd(parts[0], Convert.FromHexString(parts[1]))) throw new FormatException();
            }
            catch (FormatException) { throw new InvalidDataException($"prod.keys 第 {number} 行格式错误或键名重复：{path}"); }
        }
        Get("aes_kek_generation_source"); Get("aes_key_generation_source");
        if (!values.ContainsKey("master_key_00") && !values.ContainsKey("master_key_12"))
            throw new InvalidDataException($"prod.keys 缺少支持的 master_key：{path}");
    }
    public byte[] Get(string name)
    {
        if (!values.TryGetValue(name, out var key) || key.Length != 16)
            throw new InvalidDataException($"prod.keys 缺少有效的 {name}：{SourcePath}");
        return key;
    }
    public bool Supports(int protocol) => values.ContainsKey(protocol == 1 ? "master_key_00" : "master_key_12");
    public static string Find(string executableDirectory, string userDirectory)
    {
        string local = Path.Combine(executableDirectory, "prod.keys");
        if (File.Exists(local)) return local;
        string home = Path.Combine(userDirectory, ".switch", "prod.keys");
        if (File.Exists(home)) return home;
        throw new MissingKeysException(executableDirectory);
    }
    public static KeyFile LoadDefault() => new(Find(AppContext.BaseDirectory, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
}
