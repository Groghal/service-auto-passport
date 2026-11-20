using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace ServiceAutoPassport.Core;

public sealed class NetworkServiceInput
{
    public string J2File { get; set; } = "";
    public string Label { get; set; } = "";
    public string Block { get; set; } = NetworkTargetSettings.DefaultBlock;
    public string? ParentBlock { get; set; }
    public string YamlService { get; set; } = "";
    public string AddressVariables { get; set; } = "";
    public string Id => NetworkMapGenerator.Id(Path.GetFullPath(J2File));
}
public sealed class NetworkChoice
{
    public bool TargetReviewed { get; set; }
    public bool Include { get; set; } = true;
    public string Protocol { get; set; } = "Unknown";
    public string TargetService { get; set; } = "";
}
public sealed class NetworkTargetSettings
{
    public const string DefaultBlock = "k8s";
    public string Label { get; set; } = "";
    public string Block { get; set; } = DefaultBlock;
    public string? ParentBlock { get; set; }
    public bool LabelIsAutomatic { get; set; }
    public bool BlockIsAutomatic { get; set; }
    public string? SourceService { get; set; }
    public string? UrlPathParameters { get; set; }
}
public sealed class NetworkDisplayOptions
{
    public bool HideProtocolsOnArrows { get; set; }
    public bool ShowConfigurationKeys { get; set; }
    public bool ColorArrowsByService { get; set; } = true;
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
    public Dictionary<string, NetworkTargetSettings> Targets { get; set; } = new();
}
public sealed class NetworkConnection
{
    private static readonly Regex AddressSuffix = new(
        @"(?:(?:^|[_\s-]+)(?i:url|host)+|(?<=[\p{Ll}\p{Nd}])(?=\p{Lu})(?i:url|host)+)\z");

    public required NetworkServiceInput Service { get; init; }
    public required string Path { get; init; }
    public required string Host { get; init; }
    public string Port { get; init; } = "";
    public IReadOnlyList<string> UrlPathKeys { get; init; } = [];
    public required NetworkChoice Choice { get; init; }
    public NetworkTargetSettings ExternalTarget { get; init; } = new();
    public string[] HostKeys => J2Variables.Extract(Host).ToArray();
    internal IEnumerable<string> ParameterKeys => HostKeys.Concat(J2Variables.Extract(Port)).Concat(UrlPathKeys);
    public string Parameters => string.Join(", ", ParameterKeys.Distinct(StringComparer.Ordinal));
    public string TargetKey => GetTargetKey(Service, Path, HostKeys);
    internal static string GetTargetKey(NetworkServiceInput service, string path, IReadOnlyList<string> hostKeys) =>
        hostKeys.Count > 0 ? NetworkParameterCatalog.AddressKey(hostKeys) : NetworkParameterCatalog.LiteralKey(service.Id, path);
    internal static bool IsAddressVariable(string key) =>
        key.EndsWith("URL", StringComparison.OrdinalIgnoreCase) || key.EndsWith("HOST", StringComparison.OrdinalIgnoreCase);
    internal bool HasAddressVariable => HostKeys.Concat(UrlPathKeys).Any(IsAddressVariable);
    public string Key => Service.Id + ":" + Path;
    internal string TargetId => Choice.TargetService.Length == 0 ? NetworkMapGenerator.Id(TargetKey) : Choice.TargetService;
    internal string SuggestedTargetLabel
    {
        get
        {
            var parts = Path.Split('.').Where(part => !int.TryParse(part, out _)).ToArray();
            var context = parts.LastOrDefault(part => !Regex.IsMatch(part,
                @"^(hosts?|urls?|uris?|endpoints?|bootstrapservers|servers|connectionstrings?)$", RegexOptions.IgnoreCase));
            var label = WithoutAddressSuffix(context ?? "");
            if (label.Length > 0) return label;
            var parameter = HostKeys.Concat(UrlPathKeys).Select(WithoutAddressSuffix).Select(key => key.Replace('_', ' '))
                .FirstOrDefault(key => key.Length > 0);
            var pathLabel = WithoutAddressSuffix(Path);
            return parameter ?? (pathLabel.Length == 0 ? "Подключение" : pathLabel);
        }
    }

