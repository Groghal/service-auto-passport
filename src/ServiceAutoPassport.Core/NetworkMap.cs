using System.Net;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using HtmlAgilityPack;
using YamlDotNet.RepresentationModel;

namespace ServiceAutoPassport.Core;

public sealed class NetworkServiceInput
{
    public string J2File { get; set; } = "";
    public string Label { get; set; } = "";
    public string YamlService { get; set; } = "";
    public string AddressVariables { get; set; } = "";
    public string Id => NetworkMapGenerator.Id(Path.GetFullPath(J2File));
}
public sealed class NetworkChoice
{
    public bool TargetReviewed { get; set; }
    public bool Include { get; set; } = true;
    public string Label { get; set; } = "";
    public string Protocol { get; set; } = "Unknown";
    public string TargetService { get; set; } = "";
}
public sealed class NetworkDisplayOptions
{
    public bool HideUnknownProtocols { get; set; }
    public bool HideParameterNames { get; set; }
    public bool ShowConfigurationKeys { get; set; } = true;
}
public sealed class NetworkMapSettings
{
    public NetworkDisplayOptions Display { get; set; } = new();
    public string TemplateHtml { get; set; } = "";
    public List<NetworkServiceInput> Services { get; set; } = new();
    public List<string> Environments { get; set; } = new() { "dev", "test", "demo", "lt", "preprod", "prod" };
    public Dictionary<string, int> Ports { get; set; } = new()
    {
        ["HTTP"] = 80, ["HTTPS"] = 443, ["gRPC"] = 80, ["gRPC TLS"] = 443,
        ["PostgreSQL"] = 5432, ["SQL Server"] = 1433, ["MySQL"] = 3306, ["MongoDB"] = 27017,
        ["Redis"] = 6379, ["Kafka"] = 9092, ["Kafka TLS"] = 9093, ["AMQP"] = 5672,
        ["AMQPS"] = 5671, ["SMTP"] = 25, ["SMTPS"] = 465, ["SMTP submission"] = 587
    };
    public Dictionary<string, NetworkChoice> Choices { get; set; } = new();
}
public sealed class NetworkConnection
{
    public required NetworkServiceInput Service { get; init; }
    public required string Path { get; init; }
    public required string Host { get; init; }
    public string Port { get; init; } = "";
    public required NetworkChoice Choice { get; init; }
    public string[] HostKeys => J2Variables.Extract(Host).ToArray();
    public string Parameters => string.Join(", ", J2Variables.Extract(Host + " " + Port));
    public string TargetKey => HostKeys.Length > 0 ? "parameter:" + string.Join("|", HostKeys.OrderBy(k => k, StringComparer.Ordinal)) : "literal:" + Service.Id + ":" + Path;
    public string Key => Service.Id + ":" + Path;
    public string RowKey => TargetKey + "|" + Port + "|" + Choice.Protocol;
    internal Dictionary<string, IReadOnlyDictionary<string, string?>> Values { get; } = new();
    public Dictionary<string, string> Endpoints { get; } = new();
}
public sealed record NetworkAnalysis(IReadOnlyList<NetworkServiceInput> Services, IReadOnlyList<string> Environments,
    IReadOnlyList<NetworkConnection> Connections, IReadOnlyList<string> Diagnostics, NetworkDisplayOptions? Display = null);
public sealed record NetworkMapResult(string HtmlFragment, string PlainText, string DrawioXml);

