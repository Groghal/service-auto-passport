using System.Text.Json;

namespace ServiceAutoPassport;

internal sealed class LocalSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public string AppParamsRoot { get; set; } = "";
    public string ServiceFolder { get; set; } = "";
    public string SelectedService { get; set; } = "";
    public string SelectedTemplate { get; set; } = "";
    public string DefaultTemplateHtml { get; set; } = "";
    public Dictionary<string, ServiceIdentity> Services { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> KeyDescriptions { get; set; } = new(StringComparer.Ordinal);
    public HashSet<string> HtmlDescriptionKeys { get; set; } = new(StringComparer.Ordinal);

    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ServiceAutoPassport", "settings.json");

    public static LocalSettings Load()
    {
        var candidates = new[]
        {
            FilePath,
            Path.Combine(Environment.CurrentDirectory, "settings.json"),
            Path.Combine(AppContext.BaseDirectory, "settings.json")
        };
        var path = candidates.FirstOrDefault(File.Exists);
        if (path is null) return new LocalSettings();

        try
        {
            var settings = JsonSerializer.Deserialize<LocalSettings>(File.ReadAllText(path), JsonOptions)
                ?? new LocalSettings();
            settings.Services ??= new Dictionary<string, ServiceIdentity>(StringComparer.Ordinal);
            settings.KeyDescriptions ??= new Dictionary<string, string>(StringComparer.Ordinal);
            settings.HtmlDescriptionKeys ??= new HashSet<string>(StringComparer.Ordinal);
            return settings;
        }
        catch (JsonException error)
        {
            throw new InvalidDataException($"Не удалось прочитать локальные настройки: {path}", error);
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
    }
}

internal sealed class ServiceIdentity
{
    public string DisplayName { get; set; } = "";
    public string Team { get; set; } = "";
    public bool DisplayNamePriority { get; set; } = true;
    public bool TeamPriority { get; set; } = true;
    public bool DisplayNameIsHtml { get; set; }
    public bool TeamIsHtml { get; set; }
}
