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
    private const string Template = "<div>Saved layout</div><table><tr><td><img data-macro-name='drawio' data-macro-id='saved' /></td><td><table><tr><th>Подключение</th><th>Параметры</th></tr></table></td></tr></table>";
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
        Assert.Equal(3, xml.Descendants("mxCell").Count(c => (string?)c.Attribute("vertex") == "1"));
        Assert.Equal(3, xml.Descendants("mxCell").Count(c => (string?)c.Attribute("edge") == "1"));
        Assert.Contains(xml.Descendants("mxCell"), c => (string?)c.Attribute("source") == settings.Services[0].Id && (string?)c.Attribute("target") == settings.Services[1].Id);
        var html = new HtmlDocument(); html.LoadHtml(result.HtmlFragment);
        Assert.Equal(3, html.DocumentNode.Descendants("tr").Count(r => r.ChildNodes.Count(n => n.Name is "td" or "th") == 5));
        var stable = NetworkMapGenerator.Render(Template, Current, analysis);
        Assert.Equal(result.DrawioXml, stable.DrawioXml);
    }
    [Fact]
    public void DefaultsExpansionIpv6MissingValuesAndCredentials()
    {
        Write("params/application.yml", "HOST: '{{ BASE }}'\nBASE: '[2001:db8::1]'\nLOOP: '{{ LOOP }}'\n");
        var main = Write("A/appsettings.deploy.j2", """
            {"GrpcClients":{"Remote":{"Host":"{{ HOST }}"}},
             "External":{"EndpointUrl":"https://user:password@example.test/private?q=secret"},
             "Broken":{"Host":"{{ MISSING }}"},"Cyclic":{"Host":"{{ LOOP }}"},
             "Kestrel":{"Endpoints":{"Http":{"Url":"http://localhost:5000"}}}}
            """);
        var settings = new NetworkMapSettings { Environments = new() { "prod" }, Services = new() { new() { J2File = main, Label = "A", YamlService = "a" } } };
        var analysis = NetworkMapGenerator.Analyze(Path.Combine(_root, "params"), settings);
        Assert.Equal(4, analysis.Connections.Count);
        Assert.Contains(analysis.Connections, c => c.Endpoints["prod"] == "[2001:db8::1]:80 (порт по умолчанию)");
        Assert.Contains(analysis.Connections, c => c.Endpoints["prod"] == "example.test:443 (порт по умолчанию)");
        Assert.Contains(analysis.Connections, c => c.Endpoints["prod"].Contains("не найдено: MISSING"));
        Assert.Contains(analysis.Connections, c => c.Endpoints["prod"].Contains("циклическая ссылка"));
        var result = NetworkMapGenerator.Render(Template, Current, analysis);
        Assert.DoesNotContain("password", result.HtmlFragment + result.DrawioXml);
        Assert.DoesNotContain("private", result.HtmlFragment);
        Assert.DoesNotContain("q=secret", result.HtmlFragment);
    }
    [Fact]
    public void PortsSeparateRowsButNotSharedNodesAndEqualHostsDoNotMergeDifferentKeys()
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
        Assert.Equal(2, XDocument.Parse(result.DrawioXml).Descendants("mxCell").Count(c => (string?)c.Attribute("vertex") == "1"));
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
        Write("A/appsettings.deploy.j2", "{\"Host\": \"http://host:80\"}");
        var analysis = NetworkMapGenerator.Analyze(Path.Combine(_root, "params"), settings);
        var withoutMacro = NetworkMapGenerator.Render(Template, "<p>No macro</p>", analysis);
        Assert.Contains("data-network-diagram-placeholder", withoutMacro.HtmlFragment);
        Assert.DoesNotContain("data-macro-id", withoutMacro.HtmlFragment);
    }

    [Fact]
    public void DatabaseSettingsFragmentsAndMissingCredentialsDoNotHideConnections()
    {
        Write("params/application.yml", "DB_HOST: database\nDB_PORT: 5432\nCONNECTION: 'Host=database;Port=5432;Password={{ MISSING_PASSWORD }}'\n");
        var main = Write("A/appsettings.deploy.j2", "{\"ConnectionStrings\":{\"Whole\":\"{{ CONNECTION }}\"},\"Rmq\":{\"VirtualHost\":\"vhost\"},\"PageUrl\":\"/local/path\"}");
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
        var settings = new NetworkMapSettings { Environments = new() { "test", "prod" }, Services = new()
        {
            new() { J2File = main, Label = "A", YamlService = "a" }
        }};
        Assert.Equal("", settings.TemplateHtml);
        var analysis = NetworkMapGenerator.Analyze(Path.Combine(_root, "params"), settings);
        NetworkMapGenerator.ValidateTemplate(NetworkPageTemplates.BasicHtml);
        var result = NetworkMapGenerator.Render(NetworkPageTemplates.BasicHtml, null, analysis);
        Assert.Contains("service.example:443", result.HtmlFragment);
        Assert.Contains("<th>test</th><th>prod</th>", result.HtmlFragment);
        Assert.Contains("data-network-diagram-placeholder", result.HtmlFragment);
        Assert.DoesNotContain("data-macro-name=", result.HtmlFragment);
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
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
