using YamlDotNet.RepresentationModel;

namespace ServiceAutoPassport.Core;

public sealed class YamlConfiguration
{
    private YamlConfiguration(Dictionary<string, string?> values, HashSet<string> globalKeys)
    {
        Values = values;
        GlobalKeys = globalKeys;
    }

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

    public string ResourceOrDefault(string key, string fallback)
    {
        return Values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value : fallback;
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
        foreach (var (path, global) in layers)
        {
            if (path is null) continue;
            foreach (var (key, value) in ReadFlatYaml(path))
            {
                merged[key] = value;
                if (global) globalKeys.Add(key);
            }
        }

        if (layers.All(layer => layer.Path is null))
        {
            throw new InvalidDataException($"Для сервиса '{service}' не найдено ни одного YAML файла.");
        }

        return new YamlConfiguration(merged, globalKeys);
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

    private static Dictionary<string, string?> ReadFlatYaml(string path)
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

        if (stream.Documents.Count != 1 || stream.Documents[0].RootNode is not YamlMappingNode map)
        {
            throw new InvalidDataException($"YAML должен содержать один объект с ключами: {path}");
        }

        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (keyNode, valueNode) in map.Children)
        {
            if (keyNode is not YamlScalarNode key || string.IsNullOrWhiteSpace(key.Value))
            {
                throw new InvalidDataException($"Некорректный ключ YAML в файле {path}");
            }

            result[key.Value] = valueNode is YamlScalarNode scalar ? scalar.Value : valueNode.ToString();
        }

        return result;
    }
}
