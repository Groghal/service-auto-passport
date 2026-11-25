using System.Text.Json;
using System.Text.Json.Serialization;
using ServiceAutoPassport.Core;

namespace ServiceAutoPassport;

internal sealed class LocalSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public NetworkMapSettings Network { get; set; } = new();
    public List<KafkaTopicRule> KafkaTopicRules { get; set; } = [.. KafkaTopicReport.DefaultRules];
    public string? NetworkAppParamsRoot { get; set; }
    public string AppParamsRoot { get; set; } = "";
    public string ServiceFolder { get; set; } = "";
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SelectedService { get; set; }
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
            settings.Network ??= new NetworkMapSettings();
            settings.KafkaTopicRules ??= [.. KafkaTopicReport.DefaultRules];
            settings.Services ??= new Dictionary<string, ServiceIdentity>(StringComparer.Ordinal);
            settings.KeyDescriptions ??= new Dictionary<string, string>(StringComparer.Ordinal);
            settings.HtmlDescriptionKeys ??= new HashSet<string>(StringComparer.Ordinal);
            settings.MigratePassportIdentity();
            return settings;
        }
        catch (JsonException error)
        {
            throw new InvalidDataException($"Не удалось прочитать локальные настройки: {path}", error);
        }
    }

    private void MigratePassportIdentity()
    {
        var previousName = SelectedService?.Trim();
        SelectedService = null;
        if (string.IsNullOrEmpty(previousName) || string.IsNullOrWhiteSpace(ServiceFolder) ||
            !Services.TryGetValue(previousName, out var identity)) return;

        try
        {
            var name = ServiceComponent.DeploymentName(ServiceFolder.Trim());
            if (name.Length > 0) Services.TryAdd(name, identity);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // An invalid saved folder does not identify a profile to migrate.
        }
    }

    public void Save()
    {
        var path = FilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var pending = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(pending, JsonSerializer.Serialize(this, JsonOptions));
            File.Move(pending, path, overwrite: true);
        }
        finally
        {
            File.Delete(pending);
        }
    }
}

internal sealed class ServiceIdentity
{
    public string DomainApiVariable { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Team { get; set; } = "";
    public bool DisplayNamePriority { get; set; } = true;
    public bool TeamPriority { get; set; } = true;
    public bool DisplayNameIsHtml { get; set; }
    public bool TeamIsHtml { get; set; }
}
