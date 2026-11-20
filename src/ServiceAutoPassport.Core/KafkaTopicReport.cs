using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace ServiceAutoPassport.Core;

[Flags]
[JsonConverter(typeof(JsonStringEnumConverter<KafkaTopicDirection>))]
public enum KafkaTopicDirection { Read = 1, Write = 2 }

public sealed record KafkaTopicRule(KafkaTopicDirection Direction, string Path);
public sealed record KafkaTopicResult(string HtmlFragment, string PlainText, int ClusterCount, int TopicCount);

public static class KafkaTopicReport
{
    private const string ClusterMarker = "{kafka}";
    private static readonly Regex PathSegment = new(@"\A(?<key>\*|\{kafka\}|[^\s.\[\]{}*]+)(?<array>\[\])?\z");
    private static readonly Regex PlainNonString = new(@"\A(?:null|~|true|false|-?\d+(?:\.\d+)?(?:e[+-]?\d+)?)\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static IReadOnlyList<KafkaTopicRule> DefaultRules { get; } = Array.AsReadOnly<KafkaTopicRule>([
        new(KafkaTopicDirection.Read, "{kafka}.Consumer.Topics[]"),
        new(KafkaTopicDirection.Read, "*.{kafka}.Consumer.Topics[]"),
        new(KafkaTopicDirection.Write, "{kafka}.Producers[].TopicId"),
        new(KafkaTopicDirection.Write, "*.{kafka}.Producers[].TopicId")
    ]);

    private sealed record Segment(string Key, bool ArrayItems)
    {
        public bool CapturesCluster => Key == ClusterMarker;
        public bool Matches(string name) => CapturesCluster || Key == "*" || Key.Equals(name, StringComparison.OrdinalIgnoreCase);
    }

    private sealed record TopicRule(KafkaTopicDirection Direction, Segment[] Segments);
    private sealed record BrokerDetails(string[] Variables, string[] Addresses);
    private sealed record Cluster(string Label, SortedDictionary<string, KafkaTopicDirection> Topics, BrokerDetails? Brokers = null);
    private enum PathMatch { None, Array, Topic }

    private static TopicRule ParseRule(KafkaTopicRule? rule, int index)
    {
        if (rule is null || rule.Direction is not (KafkaTopicDirection.Read or KafkaTopicDirection.Write))
            throw new InvalidDataException($"В правиле {index + 1} укажите направление «Чтение» или «Запись».");
        var parts = (rule.Path ?? "").Split('.', StringSplitOptions.TrimEntries);
        var segments = new Segment[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            var match = PathSegment.Match(parts[i]);
            if (!match.Success)
                throw new InvalidDataException($"Некорректный путь в правиле {index + 1}. Например, *.{{kafka}}.Consumer.Topics[].");
            segments[i] = new Segment(match.Groups["key"].Value, match.Groups["array"].Success);
        }
        if (segments[^1].CapturesCluster || segments.Count(segment => segment.CapturesCluster) != 1)
            throw new InvalidDataException($"Правило {index + 1} должно содержать один раздел {{kafka}} и путь к топику внутри него.");
        return new TopicRule(rule.Direction, segments);
    }

    public static void ValidateRules(IReadOnlyList<KafkaTopicRule> rules)
    {
        for (var i = 0; i < rules.Count; i++) ParseRule(rules[i], i);
    }

    public static KafkaTopicResult Render(Stream json, IReadOnlyList<KafkaTopicRule> rules)
    {
        var paths = rules.Select(ParseRule).ToArray();
        using var document = ParseJson(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("В корне prod appsettings.json ожидается объект JSON.");
        return Format(FindTopics(ReadJsonFields(document.RootElement, []), paths));
    }

    public static KafkaTopicResult RenderForServices(string appParamsRoot, IReadOnlyList<NetworkServiceInput> services,
        IReadOnlyList<KafkaTopicRule> rules)
    {
        if (!Directory.Exists(appParamsRoot)) throw new DirectoryNotFoundException("Выберите каталог app-params на вкладке сетевых подключений.");
        if (services.Count == 0) throw new InvalidDataException("Добавьте сервисы на вкладке сетевых подключений.");
        var paths = rules.Select(ParseRule).ToArray();
        var clusters = new List<Cluster>();
        foreach (var service in services)
        {
            var label = string.IsNullOrWhiteSpace(service.Label)
                ? ServiceComponent.DeploymentName(Path.GetDirectoryName(Path.GetFullPath(service.J2File))!) : service.Label;
            try
            {
                var config = ServiceConfiguration.Read(service.J2File);
                var values = YamlConfiguration.LoadForEnvironment(appParamsRoot, service.YamlService, "prod");
                var bootstrap = config.Values.Select(field => (Cluster: BootstrapClusterPath(field.Path), Field: field))
                    .Where(entry => entry.Cluster is not null)
                    .ToLookup(entry => entry.Cluster!, entry => entry.Field, StringComparer.OrdinalIgnoreCase);
                clusters.AddRange(FindTopics(config.Values, paths, values)
                    .Select(cluster => cluster with
                    {
                        Label = $"{label} — {cluster.Label}",
                        Brokers = ReadBrokers(bootstrap[cluster.Label], values)
                    }));
            }
            catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                throw new InvalidDataException($"Сервис «{label}». {error.Message}", error);
            }
        }
        return Format(clusters);
    }

    private static string? BootstrapClusterPath(ConfigurationPart[] path)
    {
        var fieldIndex = path.Length - (path[^1].IsArrayIndex ? 2 : 1);
        return fieldIndex > 0 && !path[fieldIndex].IsArrayIndex &&
            path[fieldIndex].Text.Equals("BootstrapServers", StringComparison.OrdinalIgnoreCase)
            ? FormatPath(path.AsSpan(0, fieldIndex)) : null;
    }

    private static BrokerDetails ReadBrokers(IEnumerable<ConfigurationField> fields, IReadOnlyDictionary<string, string?> values)
    {
        var variables = fields.SelectMany(field => J2Variables.Extract(field.Value)).Distinct(StringComparer.Ordinal).ToArray();
        var addresses = fields.SelectMany(Resolve).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return new BrokerDetails(variables, addresses);

        IEnumerable<string> Resolve(ConfigurationField field)
        {
            try { return ReadBrokerAddresses(J2Variables.Expand(field.Value, values).Trim()); }
            catch (InvalidDataException) { return []; }
        }
    }

    private static IEnumerable<string> ReadBrokerAddresses(string text)
    {
        if (BrokerAddress(text) is { } address) return [address];
        if (text.StartsWith('[') && text.EndsWith(']'))
            return ReadArrayParameter(text, []).Where(item => item.Path.Length == 1)
                .SelectMany(item => ReadBrokerAddresses(item.Value.Trim())).ToArray();
        // Treat a URI with user info as a whole, since delimiters can occur inside the credentials.
        if (text.Contains('@')) return [];
        return text.Split([',', ';'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(BrokerAddress).OfType<string>().ToArray();
    }

    private static string? BrokerAddress(string text)
    {
        if (text.Contains("{{") || PlainNonString.IsMatch(text)) return null;
        if (NetworkMapGenerator.ParseAddress(text) is not { } endpoint || Uri.CheckHostName(endpoint.Host) == UriHostNameType.Unknown)
            return null;
        if (endpoint.Port.Length == 0) return endpoint.Host;
        return NetworkMapGenerator.TryPort(endpoint.Port, out _) ? $"{endpoint.Host}:{endpoint.Port}" : null;
    }

    private static IReadOnlyList<Cluster> FindTopics(IEnumerable<ConfigurationField> fields, TopicRule[] rules,
        IReadOnlyDictionary<string, string?>? values = null)
    {
        var clusters = new SortedDictionary<string, SortedDictionary<string, KafkaTopicDirection>>(
            values is null ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);
        foreach (var field in fields)
        {
            string? resolved = null;
            ConfigurationField[]? items = null;
            try
            {
                foreach (var rule in rules)
                {
                    var match = MatchPath(field.Path, rule.Segments, out var clusterDepth);
                    if (match == PathMatch.Topic)
                        AddTopic(field, Resolve(), rule.Direction, clusterDepth);
                    else if (match == PathMatch.Array && field.IsPlain && values is not null && J2Variables.PlaceholderRegex().IsMatch(field.Value))
                    {
                        // A bare J2 expression can supply the entire Topics or Producers array.
                        items ??= ReadArrayParameter(Resolve(), field.Path);
                        foreach (var item in items)
                            if (MatchPath(item.Path, rule.Segments, out var itemClusterDepth) == PathMatch.Topic)
                                AddTopic(item, item.Value, rule.Direction, itemClusterDepth);
                    }
                }
            }
            catch (InvalidDataException error)
            {
                throw new InvalidDataException($"Поле «{FormatPath(field.Path)}». {error.Message}", error);
            }

            string Resolve() => resolved ??= values is null ? field.Value : J2Variables.Expand(field.Value, values);
        }
        return clusters.Select(pair => new Cluster(pair.Key, pair.Value)).ToArray();

        void AddTopic(ConfigurationField field, string value, KafkaTopicDirection direction, int clusterDepth)
        {
            var topic = value.Trim();
            if (topic.Length == 0 || field.IsPlain && PlainNonString.IsMatch(topic)) return;
            if (values is not null && topic.Contains("{{")) throw new InvalidDataException("Неподдерживаемое выражение Jinja в топике.");
            var cluster = FormatPath(field.Path.AsSpan(0, clusterDepth));
            if (!clusters.TryGetValue(cluster, out var topics))
                clusters[cluster] = topics = new SortedDictionary<string, KafkaTopicDirection>(StringComparer.Ordinal);
            topics[topic] = topics.GetValueOrDefault(topic) | direction;
        }
    }

    private static PathMatch MatchPath(ConfigurationPart[] path, Segment[] rule, out int clusterDepth)
    {
        var depth = 0;
        clusterDepth = 0;
        foreach (var segment in rule)
        {
            if (depth >= path.Length || path[depth].IsArrayIndex || !segment.Matches(path[depth].Text)) return PathMatch.None;
            depth++;
            if (segment.ArrayItems)
            {
                if (depth == path.Length) return PathMatch.Array;
                if (!path[depth].IsArrayIndex) return PathMatch.None;
                depth++;
            }
            if (segment.CapturesCluster) clusterDepth = depth;
        }
        return depth == path.Length ? PathMatch.Topic : PathMatch.None;
    }

    private static ConfigurationField[] ReadArrayParameter(string value, ConfigurationPart[] path)
    {
        if (!value.TrimStart().StartsWith('[')) return [];
        var stream = new YamlStream();
        try { stream.Load(new StringReader(value)); }
        catch (YamlException error) { throw new InvalidDataException("Значение не удалось разобрать как массив.", error); }
        return stream.Documents.Count == 1 && stream.Documents[0].RootNode is YamlSequenceNode array
            ? ServiceConfiguration.ReadFields(array, path).ToArray() : [];
    }

    private static IEnumerable<ConfigurationField> ReadJsonFields(JsonElement element, ConfigurationPart[] path)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
                foreach (var field in ReadJsonFields(property.Value, [.. path, new(property.Name)])) yield return field;
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in element.EnumerateArray())
                foreach (var field in ReadJsonFields(item, [.. path, new((index++).ToString(CultureInfo.InvariantCulture), true)])) yield return field;
        }
        else if (element.ValueKind == JsonValueKind.String)
        {
            yield return new ConfigurationField(path, element.GetString()!, false);
        }
    }

    private static KafkaTopicResult Format(IReadOnlyList<Cluster> clusters)
    {
        var html = new StringBuilder();
        var plainText = new StringBuilder();
        foreach (var cluster in clusters)
        {
            html.Append("<h3>").Append(WebUtility.HtmlEncode(cluster.Label)).Append("</h3>");
            plainText.AppendLine(cluster.Label);
            if (cluster.Brokers is { } brokers)
            {
                var hasVariables = brokers.Variables.Length > 0;
                var label = hasVariables ? string.Join(", ", brokers.Variables) : "Брокеры prod";
                var addresses = brokers.Addresses.Length > 0 ? string.Join(", ", brokers.Addresses) : "Адрес prod не найден";
                html.Append("<p>");
                if (hasVariables) html.Append("<code>");
                html.Append(WebUtility.HtmlEncode(label));
                if (hasVariables) html.Append("</code>");
                html.Append(" → ").Append(WebUtility.HtmlEncode(addresses)).Append("</p>");
                plainText.Append(label).Append(" → ").AppendLine(addresses);
            }
            html.Append("<table class=\"confluenceTable\"><tbody><tr>")
                .Append("<th class=\"confluenceTh\">Топик</th><th class=\"confluenceTh\">Чтение</th><th class=\"confluenceTh\">Запись</th></tr>");
            plainText.AppendLine("Топик\tЧтение\tЗапись");
            foreach (var (topic, direction) in cluster.Topics)
            {
                var read = direction.HasFlag(KafkaTopicDirection.Read) ? "Да" : "—";
                var write = direction.HasFlag(KafkaTopicDirection.Write) ? "Да" : "—";
                html.Append("<tr><td class=\"confluenceTd\">").Append(WebUtility.HtmlEncode(topic))
                    .Append("</td><td class=\"confluenceTd\">").Append(read)
                    .Append("</td><td class=\"confluenceTd\">").Append(write).Append("</td></tr>");
                plainText.Append(topic).Append('\t').Append(read).Append('\t').AppendLine(write);
            }
            html.Append("</tbody></table>");
            plainText.AppendLine();
        }
        return new KafkaTopicResult(html.ToString(), plainText.ToString().TrimEnd(), clusters.Count, clusters.Sum(cluster => cluster.Topics.Count));
    }

    private static string FormatPath(ReadOnlySpan<ConfigurationPart> path)
    {
        var result = "";
        foreach (var part in path)
            result = part.IsArrayIndex ? $"{result}[{part.Text}]" : PropertyPath(result, part.Text);
        return result;
    }

    private static string PropertyPath(string parent, string key)
    {
        if (key.Length == 0 || key.Any(character => character is '.' or '[' or ']' or '"' or '\\' || char.IsControl(character)))
            return parent + "[" + JsonSerializer.Serialize(key) + "]";
        return parent.Length == 0 ? key : parent + "." + key;
    }

    private static JsonDocument ParseJson(Stream json)
    {
        try
        {
            return JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        }
        catch (JsonException error)
        {
            throw new InvalidDataException($"Некорректный JSON. Строка {error.LineNumber + 1}, позиция {error.BytePositionInLine + 1}.", error);
        }
    }
}