public static class NetworkMapGenerator
{
    internal static string Id(string text) => "n" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];
    private static readonly Regex Placeholder = new(@"\{\{\s*([A-Za-z_][A-Za-z0-9_]*)\s*\}\}");
    private static readonly Regex Sentinel = new(@"J2REF_([A-Za-z_][A-Za-z0-9_]*?)_J2END");
    private static readonly Regex EndpointName = new(@"(hosts?|urls?|uris?|endpoints?|bootstrapservers|servers)$", RegexOptions.IgnoreCase);
    private static readonly Regex Sensitive = new(@"(password|secret|token|username|accesskey|certificate|keystore|truststore|allowedhosts)", RegexOptions.IgnoreCase);

    internal static Dictionary<string, string?> Read(string file, bool template = false)
    {
        var text = File.ReadAllText(file);
        if (template)
        {
            if (text.Contains("{%") || text.Contains("{#")) throw new InvalidDataException($"Неподдерживаемая конструкция Jinja: {file}");
            text = Placeholder.Replace(text, m => "J2REF_" + m.Groups[1].Value + "_J2END");
            if (text.Contains("{{")) throw new InvalidDataException($"Неподдерживаемое выражение Jinja: {file}");
        }
        var stream = new YamlStream();
        try { stream.Load(new StringReader(text)); }
        catch (YamlDotNet.Core.YamlException e)
        { throw new InvalidDataException($"Ошибка YAML/J2: {file}, строка {e.Start.Line}, колонка {e.Start.Column}."); }
        if (stream.Documents.Count == 0 || stream.Documents.Count == 1 && stream.Documents[0].RootNode is YamlScalarNode empty && string.IsNullOrWhiteSpace(empty.Value)) return new();
        if (stream.Documents.Count != 1 || stream.Documents[0].RootNode is not YamlMappingNode)
            throw new InvalidDataException($"Ожидается объект конфигурации: {file}");
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        void Walk(YamlNode node, string path)
        {
            if (node is YamlMappingNode map)
                foreach (var pair in map.Children)
                    Walk(pair.Value, (path.Length == 0 ? "" : path + ".") + ((YamlScalarNode)pair.Key).Value!.Replace("__", "."));
            else if (node is YamlSequenceNode seq)
                for (var i = 0; i < seq.Children.Count; i++) Walk(seq.Children[i], path + "." + i);
            else if (node is YamlScalarNode scalar)
                result[path] = template ? Sentinel.Replace(scalar.Value ?? "", m => "{{ " + m.Groups[1].Value + " }}") : scalar.Value;
        }
        Walk(stream.Documents[0].RootNode, "");
        return result;
    }
    public static NetworkAnalysis Analyze(string root, NetworkMapSettings settings)
    {
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("Не найден app-params.");
        if (settings.Services.Count == 0) throw new InvalidDataException("Добавьте сервисы.");
        if (settings.Services.Select(s => Path.GetFullPath(s.J2File)).Distinct(StringComparer.OrdinalIgnoreCase).Count() != settings.Services.Count)
            throw new InvalidDataException("Один J2 выбран несколько раз.");
        if (settings.Environments.Count == 0 || settings.Environments.Any(e => !Regex.IsMatch(e, @"^[A-Za-z0-9_-]+$")) ||
            settings.Environments.Distinct(StringComparer.OrdinalIgnoreCase).Count() != settings.Environments.Count)
            throw new InvalidDataException("Укажите уникальные имена окружений без пробелов и разделителей пути.");
        var diagnostics = new List<string>();
        var connections = new List<NetworkConnection>();
        var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var service in settings.Services)
        {
            foreach (var alias in Split(service.AddressVariables))
                if (!aliases.TryAdd(alias, service.Id)) throw new InvalidDataException($"Адресная переменная назначена нескольким сервисам: {alias}");
        }
        foreach (var service in settings.Services)
        {
            if (string.IsNullOrWhiteSpace(service.Label)) service.Label = ServiceComponent.DeploymentName(Path.GetDirectoryName(service.J2File)!);
            var config = Read(service.J2File, true);
            var folder = Path.GetDirectoryName(service.J2File)!;
            var filename = Path.GetFileName(service.J2File);
            var secret = filename.EndsWith(".deploy.j2", StringComparison.OrdinalIgnoreCase)
                ? Path.Combine(folder, filename[..^10] + ".secrets.j2") : "";
            if (!File.Exists(secret)) secret = Path.Combine(folder, "secrets.j2");
            if (File.Exists(secret)) foreach (var pair in Read(secret, true)) config[pair.Key] = pair.Value;
            var envs = settings.Environments.ToDictionary(e => e, e => YamlConfiguration.LoadForEnvironment(root, service.YamlService, e));
            foreach (var pair in config)
            {
                var path = pair.Key;
                var value = pair.Value ?? "";
                if (value.Length == 0 || path.StartsWith("Logging.", StringComparison.OrdinalIgnoreCase) || path.StartsWith("Kestrel.", StringComparison.OrdinalIgnoreCase) ||
                    path.StartsWith("Urls", StringComparison.OrdinalIgnoreCase)) continue;
                var leaf = Regex.Replace(path, @"\.\d+$", "").Split('.').Last();
                if (Sensitive.IsMatch(leaf) || leaf.Equals("VirtualHost", StringComparison.OrdinalIgnoreCase) || value.StartsWith("/")) continue;
                var isDb = path.StartsWith("ConnectionStrings.", StringComparison.OrdinalIgnoreCase) || leaf.EndsWith("ConnectionString", StringComparison.OrdinalIgnoreCase);
                if (!isDb && !EndpointName.IsMatch(leaf)) continue;
                var host = value;
                var port = "";
                var protocol = Guess(path, value, config);
                if (isDb)
                {
                    if (TryDatabase(value, out var dbHost, out var dbPort)) { host = dbHost; port = dbPort; }
                    else if (J2Variables.Extract(value).Count != 1) { diagnostics.Add(service.Label + ": " + path + " — формат строки подключения не распознан."); continue; }
                }
                else
                {
                    var parent = path.Contains('.') ? path[..path.LastIndexOf('.')] + "." : "";
                    config.TryGetValue(parent + "Port", out var siblingPort);
                    port = siblingPort ?? "";
                }
                // Ignore URL paths/query parameters for target identity and parameter grouping.
                if (host.Contains("://")) host = Regex.Replace(host, @"^(\w+://[^/?#]+).*$", "$1");
                host = Regex.Replace(host, @"(?<=://)[^/@]*@", "");
                var authorityPort = Regex.Match(host, @"^(.*):(\{\{\s*[A-Za-z_][A-Za-z0-9_]*\s*\}\}|\d+)$");
                if (authorityPort.Success && port.Length == 0) { host = authorityPort.Groups[1].Value; port = authorityPort.Groups[2].Value; }
                var key = service.Id + ":" + path;
                if (!settings.Choices.TryGetValue(key, out var choice))
                    settings.Choices[key] = choice = new NetworkChoice { Label = path, Protocol = protocol };
                var connection = new NetworkConnection { Service = service, Path = path, Host = host, Port = port, Choice = choice };
                if (choice.TargetService.Length > 0 && !settings.Services.Any(s => s.Id == choice.TargetService)) choice.TargetService = "";
                if (choice.TargetService.Length == 0 && !choice.TargetReviewed)
                {
                    var owners = connection.HostKeys.Where(aliases.ContainsKey).Select(k => aliases[k]).Distinct().ToArray();
                    if (owners.Length == 1) choice.TargetService = owners[0];
                    if (owners.Length > 1) throw new InvalidDataException(service.Label + ": неоднозначный целевой сервис для " + path);
                }
                foreach (var env in envs) connection.Values[env.Key] = env.Value;
                connections.Add(connection);
            }
        }
        var analysis = new NetworkAnalysis(settings.Services.ToArray(), settings.Environments.ToArray(), connections, diagnostics, settings.Display);
        Refresh(analysis, settings.Ports);
        foreach (var group in connections.GroupBy(c => c.RowKey))
            foreach (var env in settings.Environments)
                if (group.Select(c => c.Endpoints[env]).Distinct().Count() > 1)
                    diagnostics.Add(env + ": значения общих параметров различаются у сервисов " + string.Join(", ", group.Select(c => c.Service.Label)));
        return analysis;
    }
    private static bool TryDatabase(string value, out string host, out string port)
    {
        host = port = "";
        if (!value.Contains('=')) return false;
        try
        {
            value = Regex.Replace(value, @"(?:^|;)\s*\{\{[^{}]+\}\}\s*(?=;|$)", "");
            var builder = new DbConnectionStringBuilder { ConnectionString = value };
            foreach (var key in new[] { "Host", "Server", "Data Source", "Address" })
                if (builder.TryGetValue(key, out var result)) { host = result.ToString() ?? ""; break; }
            if (builder.TryGetValue("Port", out var p)) port = p.ToString() ?? "";
            host = Regex.Replace(host, @"^tcp:", "", RegexOptions.IgnoreCase);
            var sqlPort = Regex.Match(host, @"^(.*),(\d+)$");
            if (sqlPort.Success) { host = sqlPort.Groups[1].Value; if (port.Length == 0) port = sqlPort.Groups[2].Value; }
            return host.Length > 0;
        }
        catch (ArgumentException) { return false; }
    }

    public static string[] Split(string value) => value.Split(new[] { ',', ';', ' ', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    private static string? SchemeProtocol(string scheme) => scheme.ToLowerInvariant() switch
    {
        "http" => "HTTP", "https" => "HTTPS", "grpc" => "gRPC", "grpcs" => "gRPC TLS",
        "amqp" => "AMQP", "amqps" => "AMQPS", "smtp" => "SMTP", "smtps" => "SMTPS",
        "redis" or "rediss" => "Redis", "kafka" => "Kafka", "mongodb" => "MongoDB",
        "postgres" or "postgresql" => "PostgreSQL", "mysql" => "MySQL", "sqlserver" => "SQL Server", _ => null
    };
    private static string Guess(string path, string value, Dictionary<string, string?> config)
    {
        var scheme = Regex.Match(value, @"^(\w+)://").Groups[1].Value;
        if (SchemeProtocol(scheme) is { } known) return known;
        if (path.Contains("Grpc", StringComparison.OrdinalIgnoreCase)) return "gRPC";
        if (path.Contains("Kafka", StringComparison.OrdinalIgnoreCase)) return config.Any(p => p.Key.StartsWith(path.Split('.')[0]) && p.Key.EndsWith("SecurityProtocol") && (p.Value ?? "").Contains("ssl", StringComparison.OrdinalIgnoreCase)) ? "Kafka TLS" : "Kafka";
        if (path.Contains("Rmq", StringComparison.OrdinalIgnoreCase) || path.Contains("Rabbit", StringComparison.OrdinalIgnoreCase)) return "AMQP";
        if (path.Contains("Redis", StringComparison.OrdinalIgnoreCase)) return "Redis";
        if (path.Contains("Smtp", StringComparison.OrdinalIgnoreCase)) return "SMTP";
        if (value.Contains("Host=", StringComparison.OrdinalIgnoreCase) && path.StartsWith("ConnectionStrings.")) return "PostgreSQL";
        return "Unknown";
    }
    private static string Expand(string expression, IReadOnlyDictionary<string, string?> values, HashSet<string> stack)
    {
        if (stack.Count > 128) throw new InvalidDataException("слишком глубокая цепочка ссылок");
        return Placeholder.Replace(expression, match =>
        {
            var key = match.Groups[1].Value;
            if (!stack.Add(key)) throw new InvalidDataException("циклическая ссылка: " + key);
            if (!values.TryGetValue(key, out var value) || value is null) throw new InvalidDataException("не найдено: " + key);
            if (TryDatabase(value, out var dbHost, out var dbPort)) value = dbHost + (dbPort.Length > 0 ? ":" + dbPort : "");
            if (value.Contains("://")) value = Regex.Replace(Regex.Replace(value, @"(?<=://)[^/@]*@", ""), @"^(\w+://[^/?#]+).*$", "$1");
            var result = Expand(value, values, stack);
            stack.Remove(key);
            return result;
        });
    }
    public static void Refresh(NetworkAnalysis analysis, IReadOnlyDictionary<string, int> ports)
    {
        foreach (var c in analysis.Connections)
            foreach (var env in analysis.Environments)
            {
                try
                {
                    var host = Expand(c.Host, c.Values[env], new());
                    var port = Expand(c.Port, c.Values[env], new());
                    var results = new List<string>();
                    if (TryDatabase(host, out var dbHost, out var dbPort)) { host = dbHost; if (port.Length == 0) port = dbPort; }
                    if (host.Contains('=')) throw new InvalidDataException("формат строки подключения не распознан");
                    foreach (var address in host.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        string hostname;
                        var selectedPort = port;
                        var uriText = address.Contains("://") ? address : "tcp://" + address;
                        if (!Uri.TryCreate(uriText, UriKind.Absolute, out var uri) || uri.Host.Length == 0 || uri.Host.Contains('{'))
                            throw new InvalidDataException("адрес не распознан");
                        hostname = uri.Host.Trim('[', ']');
                        if (hostname.Contains(':')) hostname = "[" + hostname + "]";
                        var explicitAuthority = Regex.IsMatch(uri.Authority, @"(?:\]|[^:]):\d+$");
                        if (selectedPort.Length == 0 && explicitAuthority) selectedPort = uri.Port.ToString();
                        var inferred = false;
                        if (selectedPort.Length == 0)
                        {
                            var protocol = SchemeProtocol(uri.Scheme) ?? c.Choice.Protocol;
                            if (ports.TryGetValue(protocol, out var fallback)) { selectedPort = fallback.ToString(); inferred = true; }
                        }
                        if (selectedPort.Length > 0 && (!int.TryParse(selectedPort, out var numeric) || numeric is < 1 or > 65535))
                            throw new InvalidDataException("некорректный порт");
                        results.Add(hostname + (selectedPort.Length == 0 ? " (порт не указан)" : ":" + selectedPort) + (inferred ? " (порт по умолчанию)" : ""));
                    }
                    c.Endpoints[env] = results.Count == 0 ? "значение не найдено" : string.Join("\n", results.Distinct());
                }
                catch (InvalidDataException e) { c.Endpoints[env] = e.Message; }
            }
    }
    private static HtmlDocument Parse(string html) { var doc = new HtmlDocument(); doc.LoadHtml(CfHtml.ExtractFragment(html)); return doc; }
    private static HtmlNode DiagramSlot(HtmlDocument doc)
    {
        var nodes = doc.DocumentNode.Descendants().Where(n => n.GetAttributeValue("data-macro-name", "") == "drawio" || n.GetAttributeValue("data-network-diagram-placeholder", "") == "true").ToArray();
        return nodes.Length == 1 ? nodes[0] : throw new InvalidDataException("Страница должна содержать ровно один макрос drawio или место для диаграммы из базового шаблона.");
    }
    private static HtmlNode Table(HtmlDocument doc)
    {
        var matches = doc.DocumentNode.Descendants("table").Where(table =>
            TemplateTables.Find(new[] { table }, "Подключение", "Параметры") is not null).ToArray();
        return matches.Length == 1 ? matches[0] : throw new InvalidDataException("Нужна ровно одна таблица с заголовками Подключение и Параметры.");
    }
    public static void ValidateTemplate(string html) { var doc = Parse(html); DiagramSlot(doc); Table(doc); }
    public static NetworkMapResult Render(string templateHtml, string? clipboardHtml, NetworkAnalysis analysis)
    {
        var doc = Parse(templateHtml);
        var slot = DiagramSlot(doc);
        var macros = string.IsNullOrWhiteSpace(clipboardHtml) ? Array.Empty<HtmlNode>() : Parse(clipboardHtml).DocumentNode.Descendants()
            .Where(n => n.GetAttributeValue("data-macro-name", "") == "drawio").ToArray();
        if (macros.Length > 1) throw new InvalidDataException("В буфере несколько draw.io макросов. Скопируйте одну сетевую страницу.");
        var replacement = macros.Length == 1 ? macros[0] : DiagramSlot(Parse(NetworkPageTemplates.BasicHtml));
        slot.ParentNode!.ReplaceChild(replacement.CloneNode(true), slot);
        var table = Table(doc);
        var selected = SelectedConnections(analysis);
        static string H(string text) => WebUtility.HtmlEncode(text).Replace("\n", "<br />");
        var html = new StringBuilder("<tr><th>Подключение</th><th>Параметры</th><th>Сервисы</th>");
        foreach (var env in analysis.Environments) html.Append("<th>" + H(env) + "</th>");
        html.Append("</tr>");
        foreach (var group in selected.GroupBy(c => c.RowKey))
        {
            html.Append("<tr><td>" + H(string.Join("\n", group.Select(c => c.Choice.Label).Distinct())) + "</td><td>" + H(group.First().Parameters) + "</td><td>" + H(string.Join("\n", group.Select(c => c.Service.Label + " — " + c.Path))) + "</td>");
            foreach (var env in analysis.Environments)
            {
                var values = group.Select(c => c.Endpoints[env]).Distinct().ToArray();
                var text = values.Length == 1 ? values[0] : string.Join("\n", group.Select(c => c.Service.Label + " — " + c.Endpoints[env]));
                html.Append("<td>" + H(text) + "</td>");
            }
            html.Append("</tr>");
        }
        table.InnerHtml = html.ToString();
        return new(doc.DocumentNode.InnerHtml, HtmlEntity.DeEntitize(doc.DocumentNode.InnerText), Diagram(analysis, selected));
    }
    public static string CreateDiagram(NetworkAnalysis analysis) => Diagram(analysis, SelectedConnections(analysis));

    private static NetworkConnection[] SelectedConnections(NetworkAnalysis analysis)
    {
        var selected = analysis.Connections.Where(c => c.Choice.Include).OrderBy(c => c.Key, StringComparer.Ordinal).ToArray();
        if (selected.Length == 0) throw new InvalidDataException("Не выбрано ни одного подключения.");
        if (selected.Any(c => c.Choice.TargetService.Length > 0 && !analysis.Services.Any(s => s.Id == c.Choice.TargetService)))
            throw new InvalidDataException("Выбран целевой сервис, отсутствующий на карте.");
        return selected;
    }

    private static string Diagram(NetworkAnalysis analysis, NetworkConnection[] connections)
    {
        var display = analysis.Display ?? new NetworkDisplayOptions();
        var vertices = analysis.Services.Select(s => new DiagramVertex(s.Id, s.Label, DiagramColumn.Services)).ToList();
        foreach (var group in connections.Where(c => c.Choice.TargetService.Length == 0)
                     .GroupBy(c => c.TargetKey).OrderBy(g => g.Key, StringComparer.Ordinal))
            vertices.Add(new DiagramVertex(Id(group.Key), string.Join("\n", group.Select(c => c.Choice.Label).Distinct()) +
                (display.HideParameterNames ? "" : "\n" + string.Join(", ", group.First().HostKeys)),
                group.First().HostKeys.Any(key => key.StartsWith("SVC_", StringComparison.Ordinal))
                    ? DiagramColumn.SvcDependencies : DiagramColumn.OtherDependencies));
        var edges = connections.Select(c => new DiagramLink(Id(c.Key + "edge"), c.Service.Id,
            c.Choice.TargetService.Length > 0 ? c.Choice.TargetService : Id(c.TargetKey),
            string.Join("\n", new[] { display.ShowConfigurationKeys ? c.Path : "",
                display.HideUnknownProtocols && c.Choice.Protocol.Equals("Unknown", StringComparison.OrdinalIgnoreCase) ? "" : c.Choice.Protocol }
                .Where(text => text.Length > 0)), c.Path));
        return NetworkDiagramLayout.Render(vertices, edges);
    }
}
