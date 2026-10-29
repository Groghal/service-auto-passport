using YamlDotNet.RepresentationModel;

namespace ServiceAutoPassport.Core;

public sealed record ResourceValue(string? Value, bool IsDefault = false);

public sealed class YamlConfiguration
{
    private YamlConfiguration(Dictionary<string, string?> values, HashSet<string> globalKeys,
        Dictionary<string, Dictionary<string, string?>> resourceDefaults)
    {
        Values = values;
        GlobalKeys = globalKeys;
        _resourceDefaults = resourceDefaults;
    }

    private readonly Dictionary<string, Dictionary<string, string?>> _resourceDefaults;

    public IReadOnlyDictionary<string, string?> Values { get; }
    public IReadOnlySet<string> GlobalKeys { get; }

    public string Required(string key)
    {
        if (!Values.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidDataException($"В YAML не найден обязательный параметр '{key}'.");
        }

        return value;
    }

    public string RequiredAny(params string[] keys)
    {
        var value = OptionalAny(keys);
        if (value is not null) return value;

        throw new InvalidDataException($"В YAML не найден обязательный параметр: {string.Join(" или ", keys)}.");
    }

    public string? OptionalAny(params string[] keys)
    {
        foreach (var key in keys)
        {
            if (Values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
                return value;
        }

        return null;
    }

    public ResourceValue ResolveResource(string key, ComponentType type, string kind, string field)
    {
        if (OptionalAny(key) is { } explicitValue) return new ResourceValue(explicitValue);
        var group = type switch
        {
            ComponentType.UI => "UI",
            ComponentType.WebApi => "API",
            ComponentType.Daemon => "DAEMON",
            _ => null
        };
        if (group is not null && _resourceDefaults.TryGetValue($"DEFAULT_RESOURCES_{group}_{kind}", out var defaults) &&
            defaults.TryGetValue(field, out var value) && !string.IsNullOrWhiteSpace(value))
            return new ResourceValue(value, IsDefault: true);
        return new ResourceValue(null);
    }

    public static YamlConfiguration Load(string root, string service)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(service);
        if (service is "." or ".." || service.IndexOfAny(['/', '\\']) >= 0)
            throw new InvalidDataException("Имя сервиса должно быть названием одной папки.");

        root = Path.GetFullPath(root);
        var serviceDirectory = Path.Combine(root, service);
        var hasServiceDirectory = Directory.Exists(serviceDirectory);
        var globalApplication = FindFirst(root, "application");
        if (!hasServiceDirectory && globalApplication is null)
            throw new InvalidDataException($"Каталог сервиса '{service}' отсутствует, а в {root} нет application.yml или application.yaml.");

        var layers = new (string? Path, bool Global)[]
        {
            (globalApplication, true),
            (hasServiceDirectory ? FindFirst(serviceDirectory, service) ?? FindFirst(serviceDirectory, "application") : null, false),
            (hasServiceDirectory ? FindFirst(root, "application-prod") : null, true),
            (hasServiceDirectory ? FindFirst(serviceDirectory, service + "-prod") ??
             FindFirst(serviceDirectory, "application-prod") : null, false)
        };

        var merged = new Dictionary<string, string?>(StringComparer.Ordinal);
        var globalKeys = new HashSet<string>(StringComparer.Ordinal);
        var resourceDefaults = new Dictionary<string, Dictionary<string, string?>>(StringComparer.Ordinal);
        foreach (var (path, global) in layers)
        {
            if (path is null) continue;
            foreach (var (key, value) in ReadFlatYaml(path, resourceDefaults))
            {
                merged[key] = value;
                if (global) globalKeys.Add(key);
            }
        }

        if (layers.All(layer => layer.Path is null))
        {
            throw new InvalidDataException($"Для сервиса '{service}' не найдено ни одного YAML файла.");
        }

        return new YamlConfiguration(merged, globalKeys, resourceDefaults);
    }

    private static string? FindFirst(string directory, string stem)
    {
        foreach (var extension in new[] { ".yml", ".yaml" })
        {
            var candidate = Path.Combine(directory, stem + extension);
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }

    private static Dictionary<string, string?> ReadFlatYaml(string path,
        Dictionary<string, Dictionary<string, string?>> resourceDefaults)
    {
        using var reader = File.OpenText(path);
        var stream = new YamlStream();
        try
        {
            stream.Load(reader);
        }
        catch (Exception error)
        {
            throw new InvalidDataException($"Не удалось прочитать YAML '{path}': {error.Message}", error);
        }

        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (stream.Documents.Count == 0 ||
            stream.Documents.Count == 1 &&
            stream.Documents[0].RootNode is YamlScalarNode empty &&
            string.IsNullOrWhiteSpace(empty.Value))
            return result;

        if (stream.Documents.Count != 1 || stream.Documents[0].RootNode is not YamlMappingNode map)
        {
            throw new InvalidDataException($"YAML должен содержать один объект с ключами: {path}");
        }

        foreach (var (keyNode, valueNode) in map.Children)
        {
            if (keyNode is not YamlScalarNode key || string.IsNullOrWhiteSpace(key.Value))
            {
                throw new InvalidDataException($"Некорректный ключ YAML в файле {path}");
            }

            if (key.Value.StartsWith("DEFAULT_RESOURCES_", StringComparison.Ordinal))
            {
                var resources = new Dictionary<string, string?>(StringComparer.Ordinal);
                if (valueNode is YamlMappingNode resourceMap)
                    foreach (var (resourceKey, resourceValue) in resourceMap.Children)
                        if (resourceKey is YamlScalarNode { Value: { } name } && resourceValue is YamlScalarNode value)
                            resources[name] = value.Value;
                resourceDefaults[key.Value] = resources;
            }
            result[key.Value] = valueNode is YamlScalarNode scalar ? scalar.Value : valueNode.ToString();
        }

        return result;
    }
}
