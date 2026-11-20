using System.Globalization;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace ServiceAutoPassport.Core;

internal readonly record struct ConfigurationPart(string Text, bool IsArrayIndex = false);
internal sealed record ConfigurationField(ConfigurationPart[] Path, string Value, bool IsPlain);

internal static class ServiceConfiguration
{
    private static readonly Regex Sentinel = new(@"J2REF_([A-Za-z_][A-Za-z0-9_]*?)_J2END");

    public static Dictionary<string, ConfigurationField> Read(string j2File)
    {
        var configuration = new Dictionary<string, ConfigurationField>(StringComparer.OrdinalIgnoreCase);
        AddFile(j2File);
        var folder = Path.GetDirectoryName(j2File)!;
        var filename = Path.GetFileName(j2File);
        const string deploySuffix = ".deploy.j2";
        var secrets = filename.EndsWith(deploySuffix, StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(folder, filename[..^deploySuffix.Length] + ".secrets.j2") : "";
        if (!File.Exists(secrets)) secrets = Path.Combine(folder, "secrets.j2");
        if (File.Exists(secrets)) AddFile(secrets);
        return configuration;

        void AddFile(string file)
        {
            var text = File.ReadAllText(file);
            if (text.Contains("{%") || text.Contains("{#")) throw new InvalidDataException($"Неподдерживаемая конструкция Jinja: {file}");
            text = J2Variables.PlaceholderRegex().Replace(text, match => "J2REF_" + match.Groups[1].Value + "_J2END");
            if (text.Contains("{{")) throw new InvalidDataException($"Неподдерживаемое выражение Jinja: {file}");
            var stream = new YamlStream();
            try { stream.Load(new StringReader(text)); }
            catch (YamlException error)
            { throw new InvalidDataException($"Ошибка YAML/J2: {file}, строка {error.Start.Line}, колонка {error.Start.Column}."); }
            if (stream.Documents.Count == 0 || stream.Documents.Count == 1 &&
                stream.Documents[0].RootNode is YamlScalarNode empty && string.IsNullOrWhiteSpace(empty.Value)) return;
            if (stream.Documents.Count != 1 || stream.Documents[0].RootNode is not YamlMappingNode root)
                throw new InvalidDataException($"Ожидается объект конфигурации: {file}");
            foreach (var field in ReadFields(root, []))
            {
                var key = string.Join('.', field.Path.Select(part => part.Text));
                var value = Sentinel.Replace(field.Value, match => "{{ " + match.Groups[1].Value + " }}");
                var path = configuration.TryGetValue(key, out var previous) ? previous.Path : field.Path;
                configuration[key] = field with { Path = path, Value = value };
            }
        }
    }

    public static IEnumerable<ConfigurationField> ReadFields(YamlNode node, ConfigurationPart[] path)
    {
        if (node is YamlMappingNode map)
        {
            foreach (var pair in map.Children)
            {
                var key = ((YamlScalarNode)pair.Key).Value!;
                var parts = key.Split("__", StringSplitOptions.None);
                var childPath = parts.Select(part => new ConfigurationPart(part,
                    parts.Length > 1 && int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out _)));
                foreach (var field in ReadFields(pair.Value, [.. path, .. childPath])) yield return field;
            }
        }
        else if (node is YamlSequenceNode sequence)
        {
            for (var i = 0; i < sequence.Children.Count; i++)
                foreach (var field in ReadFields(sequence.Children[i], [.. path, new(i.ToString(CultureInfo.InvariantCulture), true)]))
                    yield return field;
        }
        else if (node is YamlScalarNode scalar)
        {
            yield return new ConfigurationField(path, scalar.Value ?? "", scalar.Style == ScalarStyle.Plain);
        }
    }
}
