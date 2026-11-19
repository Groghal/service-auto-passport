using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using HtmlAgilityPack;
using ServiceAutoPassport.Core;

namespace ServiceAutoPassport.Tests;

public sealed class NetworkTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "network-tests-" + Guid.NewGuid().ToString("N"));
    private string Write(string path, string text)
    {
        var file = Path.Combine(_root, path); Directory.CreateDirectory(Path.GetDirectoryName(file)!); File.WriteAllText(file, text); return file;
    }
    private static readonly string Template = "<div>Saved layout</div>" + Regex.Replace(
        NetworkPageTemplates.BasicHtml.Replace("export-csv=false", "export-csv=true"),
        """<div data-network-diagram-placeholder="true">[\s\S]*?</div>""",
        "<img data-macro-name='drawio' data-macro-id='saved' />");

    private static HtmlNode Transformer(string html)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        return doc.DocumentNode.Descendants("table").Single(t => t.GetAttributeValue("data-macro-name", "") == "table-joiner");
    }

    private static HtmlNode[] DataRows(HtmlNode macro) => macro.Descendants("table").Single()
        .Descendants("tr").Where(r => r.Elements("td").Any()).ToArray();

    private const string Current = "<img data-macro-name='drawio' data-macro-id='current' data-macro-parameters='attachment=own' />";
    [Fact]
    public void CombinedServicesShareParametersAndBindSelectedService()
    {
        Write("params/application.yaml", "DB_HOST: base-db\nDB_PORT: 5432\nB_HOST: https://b.example\n");
        Write("params/application-test.yaml", "DB_HOST: test-db\n");
        Write("params/application-prod.yaml", "DB_HOST: prod-db\n");
        var a = Write("A/appsettings.deploy.j2", "{\"GrpcClients\": {\"B\": {\"Host\": \"{{ B_HOST }}\"}}}");
        var b = Write("B/appsettings.deploy.j2", "{}");
        foreach (var folder in new[] { "A", "B" })
            Write(folder + "/appsettings.secrets.j2", "ConnectionStrings__Main: 'Host={{ DB_HOST }};Port={{ DB_PORT }};Password=SHOULD_NOT_LEAK'\n");
        Write("params/b/b-prod.yml", "DB_HOST: override-db\n");
        var settings = new NetworkMapSettings { Environments = new() { "test", "prod" }, Services = new()
        {
            new() { J2File = a, Label = "A", YamlService = "a" },
            new() { J2File = b, Label = "B", YamlService = "b", AddressVariables = "B_HOST" }
        }};
        var analysis = NetworkMapGenerator.Analyze(Path.Combine(_root, "params"), settings);
        Assert.Equal(3, analysis.Connections.Count);
        Assert.Equal("test-db:5432", analysis.Connections.Single(c => c.Path == "ConnectionStrings.Main" && c.Service.Label == "A").Endpoints["test"]);
        var result = NetworkMapGenerator.Render(Template, Current, analysis);
        Assert.Contains("current", result.HtmlFragment); Assert.DoesNotContain("'saved'", result.HtmlFragment);
        Assert.Contains("Saved layout", result.HtmlFragment);
        Assert.Contains("A — prod-db:5432", result.HtmlFragment);
        Assert.Contains("B — override-db:5432", result.HtmlFragment);
        Assert.DoesNotContain("SHOULD_NOT_LEAK", result.HtmlFragment + result.DrawioXml + string.Join("", analysis.Diagnostics));
        var xml = XDocument.Parse(result.DrawioXml);
        Assert.Equal(3, xml.Descendants("mxCell").Count(c => (string?)c.Attribute("kind") == "node"));
        Assert.Equal(3, xml.Descendants("mxCell").Count(c => (string?)c.Attribute("edge") == "1"));
        Assert.Equal(2, xml.Descendants("mxCell").Count(c => (string?)c.Attribute("edge") == "1" &&
            (string?)c.Attribute("value") == "5432 (PostgreSQL)" && (string?)c.Attribute("sourcePath") == "ConnectionStrings.Main"));
        Assert.Contains(xml.Descendants("mxCell"), c => (string?)c.Attribute("source") == settings.Services[0].Id && (string?)c.Attribute("target") == settings.Services[1].Id);
        Assert.Equal(2, DataRows(Transformer(result.HtmlFragment)).Length);
        Assert.Contains("export-csv=true", Transformer(result.HtmlFragment).GetAttributeValue("data-macro-parameters", ""));
        var stable = NetworkMapGenerator.Render(Template, Current, analysis);
        Assert.Equal(result.DrawioXml, stable.DrawioXml);
    }
    [Fact]
    public void DefaultsExpansionIpv6MissingValuesAndCredentials()
    {
        Write("params/application.yml", "HOST: '{{ BASE }}'\nBASE: '[2001:db8::1]'\nLOOP_HOST: '{{ LOOP_HOST }}'\nEXTERNAL_URL: 'https://user:password@example.test/private?q=secret'\n");
        var main = Write("A/appsettings.deploy.j2", """
            {"GrpcClients":{"Remote":{"Host":"{{ HOST }}"}},
             "External":{"EndpointUrl":"{{ EXTERNAL_URL }}"},
             "Broken":{"Host":"{{ MISSING_HOST }}"},"Cyclic":{"Host":"{{ LOOP_HOST }}"},
             "Kestrel":{"Endpoints":{"Http":{"Url":"http://localhost:5000"}}}}
            """);
        var settings = new NetworkMapSettings { Environments = new() { "prod" }, Services = new() { new() { J2File = main, Label = "A", YamlService = "a" } } };
        var analysis = NetworkMapGenerator.Analyze(Path.Combine(_root, "params"), settings);
        Assert.Equal(4, analysis.Connections.Count);
        Assert.Contains(analysis.Connections, c => c.Endpoints["prod"] == "[2001:db8::1]:80 (порт по умолчанию)");
        Assert.Contains(analysis.Connections, c => c.Endpoints["prod"] == "example.test:443 (порт по умолчанию)");
        Assert.Contains(analysis.Connections, c => c.Endpoints["prod"].Contains("не найдено: MISSING_HOST"));
        Assert.Contains(analysis.Connections, c => c.Endpoints["prod"].Contains("циклическая ссылка"));
        var result = NetworkMapGenerator.Render(Template, Current, analysis);
        Assert.DoesNotContain("password", result.HtmlFragment + result.DrawioXml);
        Assert.DoesNotContain("private", result.HtmlFragment);
        Assert.DoesNotContain("q=secret", result.HtmlFragment);
    }
    [Fact]
    public void UrlOrHostVariableSuffixQualifiesConnectionsForBothExports()
    {
        Write("params/application.yml", "PUBLIC_HoSt: api.example.test\nAPI_PORT: 8443\n");
        var main = Write("A/appsettings.deploy.j2", """
            {
                "HelpUrl": "https://docs.example.test/help",
                "SupportUrl": "https://support.example.test/{{ HELP_URL }}?q={{ SEARCH_URL }}",
                "TicketUrl": "https://tickets.example.test/{{ HELP_URL }}",
                "QueryOnlyUrl": "https://search.example.test/docs?q={{ SEARCH_URL }}",
                "UnrelatedHost": "{{ REGION }}",
                "UnrelatedUrl": "https://region.example.test/{{ REGION }}",
                "Metrics": {"Url": "http://fixed.example.test:{{ INLINE_PORT }}", "Port": "{{ OVERRIDE_PORT }}"},
                "ConnectionStrings": {"Audit": "Host=db.example.test;Password={{ DB_PASSWORD }}"},
                "Api": {"Url": "https://{{ PUBLIC_HoSt }}:{{ API_PORT }}/v1"}
            }
            """);
        var settings = new NetworkMapSettings { Environments = ["prod"], Services = [new() { J2File = main, Label = "A" }] };
        var analysis = NetworkMapGenerator.Analyze(Path.Combine(_root, "params"), settings);
        var expectedPaths = new[] { "Api.Url", "SupportUrl", "TicketUrl" };
        Assert.Equal(expectedPaths, analysis.Connections.Select(connection => connection.Path).Order(StringComparer.Ordinal));
        Assert.Equal(3, settings.Targets.Count);
        Assert.Equal(3, settings.Choices.Count);
        var support = analysis.Connections.Single(connection => connection.Path == "SupportUrl");
        Assert.Equal("HELP_URL", support.Parameters);
        Assert.Equal("HELP_URL — A · SupportUrl", NetworkParameterCatalog.DisplayParameter(support.TargetKey, support.ExternalTarget));

        var result = NetworkMapGenerator.Render(NetworkPageTemplates.BasicHtml, null, analysis);
        var rows = DataRows(Transformer(result.HtmlFragment));
        Assert.Equal(3, rows.Length);
        Assert.Contains(rows, row => row.InnerText.Contains("api.example.test:8443", StringComparison.Ordinal));
        Assert.Contains(rows, row => row.Elements("td").ElementAt(1).InnerText == "support.example.test:443 (порт по умолчанию)");
        Assert.Contains(rows, row => row.Elements("td").ElementAt(1).InnerText == "tickets.example.test:443 (порт по умолчанию)");
        Assert.DoesNotContain("SEARCH", result.HtmlFragment);
        var links = XDocument.Parse(result.DrawioXml).Descendants("mxCell")
            .Where(cell => (string?)cell.Attribute("edge") == "1");
        Assert.Equal(expectedPaths, links.Select(cell => (string?)cell.Attribute("sourcePath")).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void ArrowLabelsUseResolvedPortsAndKnownProtocols()
    {
        Write("params/application.yml", "API_URL: 'https://api.example.test:443'\n");
        Write("params/application-prod.yml", "API_URL: 'https://api.example.test'\n");
        var main = Write("A/appsettings.deploy.j2", """{"Api":{"Url":"{{ API_URL }}"}}""");
        var settings = new NetworkMapSettings { Environments = ["test", "prod"], Services = [new() { J2File = main }] };
        settings.Ports["HTTPS"] = 8443;
        var analysis = NetworkMapGenerator.Analyze(Path.Combine(_root, "params"), settings);
        var result = NetworkMapGenerator.Render(NetworkPageTemplates.BasicHtml, null, analysis);
        var arrow = Assert.Single(XDocument.Parse(result.DrawioXml).Descendants("mxCell")
            .Where(c => (string?)c.Attribute("edge") == "1"));
        Assert.Equal("443 (HTTPS), 8443 (HTTPS)", (string?)arrow.Attribute("value"));
        Assert.Contains("api.example.test:443", result.HtmlFragment);
        Assert.Contains("api.example.test:8443 (порт по умолчанию)", result.HtmlFragment);
    }

    [Fact]
    public void DifferentPortsShareOneRowAndOneArrowButDifferentKeysRemainSeparate()
    {
        Write("params/application.yml", "HOST: server\nOTHER_HOST: server\nPORT_A: 9001\nPORT_B: 9002\n");
        var main = Write("A/appsettings.deploy.j2", "{\"One\":{\"Host\":\"http://{{ HOST }}:{{ PORT_A }}\"},\"Two\":{\"Host\":\"{{ HOST }}\",\"Port\":{{ PORT_B }}},\"Three\":{\"Host\":\"{{ OTHER_HOST }}\"}}");
        var settings = new NetworkMapSettings { Environments = new() { "prod" }, Services = new() { new() { J2File = main, YamlService = "a", Label = "A" } } };
        var analysis = NetworkMapGenerator.Analyze(Path.Combine(_root, "params"), settings);
        Assert.Equal(2, analysis.Connections.Select(c => c.TargetKey).Distinct().Count());
        Assert.Contains(analysis.Connections, c => c.Endpoints["prod"] == "server:9001");
        Assert.Contains(analysis.Connections, c => c.Endpoints["prod"] == "server:9002");
        analysis.Connections[2].Choice.Include = false;
        var result = NetworkMapGenerator.Render(Template, Current, analysis);
        var xml = XDocument.Parse(result.DrawioXml);
        Assert.Equal(2, xml.Descendants("mxCell").Count(c => (string?)c.Attribute("kind") == "node"));
        var arrow = Assert.Single(xml.Descendants("mxCell").Where(c => (string?)c.Attribute("edge") == "1"));
        Assert.Equal("9001 (HTTP), 9002", (string?)arrow.Attribute("value"));
        var row = Assert.Single(DataRows(Transformer(result.HtmlFragment)));
        Assert.Contains("server:9001", row.InnerText);
        Assert.Contains("server:9002", row.InnerText);
    }
    [Fact]
    public void IncludeSelectionControlsTableAndDiagramIncludingSharedAndEmptyResults()
    {
        Write("params/application.yml", "DB_HOST: database\nDB_PORT: 5432\nCACHE_HOST: cache\n");
        var mainA = Write("A/appsettings.deploy.j2", "{\"Database\":{\"Host\":\"{{ DB_HOST }}\",\"Port\":\"{{ DB_PORT }}\"},\"Cache\":{\"Host\":\"{{ CACHE_HOST }}\"}}");
        var mainB = Write("B/appsettings.deploy.j2", "{\"Database\":{\"Host\":\"{{ DB_HOST }}\",\"Port\":\"{{ DB_PORT }}\"}}");
        var settings = new NetworkMapSettings { Environments = new() { "prod" }, Services = new()
        {
            new() { J2File = mainA, Label = "A" }, new() { J2File = mainB, Label = "B" }
        }};
        var analysis = NetworkMapGenerator.Analyze(Path.Combine(_root, "params"), settings);
        var dbA = analysis.Connections.Single(c => c.Service.Label == "A" && c.Path == "Database.Host");
        var dbB = analysis.Connections.Single(c => c.Service.Label == "B");
        var cache = analysis.Connections.Single(c => c.Path == "Cache.Host");
        void Check(int dependencies, int edges, int rows, string[] included, string[] excluded)
        {
            var result = NetworkMapGenerator.Render(NetworkPageTemplates.BasicHtml, null, analysis);
            Assert.Equal(result.DrawioXml, NetworkMapGenerator.CreateDiagram(analysis));
            var xml = XDocument.Parse(result.DrawioXml);
            Assert.Equal(2 + dependencies, xml.Descendants("mxCell").Count(c => (string?)c.Attribute("kind") == "node"));
            Assert.Equal(edges, xml.Descendants("mxCell").Count(c => (string?)c.Attribute("edge") == "1"));
            var macro = Transformer(result.HtmlFragment);
            var table = macro.Descendants("table").Single();
            Assert.Equal(rows, DataRows(macro).Length);
            foreach (var text in included) Assert.Contains(text, table.InnerText);
            foreach (var text in excluded) Assert.DoesNotContain(text, table.InnerText);
            foreach (var connection in analysis.Connections)
                Assert.Equal(connection.Choice.Include, xml.Descendants("mxCell").Any(c =>
                    (string?)c.Attribute("edge") == "1" && (string?)c.Attribute("source") == connection.Service.Id &&
                    (string?)c.Attribute("sourcePath") == connection.Path));
        }
        Check(2, 3, 2, new[] { "DB_HOST", "CACHE_HOST" }, Array.Empty<string>());
        dbA.Choice.Include = false;
        cache.Choice.Include = false;
        Check(1, 1, 1, new[] { "DB_HOST", "B — Database.Host" }, new[] { "CACHE_HOST", "A — Database.Host" });
        dbB.Choice.Include = false;
        Check(0, 0, 0, Array.Empty<string>(), new[] { "DB_HOST", "CACHE_HOST" });
        cache.Choice.Include = true;
        Check(1, 1, 1, new[] { "CACHE_HOST" }, new[] { "DB_HOST" });
        // Re-analysis keeps the same selection stored in settings.
        var reloaded = NetworkMapGenerator.Analyze(Path.Combine(_root, "params"), settings);
        Assert.Equal(new[] { cache.Key }, reloaded.Connections.Where(c => c.Choice.Include).Select(c => c.Key));
    }
    [Fact]
    public void ErrorsAreActionableAndDoNotExposeYamlSecretValues()
    {
        Write("params/application.yml", "");
        var main = Write("A/appsettings.deploy.j2", "{% if something %}");
        var settings = new NetworkMapSettings { Services = new() { new() { J2File = main, YamlService = "a" } } };
        Assert.Throws<InvalidDataException>(() => NetworkMapGenerator.Analyze(Path.Combine(_root, "params"), settings));
        Assert.Throws<InvalidDataException>(() => NetworkMapGenerator.ValidateTemplate("<p>Invalid</p>"));
        Assert.Throws<InvalidDataException>(() => NetworkMapGenerator.ValidateTemplate(Template + "<img data-macro-name='drawio' />"));
        Assert.Throws<InvalidDataException>(() => NetworkMapGenerator.ValidateTemplate(Template + Transformer(Template).OuterHtml));
        Write("A/appsettings.deploy.j2", "{\"Host\": \"http://host:80\"}");
        var analysis = NetworkMapGenerator.Analyze(Path.Combine(_root, "params"), settings);
        var withoutMacro = NetworkMapGenerator.Render(Template, "<p>No macro</p>", analysis);
        Assert.Contains("data-network-diagram-placeholder", withoutMacro.HtmlFragment);
        Assert.DoesNotContain("data-macro-id", withoutMacro.HtmlFragment);
    }

    [Fact]
    public void DatabaseSettingsFragmentsAndMissingCredentialsDoNotHideConnections()
    {
        Write("params/application.yml", "DB_HOST: database\nDB_PORT: 5432\nCONNECTION_URL: 'Host=database;Port=5432;Password={{ MISSING_PASSWORD }}'\n");
        var main = Write("A/appsettings.deploy.j2", "{\"ConnectionStrings\":{\"Whole\":\"{{ CONNECTION_URL }}\"},\"Rmq\":{\"VirtualHost\":\"vhost\"},\"PageUrl\":\"/local/path\"}");
        Write("A/secrets.j2", "ConnectionStrings__Main: Host={{ DB_HOST }};Port={{ DB_PORT }};Password={{ MISSING_PASSWORD }};{{ EXTRA_SETTINGS }}\n");
        var settings = new NetworkMapSettings { Environments = new() { "prod" }, Services = new() { new() { J2File = main, Label = "A", YamlService = "a" } } };
        var analysis = NetworkMapGenerator.Analyze(Path.Combine(_root, "params"), settings);
        Assert.Equal(2, analysis.Connections.Count);
        Assert.All(analysis.Connections, c => Assert.Equal("database:5432", c.Endpoints["prod"]));
        Assert.Empty(analysis.Diagnostics);
    }

    [Fact]
    public void EveryEnvironmentUsesIndependentFourLayerPrecedence()
    {
        Write("params/application.yml", "HOST: base\nPORT: 80\n");
        Write("params/a/a.yml", "HOST: service-base\n");
        Write("params/application-prod.yml", "HOST: prod-global\n");
        Write("params/a/a-prod.yml", "HOST: prod-service\n");
        var prod = YamlConfiguration.LoadForEnvironment(Path.Combine(_root, "params"), "a", "prod");
        var test = YamlConfiguration.LoadForEnvironment(Path.Combine(_root, "params"), "a", "test");
        Assert.Equal("prod-service", prod["HOST"]);
        Assert.Equal("service-base", test["HOST"]);
        Assert.Equal("80", test["PORT"]);
        Assert.Throws<InvalidDataException>(() => YamlConfiguration.LoadForEnvironment(Path.Combine(_root, "params"), "../bad", "prod"));
    }

    [Fact]
    public void BasicTemplateGeneratesPageAndDiagramWithNoClipboardOrSavedTemplate()
    {
        Write("params/application.yml", "SVC_HOST: https://service.example\n");
        var main = Write("A/appsettings.deploy.j2", "{\"Client\":{\"Host\":\"{{ SVC_HOST }}\"}}");
        var settings = new NetworkMapSettings { Environments = new() { "prod", "demo", "dev-2" }, Services = new()
        {
            new() { J2File = main, Label = "A", YamlService = "a" }
        }};
        Assert.Equal("", settings.TemplateHtml);
        var analysis = NetworkMapGenerator.Analyze(Path.Combine(_root, "params"), settings);
        NetworkMapGenerator.ValidateTemplate(NetworkPageTemplates.BasicHtml);
        var result = NetworkMapGenerator.Render(NetworkPageTemplates.BasicHtml, null, analysis);
        Assert.Contains("service.example:443", result.HtmlFragment);
        var macro = Transformer(CfHtml.ExtractFragment(CfHtml.Build(result.HtmlFragment)));
        Assert.Equal("RICH_TEXT", macro.GetAttributeValue("data-macro-body-type", ""));
        var parameters = WebUtility.HtmlDecode(macro.GetAttributeValue("data-macro-parameters", ""));
        Assert.Contains("show-source=false", parameters);
        Assert.Contains("sql=SELECT T1.'Элемент', T1.'prod', T1.'demo', T1.'dev-2' FROM T1|", parameters);
        Assert.Contains(@"worklog=365\|5\|8\|y w d h m\|y w d h m", parameters);
        Assert.Equal(new[] { "Элемент", "prod", "demo", "dev-2", "Параметры", "Сервисы", "Подключение", "Адресный параметр", "Блок", "Блок-родитель", "Подпись узла" },
            macro.Descendants("th").Select(th => th.InnerText));
        Assert.Contains("data-network-diagram-placeholder", result.HtmlFragment);
        Assert.Equal(result.DrawioXml, NetworkMapGenerator.CreateDiagram(analysis));
        Assert.Equal("mxGraphModel", XDocument.Parse(result.DrawioXml).Root!.Name.LocalName);
        NetworkMapGenerator.ValidateTemplate(result.HtmlFragment);
        var regenerated = NetworkMapGenerator.Render(result.HtmlFragment, null, analysis);
        Assert.Equal(result.HtmlFragment, regenerated.HtmlFragment);

        var existing = NetworkMapGenerator.Render(NetworkPageTemplates.BasicHtml, Current, analysis);
        Assert.Contains("attachment=own", existing.HtmlFragment);
        Assert.DoesNotContain("data-network-diagram-placeholder", existing.HtmlFragment);
        var savedWithoutClipboard = NetworkMapGenerator.Render(Template, null, analysis);
        Assert.Contains("Saved layout", savedWithoutClipboard.HtmlFragment);
        Assert.Contains("data-network-diagram-placeholder", savedWithoutClipboard.HtmlFragment);
        Assert.DoesNotContain("data-macro-id", savedWithoutClipboard.HtmlFragment);
    }
    [Fact]
    public void SingleNestedIntegrationUsesBlockNameInTableAndRoundTripsNodeLabel()
    {
        Write("params/application.yml", "KAFKA_HOST: kafka.example\n");
        const string appsettings = """{"Kafka":{"Host":"{{ KAFKA_HOST }}","Port":9092}}""";
        var a = Write("A/appsettings.deploy.j2", appsettings);
        var b = Write("B/appsettings.deploy.j2", appsettings);
        const string label = "Топики Kafka & события";
        var settings = new NetworkMapSettings
        {
            Environments = ["prod"],
            Services = [new() { J2File = a, Label = "A" }, new() { J2File = b, Label = "B" }],
            Targets = new() { ["parameter:KAFKA_HOST"] = new() { Label = label, Block = "Kafka", ParentBlock = "k8s" } }
        };
        var analysis = NetworkMapGenerator.Analyze(Path.Combine(_root, "params"), settings);
        var result = NetworkMapGenerator.Render(NetworkPageTemplates.BasicHtml, null, analysis);
        var macro = Transformer(result.HtmlFragment);
        var row = Assert.Single(DataRows(macro)).Elements("td").ToArray();
        Assert.Equal("Kafka", row[0].InnerText);
        Assert.Equal("kafka.example:9092", row[1].InnerText);
        Assert.Equal(label, WebUtility.HtmlDecode(row[^1].InnerText));
        Assert.Contains("sql=SELECT T1.'Элемент', T1.'prod' FROM T1|",
            WebUtility.HtmlDecode(macro.GetAttributeValue("data-macro-parameters", "")));

        var cells = XDocument.Parse(result.DrawioXml).Descendants("mxCell").ToArray();
        var container = Assert.Single(cells.Where(c => (string?)c.Attribute("value") == "Kafka"));
        Assert.Equal("container", (string?)container.Attribute("kind"));
        var node = Assert.Single(cells.Where(c => WebUtility.HtmlDecode((string?)c.Attribute("value")) == label));
        Assert.Equal((string?)container.Attribute("id"), (string?)node.Attribute("parent"));
        Assert.Equal(2, cells.Count(c => (string?)c.Attribute("target") == (string?)container.Attribute("id")));

        var imported = NetworkTemplatePrefill.Read(CfHtml.Build(result.HtmlFragment), [], new Dictionary<string, NetworkParameterEdit>());
        Assert.Equal(new NetworkParameterEdit(label, "Kafka") { ParentBlock = "k8s" }, imported.Edits["parameter:KAFKA_HOST"]);
    }

    [Fact]
    public void HidingOtherIntegrationsRetargetsArrowsWithoutRenamingTableRows()
    {
        var main = Write("A/appsettings.deploy.j2", """
            {"Db":{"Host":"{{ DB_HOST }}"},"Replica":{"Host":"{{ REPLICA_HOST }}"},
             "Child":{"Host":"{{ CHILD_HOST }}"},"Outer":{"Host":"{{ OUTER_HOST }}"},
             "Managed":{"Host":"{{ MANAGED_HOST }}"},"Default":{"Host":"{{ DEFAULT_HOST }}"},
             "Standalone":{"Host":"{{ STANDALONE_HOST }}"}}
            """);
        var settings = new NetworkMapSettings
        {
            Environments = ["prod"],
            Services = [new() { J2File = main, Label = "Service", Block = "Managed" }],
            Targets = new()
            {
                ["parameter:DB_HOST"] = new() { Label = "Primary DB", Block = "Storage" },
                ["parameter:REPLICA_HOST"] = new() { Label = "Replica DB", Block = " storage " },
                ["parameter:CHILD_HOST"] = new() { Label = "Child integration", Block = "Child", ParentBlock = " outer " },
                ["parameter:OUTER_HOST"] = new() { Label = "Outer integration", Block = "Outer" },
                ["parameter:MANAGED_HOST"] = new() { Label = "Managed integration", Block = "Managed" },
                ["parameter:DEFAULT_HOST"] = new() { Label = "Default integration", Block = " K8S " },
                ["parameter:STANDALONE_HOST"] = new() { Label = "Standalone integration", Block = "" }
            }
        };
        var analysis = NetworkMapGenerator.Analyze(_root, settings);
        static string[] Names(NetworkMapResult page) => DataRows(Transformer(page.HtmlFragment))
            .Select(row => row.Elements("td").First().InnerText).Order(StringComparer.Ordinal).ToArray();
        static string Target(NetworkMapResult page, string path)
        {
            var cells = XDocument.Parse(page.DrawioXml).Descendants("mxCell").ToArray();
            var edge = Assert.Single(cells.Where(c => (string?)c.Attribute("sourcePath") == path));
            return (string)cells.Single(c => (string?)c.Attribute("id") == (string?)edge.Attribute("target")).Attribute("value")!;
        }

        var before = NetworkMapGenerator.Render(NetworkPageTemplates.BasicHtml, null, analysis);
        Assert.Equal(new[] { "Child", "Default integration", "Managed integration", "Outer integration", "Primary DB", "Replica DB", "Standalone integration" }, Names(before));
        Assert.Equal("Primary DB", Target(before, "Db.Host"));
        Assert.Equal("Outer integration", Target(before, "Outer.Host"));
        Assert.Equal("Managed integration", Target(before, "Managed.Host"));
        Assert.Equal("Standalone integration", Target(before, "Standalone.Host"));
        analysis.Connections.Single(c => c.Path == "Replica.Host").Choice.Include = false;
        analysis.Connections.Single(c => c.Path == "Child.Host").Choice.Include = false;
        var after = NetworkMapGenerator.Render(NetworkPageTemplates.BasicHtml, null, analysis);
        Assert.Equal(new[] { "Default integration", "Managed integration", "Outer integration", "Primary DB", "Standalone integration" }, Names(after));
        Assert.Equal("Storage", Target(after, "Db.Host"));
        Assert.Equal("Outer", Target(after, "Outer.Host"));
    }

    [Fact]
    public void SharedNameAndExplicitEmptyBlockSurviveSettingsReload()
    {
        Write("params/application.yml", "DB_HOST: database\n");
        var a = Write("A/appsettings.deploy.j2", """{"Db":{"Host":"{{ DB_HOST }}"}}""");
        var b = Write("B/appsettings.deploy.j2", """{"ReadReplica":{"Host":"{{ DB_HOST }}"}}""");
        var root = Path.Combine(_root, "params");
        var settings = new NetworkMapSettings
        {
            Environments = new() { "prod" },
            Services = new() { new() { J2File = a, Label = "A" }, new() { J2File = b, Label = "B", Block = "" } }
        };
        var initial = NetworkMapGenerator.Analyze(root, settings);
        var options = initial.Connections[0].ExternalTarget;
        Assert.Same(options, initial.Connections[1].ExternalTarget);
        Assert.Equal("Db", options.Label);
        Assert.Equal("k8s", options.Block);
        const string title = "База <A> & B";
        var edits = NetworkParameterCatalog.ParseExcel($"DB_HOST\t{title}\t\nNEW_HOST\tНовый узел\tk8s", settings.Targets);
        string? snapshot = null;
        NetworkParameterCatalog.SaveEdits(settings, edits, () => snapshot = JsonSerializer.Serialize(settings));
        Assert.Same(options, settings.Targets[initial.Connections[0].TargetKey]);
        Assert.Equal(title, options.Label);
        Assert.Equal("", options.Block);
        var live = NetworkMapGenerator.Render(NetworkPageTemplates.BasicHtml, null, initial);
        Assert.Equal(title, WebUtility.HtmlDecode(Assert.Single(DataRows(Transformer(live.HtmlFragment))).Elements("td").First().InnerText));
        Assert.DoesNotContain("Новый узел", live.HtmlFragment);
        Assert.Contains("База", live.DrawioXml);
        var loaded = JsonSerializer.Deserialize<NetworkMapSettings>(snapshot!)!;
        var previousServices = loaded.Services.ToArray();
        var other = Write("C/appsettings.deploy.j2", """{"Client":{"Host":"http://server.example:80"}}""");
        loaded.Services = new() { new() { J2File = other, Label = "C" } };
        Assert.Empty(NetworkMapGenerator.Analyze(root, loaded).Connections);
        Assert.Equal(title, loaded.Targets[initial.Connections[0].TargetKey].Label);
        Assert.Equal("Новый узел", loaded.Targets["parameter:NEW_HOST"].Label);
        loaded.Services = previousServices.ToList();
        var analysis = NetworkMapGenerator.Analyze(root, loaded);
        var result = NetworkMapGenerator.Render(NetworkPageTemplates.BasicHtml, null, analysis);
        var xml = XDocument.Parse(result.DrawioXml);
        var cells = xml.Descendants("mxCell").ToArray();
        var frame = Assert.Single(cells.Where(c => (string?)c.Attribute("kind") == "container"));
        Assert.Equal("k8s", (string?)frame.Attribute("value"));
        var db = Assert.Single(cells.Where(c => WebUtility.HtmlDecode((string?)c.Attribute("value")) == options.Label));
        Assert.Equal("1", (string?)db.Attribute("parent"));
        Assert.Equal("1", (string?)cells.Single(c => (string?)c.Attribute("id") == loaded.Services[1].Id).Attribute("parent"));
        var firstCell = Assert.Single(DataRows(Transformer(result.HtmlFragment))).Elements("td").First();
        Assert.Equal(options.Label, WebUtility.HtmlDecode(firstCell.InnerText));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