    private static string WithoutAddressSuffix(string value)
    {
        value = value.TrimEnd('_', '-', ' ');
        while (AddressSuffix.Match(value) is { Success: true } suffix)
            value = value[..suffix.Index].TrimEnd('_', '-', ' ');
        return value;
    }
    internal Dictionary<string, IReadOnlyDictionary<string, string?>> Values { get; } = new();
    internal HashSet<NetworkTransport> Transports { get; } = new();
    public Dictionary<string, string> Endpoints { get; } = new();
}
internal readonly record struct NetworkTransport(int Port, string? Protocol);
internal readonly record struct NetworkAddress(string Host, string Port, string Scheme);
public sealed record NetworkAnalysis(IReadOnlyList<NetworkServiceInput> Services, IReadOnlyList<string> Environments,
    IReadOnlyList<NetworkConnection> Connections, IReadOnlyList<string> Diagnostics, NetworkDisplayOptions? Display = null);
public sealed record NetworkTableResult(string HtmlFragment, string PlainText);

public static class NetworkMapGenerator
{
    internal static string Id(string text) => "n" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];
    private static readonly Regex Placeholder = J2Variables.PlaceholderRegex();
    private static readonly Regex EndpointName = new(@"(hosts?|urls?|uris?|endpoints?|bootstrapservers|servers)$", RegexOptions.IgnoreCase);
    private static readonly Regex Sensitive = new(@"(password|secret|token|username|accesskey|certificate|keystore|truststore|allowedhosts)", RegexOptions.IgnoreCase);

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
            var config = ServiceConfiguration.Read(service.J2File)
                .ToDictionary(pair => pair.Key, pair => pair.Value.Value, StringComparer.OrdinalIgnoreCase);
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
                if (!Placeholder.IsMatch(value)) continue;
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
                // Path variables qualify the call, but the authority identifies and describes the endpoint.
                var url = Regex.Match(host, @"^(?<authority>(?:[A-Za-z][A-Za-z0-9+.-]*://)?[^/?#]+)(?<path>/[^?#]*)?");
                var urlPathKeys = J2Variables.Extract(url.Groups["path"].Value);
                if (url.Success) host = url.Groups["authority"].Value;
                host = Regex.Replace(host, @"(?<=://)[^/@]*@", "");
                var authorityPort = Regex.Match(host, @"^(.*):(\{\{\s*[A-Za-z_][A-Za-z0-9_]*\s*\}\}|\d+)$");
                if (authorityPort.Success)
                {
                    host = authorityPort.Groups[1].Value;
                    if (port.Length == 0) port = authorityPort.Groups[2].Value;
                }
                var hostKeys = J2Variables.Extract(host);
                if (!hostKeys.Concat(urlPathKeys).Any(NetworkConnection.IsAddressVariable)) continue;
                var targetKey = NetworkConnection.GetTargetKey(service, path, hostKeys);

                var key = service.Id + ":" + path;
                if (!settings.Choices.TryGetValue(key, out var choice))
                    settings.Choices[key] = choice = new NetworkChoice { Protocol = protocol };
                if (!settings.Targets.TryGetValue(targetKey, out var target))
                    settings.Targets[targetKey] = target = new NetworkTargetSettings
                    {
                        LabelIsAutomatic = true, BlockIsAutomatic = true
                    };
                if (hostKeys.Count == 0)
                {
                    target.SourceService = service.Label;
                    target.UrlPathParameters = string.Join(", ", urlPathKeys);
                }
                var connection = new NetworkConnection
                {
                    Service = service, Path = path, Host = host, Port = port, UrlPathKeys = urlPathKeys, Choice = choice, ExternalTarget = target
                };
                if (choice.TargetService.Length > 0 && !settings.Services.Any(s => s.Id == choice.TargetService)) choice.TargetService = "";
                if (choice.TargetService.Length == 0 && !choice.TargetReviewed)
                {
                    var owners = hostKeys.Where(aliases.ContainsKey).Select(k => aliases[k]).Distinct().ToArray();
                    if (owners.Length == 1) choice.TargetService = owners[0];
                    if (owners.Length > 1) throw new InvalidDataException(service.Label + ": неоднозначный целевой сервис для " + path);
                }
                foreach (var env in envs) connection.Values[env.Key] = env.Value;
                connections.Add(connection);
            }
        }
        foreach (var group in connections.GroupBy(c => c.ExternalTarget))
            if (string.IsNullOrWhiteSpace(group.Key.Label))
                group.Key.Label = group.OrderBy(c => c.Path, StringComparer.Ordinal).First().SuggestedTargetLabel;
        var analysis = new NetworkAnalysis(settings.Services.ToArray(), settings.Environments.ToArray(), connections, diagnostics, settings.Display);
        Refresh(analysis, settings.Ports);
        foreach (var group in connections.Where(c => c.Choice.Include).GroupBy(c => c.TargetId))
            foreach (var env in settings.Environments)
                if (group.Select(c => c.Endpoints[env]).Distinct().Count() > 1)
                    diagnostics.Add(env + ": адреса общего узла различаются у подключений " + string.Join(", ", group.Select(c => c.Service.Label + " — " + c.Path)));
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
    private static string Guess(string path, string value, Dictionary<string, string> config)
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
    private static string NormalizeEndpointValue(string value)
    {
        if (TryDatabase(value, out var dbHost, out var dbPort)) value = dbHost + (dbPort.Length > 0 ? ":" + dbPort : "");
        if (value.Contains("://")) value = Regex.Replace(Regex.Replace(value, @"(?<=://)[^/@]*@", ""), @"^(\w+://[^/?#]+).*$", "$1");
        return value;
    }
    public static void Refresh(NetworkAnalysis analysis, IReadOnlyDictionary<string, int> ports)
    {
        foreach (var c in analysis.Connections)
        {
            c.Transports.Clear();
            foreach (var env in analysis.Environments)
            {
                try
                {
                    var host = J2Variables.Expand(c.Host, c.Values[env], NormalizeEndpointValue);
                    var port = J2Variables.Expand(c.Port, c.Values[env], NormalizeEndpointValue);
                    var results = new List<string>();
                    var transports = new List<NetworkTransport>();
                    if (TryDatabase(host, out var dbHost, out var dbPort)) { host = dbHost; if (port.Length == 0) port = dbPort; }
                    if (host.Contains('=')) throw new InvalidDataException("формат строки подключения не распознан");
                    foreach (var address in host.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        var endpoint = ParseAddress(address) ?? throw new InvalidDataException("адрес не распознан");
                        var selectedPort = port.Length > 0 ? port : endpoint.Port;
                        var inferred = false;
                        if (selectedPort.Length == 0)
                        {
                            var protocol = SchemeProtocol(endpoint.Scheme) ?? c.Choice.Protocol;
                            if (ports.TryGetValue(protocol, out var fallback)) { selectedPort = fallback.ToString(); inferred = true; }
                        }
                        if (selectedPort.Length > 0)
                        {
                            if (!TryPort(selectedPort, out var numeric)) throw new InvalidDataException("некорректный порт");
                            transports.Add(new NetworkTransport(numeric, KnownProtocol(c.Choice.Protocol) ?? SchemeProtocol(endpoint.Scheme)));
                        }
                        results.Add(endpoint.Host + (selectedPort.Length == 0 ? " (порт не указан)" : ":" + selectedPort) + (inferred ? " (порт по умолчанию)" : ""));
                    }
                    c.Endpoints[env] = results.Count == 0 ? "значение не найдено" : string.Join("\n", results.Distinct());
                    c.Transports.UnionWith(transports);
                }
                catch (InvalidDataException e) { c.Endpoints[env] = e.Message; }
            }
        }
    }

    internal static NetworkAddress? ParseAddress(string address)
    {
        var uriText = address.Contains("://") ? address : "tcp://" + address;
        if (!Uri.TryCreate(uriText, UriKind.Absolute, out var uri) || uri.Host.Length == 0 || uri.Host.Contains('{')) return null;
        var hostname = uri.Host.Trim('[', ']');
        if (hostname.Contains(':')) hostname = "[" + hostname + "]";
        var explicitPort = Regex.IsMatch(uri.OriginalString, @"^[^:]+://[^/?#]*:\d+(?:[/?#]|$)");
        return new NetworkAddress(hostname, explicitPort ? uri.Port.ToString() : "", uri.Scheme);
    }

    internal static bool TryPort(string port, out int numeric) =>
        int.TryParse(port, out numeric) && numeric is >= 1 and <= 65535;

    private static string? KnownProtocol(string protocol) =>
        string.IsNullOrWhiteSpace(protocol) || protocol.Equals("Unknown", StringComparison.OrdinalIgnoreCase) ? null : protocol;

    private static IEnumerable<NetworkTransport> ArrowTransports(NetworkConnection connection)
    {
        if (connection.Transports.Count > 0) return connection.Transports;
        // A fixed port still applies when the host value is missing.
        return TryPort(connection.Port, out var port) ? [new NetworkTransport(port, KnownProtocol(connection.Choice.Protocol))] : [];
    }

    public static string ExtractTableTemplate(string html) => NetworkTableTransformer.Find(html).Macro.OuterHtml;

    public static NetworkTableResult RenderTable(string templateHtml, NetworkAnalysis analysis)
    {
        var template = NetworkTableTransformer.Find(templateHtml);
        var targets = Targets(analysis);
        var blockParents = ResolveBlockParents(analysis, VisibleVertices(analysis, targets));
        NetworkTableTransformer.Fill(template, analysis.Environments, targets, blockParents, SharedContainers(analysis));
        return new(template.Macro.OuterHtml, HtmlEntity.DeEntitize(template.Macro.InnerText));
    }

    private static DiagramVertex ServiceVertex(NetworkServiceInput service) =>
        new(service.Id, service.Label, DiagramColumn.Services, service.Block?.Trim() ?? "", service.ParentBlock?.Trim() ?? "");

    private static DiagramVertex ExternalVertex(NetworkConnection connection)
    {
        var options = connection.ExternalTarget;
        var label = string.IsNullOrWhiteSpace(options.Label) ? connection.SuggestedTargetLabel : options.Label;
        var column = connection.HostKeys.Any(key => key.StartsWith("SVC_", StringComparison.Ordinal))
            ? DiagramColumn.SvcDependencies : DiagramColumn.OtherDependencies;
        return new(connection.TargetId, label, column, options.Block?.Trim() ?? "", options.ParentBlock?.Trim() ?? "");
    }

    private static NetworkTarget[] Targets(NetworkAnalysis analysis)
    {
        var services = analysis.Services.ToDictionary(service => service.Id);
        var groups = analysis.Connections.Where(c => c.Choice.Include && c.HasAddressVariable)
            .OrderBy(c => c.Key, StringComparer.Ordinal)
            .GroupBy(c => c.TargetId);

        return groups.Select(group =>
        {
            var first = group.First();
            if (first.Choice.TargetService.Length != 0)
            {
                if (!services.TryGetValue(group.Key, out var service))
                    throw new InvalidDataException("Выбран целевой сервис, отсутствующий на карте.");
                return new NetworkTarget(ServiceVertex(service), group.ToArray());
            }

            return new NetworkTarget(ExternalVertex(first), group.ToArray());
        }).OrderBy(target => target.Vertex.Label, StringComparer.Ordinal)
            .ThenBy(target => target.Vertex.Id, StringComparer.Ordinal).ToArray();
    }

    private static HashSet<string> SharedContainers(NetworkAnalysis analysis)
    {
        var comparer = StringComparer.OrdinalIgnoreCase;
        // Count configured nodes before filtering so the inclusion checkboxes keep row names stable.
        var dependencies = analysis.Connections.Where(c => c.Choice.TargetService.Length == 0 && c.HasAddressVariable)
            .OrderBy(c => c.Key, StringComparer.Ordinal).DistinctBy(c => c.TargetId).Select(ExternalVertex);
        var vertices = analysis.Services.Select(ServiceVertex).Concat(dependencies).ToArray();
        var shared = vertices.GroupBy(vertex => vertex.Container, comparer)
            .Where(group => group.Skip(1).Any()).Select(group => group.Key).ToHashSet(comparer);
        shared.UnionWith(vertices.Where(vertex => vertex.Block.Length > 0 && vertex.ParentBlock.Length > 0)
            .Select(vertex => vertex.ParentBlock));
        return shared;
    }

    private static DiagramVertex[] VisibleVertices(NetworkAnalysis analysis, NetworkTarget[] targets) =>
        analysis.Services.Select(ServiceVertex)
            .Concat(targets.Where(target => target.Vertex.Column != DiagramColumn.Services).Select(target => target.Vertex)).ToArray();

    public static string CreateDiagram(NetworkAnalysis analysis)
    {
        var display = analysis.Display ?? new NetworkDisplayOptions();
        var targets = Targets(analysis);
        var vertices = VisibleVertices(analysis, targets);
        var blockParents = ResolveBlockParents(analysis, vertices);
        var links = targets.SelectMany(target => target.Connections.GroupBy(c => c.Service.Id).Select(calls =>
        {
            var paths = calls.Select(c => c.Path).Distinct().Order(StringComparer.Ordinal);
            var transports = calls.SelectMany(ArrowTransports)
                .Select(transport => display.HideProtocolsOnArrows ? transport with { Protocol = null } : transport)
                .DistinctBy(transport => (transport.Port, transport.Protocol?.ToUpperInvariant()))
                .OrderBy(transport => transport.Port).ThenBy(transport => transport.Protocol, StringComparer.Ordinal);
            var caption = string.Join(", ", transports.Select(transport => transport.Protocol is null
                ? transport.Port.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : FormattableString.Invariant($"{transport.Port} ({transport.Protocol})")));
            var keys = string.Join("\n", paths);
            return new DiagramLink(Id(calls.Key + ":" + target.Vertex.Id), calls.Key, target.Vertex.Id,
                display.ShowConfigurationKeys ? keys : "", keys, caption);
        }));
        return NetworkDiagramLayout.Render(vertices, links, blockParents, display.ColorArrowsByService);
    }

    private static Dictionary<string, string> ResolveBlockParents(NetworkAnalysis analysis, DiagramVertex[] vertices)
    {
        var comparer = StringComparer.OrdinalIgnoreCase;
        // Excluding an arrow must not change a block's parent while other nodes still use that block.
        var declarations = vertices.Select(v => (v.Block, v.ParentBlock))
            .Concat(analysis.Connections.Where(c => c.Choice.TargetService.Length == 0 && c.HasAddressVariable)
                .Select(c => (Block: c.ExternalTarget.Block?.Trim() ?? "", ParentBlock: c.ExternalTarget.ParentBlock?.Trim() ?? "")))
            .Where(entry => entry.Block.Length > 0 && entry.ParentBlock.Length > 0)
            .ToLookup(entry => entry.Block, entry => entry.ParentBlock, comparer);

        string? ParentOf(string block)
        {
            var candidates = declarations[block].Order(StringComparer.Ordinal).Distinct(comparer).ToArray();
            if (candidates.Length > 1)
                throw new InvalidDataException($"У блока «{block}» указаны разные родители. Выберите одного из них ({string.Join(", ", candidates)}).");
            return candidates.SingleOrDefault();
        }

        var parents = new Dictionary<string, string>(comparer);
        foreach (var block in vertices.Select(v => v.Container).Where(name => name.Length > 0)
                     .Order(StringComparer.Ordinal).Distinct(comparer))
        {
            if (ParentOf(block) is not { } parent) continue;
            if (comparer.Equals(block, parent))
                throw new InvalidDataException($"Блок «{block}» указан своим собственным родителем.");
            if (ParentOf(parent) is { } ancestor)
                throw new InvalidDataException($"Обнаружен цикл или превышены два уровня вложенности в цепочке «{block}» → «{parent}» → «{ancestor}».");
            parents.Add(block, parent);
        }
        return parents;
    }
}

internal sealed record NetworkTarget(DiagramVertex Vertex, NetworkConnection[] Connections);
