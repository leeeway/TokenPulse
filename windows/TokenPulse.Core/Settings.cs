using System.Text.Json;

namespace TokenPulse.Core;

public sealed record Settings
{
    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public string ClaudeDirectory { get; init; } = Path.Combine(Home, ".claude", "projects");
    public string CodexDirectory { get; init; } = Path.Combine(Home, ".codex", "sessions");
    public string AntigravityDirectory { get; init; } = Path.Combine(Home, ".gemini", "antigravity", "brain");
    public string CustomEventsFile { get; init; } = Path.Combine(Home, ".tokenpulse", "custom_events.jsonl");
    public bool StartWithWindows { get; init; }
    public bool EnableOllama { get; init; } = true;

    public void Validate()
    {
        foreach (var path in new[] { ClaudeDirectory, CodexDirectory, AntigravityDirectory, CustomEventsFile })
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
                throw new ArgumentException("请填写完整的绝对路径；目录可以暂时不存在。");
            _ = Path.GetFullPath(path);
        }
    }
}

public sealed class SettingsStore(string? filePath = null)
{
    public string FilePath { get; } = filePath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TokenPulse", "settings.json");
    public (Settings Settings, string? Warning) Load()
    {
        try
        {
            var settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? throw new JsonException("配置为空");
            settings.Validate();
            return (settings, null);
        }
        catch (FileNotFoundException) { return (new(), null); }
        catch (DirectoryNotFoundException) { return (new(), null); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return (new(), "设置读取失败，已临时使用默认设置：" + ex.Message);
        }
    }
    public void Save(Settings settings)
    {
        settings.Validate();
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temporary = FilePath + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, FilePath, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
