using System.Text;
using HtmlAgilityPack;
using ServiceAutoPassport.Core;

namespace ServiceAutoPassport.Tests;

public sealed class PassportTests
{
    [Fact]
    public void CfHtmlOffsetsAreUtf8ByteOffsets()
    {
        const string fragment = "<p>Привет, Confluence</p>";
        var payload = CfHtml.Build(fragment);
        var bytes = Encoding.UTF8.GetBytes(payload);
        var start = Offset(payload, "StartFragment:");
        var end = Offset(payload, "EndFragment:");
        Assert.Equal(fragment, Encoding.UTF8.GetString(bytes, start, end - start));
        Assert.Equal(fragment, CfHtml.ExtractFragment(payload));
    }

    [Fact]
    public void ClipboardProbeRecognizesWholePageAndSingleCardBlock()
    {
        var wholePage = CfHtml.Build(TestTemplate.FullPage);
        var cardBlock = CfHtml.Build(TestTemplate.CardBlock);

        Assert.True(ClipboardTemplateProbe.LooksLikeTemplate(wholePage));
        Assert.True(ClipboardTemplateProbe.LooksLikeTemplate(cardBlock));
        Assert.True(ClipboardTemplateProbe.LooksLikeTemplate(CfHtml.Build(
            "<h2>Дополнительная информация</h2><table data-macro-name=\"table-excerpt\"><tr><td></td></tr></table>")));
        Assert.False(ClipboardTemplateProbe.LooksLikeTemplate(CfHtml.Build("<p>Название сервиса</p>")));
        Assert.False(ClipboardTemplateProbe.LooksLikeTemplate("plain text"));
    }

    [Fact]
    public void SavedTemplateMergesClipboardValuesBeforeYamlAndJ2()
    {
        using var fixture = new Fixture();
        fixture.Write("app-params/.git/config", "[remote \"origin\"]\n  url = git@gitlab.example:team/app-params.git\n");
        fixture.Write("component/.git/config", "[remote \"origin\"]\n  url = git@gitlab.example:team/component.git\n");
        fixture.Write("app-params/application.yml", "deploy_num: 2\nnamespace: runtime\n");
        fixture.Write("component/demo/appsettings.deploy.j2",
            "{{ SVC_PAYMENT_API }} {{ S3_TEST_URL }} {{ DB_PRO_PRODUCTS_DATABASE }} {{ CUSTOM_FLAG }}");

        var savedTemplate = CfHtml.Build(TestTemplate.FullPage);
        var edited = new HtmlDocument();
        edited.LoadHtml(TestTemplate.FullPage);
        var tables = edited.DocumentNode.Descendants("table").ToArray();
        var cardRows = tables[0].Descendants("tr").ToArray();
        Cells(cardRows[0])[^1].InnerHtml = "Старое имя";
        Cells(cardRows.Single(row => Cell(row, 0) == "Описание"))[^1].InnerHtml = "Описание из буфера";
        var serviceRow = tables[1].Descendants("tr").Skip(1).Single();
        Cells(serviceRow)[1].InnerHtml = "SVC_PAYMENT_API";
        Cells(serviceRow)[2].InnerHtml = "Комментарий из буфера";
        var accessRow = tables[2].Descendants("tr").Single(row => Cell(row, 0) == "S3");
        Cells(accessRow)[3].InnerHtml = "Ссылка из буфера";
        var databaseRow = FindRow(tables[4], "DB_PRO_PRODUCTS_DATABASE");
        Cells(databaseRow)[0].InnerHtml = "БД из буфера";
        Cells(databaseRow)[3].InnerHtml = "База из буфера";
        tables[4].AppendChild(HtmlNode.CreateNode(
            "<tr><td>Новый параметр из буфера</td><td>CUSTOM_FLAG</td><td>Нет</td><td>Новое значение</td></tr>"));
        var excerpt = tables[5];
        excerpt.SetAttributeValue("data-macro-parameters", "hide=false");
        excerpt.Descendants("td").First().InnerHtml = "Детали из буфера";
        edited.DocumentNode.Descendants("p").Last().InnerHtml = "Текст из буфера";

        TemplateValueMerger.ValidateFullTemplate(savedTemplate);
        var partialCard = CfHtml.Build(tables[0].OuterHtml);
        Assert.Throws<InvalidDataException>(() => TemplateValueMerger.ValidateFullTemplate(partialCard));
        var restoredCard = TemplateValueMerger.Merge(savedTemplate, partialCard);
        Assert.Contains("Старое имя", CfHtml.ExtractFragment(restoredCard));
        Assert.Contains("data-macro-name=\"table-excerpt\"", CfHtml.ExtractFragment(restoredCard));

        var merged = TemplateValueMerger.Merge(savedTemplate, CfHtml.Build(edited.DocumentNode.InnerHtml));
        var mergedDocument = new HtmlDocument();
        mergedDocument.LoadHtml(CfHtml.ExtractFragment(merged));
        Assert.Contains("S3_TEST_URL", mergedDocument.DocumentNode.Descendants("table").ElementAt(2).InnerText);
        var result = PassportGenerator.Generate(merged, new PassportOptions(
            "demo", fixture.PathTo("app-params"),
            fixture.PathTo("component/demo/appsettings.deploy.j2"), "Финальное имя", "Команда А"));
        var resultDocument = new HtmlDocument();
        resultDocument.LoadHtml(result.HtmlFragment);
        var resultTables = resultDocument.DocumentNode.Descendants("table").ToArray();
        Assert.Equal("Финальное имя", Cell(resultTables[0].Descendants("tr").First(), 2));
        Assert.Contains("Описание из буфера", result.HtmlFragment);
        Assert.Contains("Комментарий из буфера", result.HtmlFragment);
        Assert.Contains("Ссылка из буфера", result.HtmlFragment);
        Assert.Equal("БД из буфера", Cell(FindRow(resultTables[4], "DB_PRO_PRODUCTS_DATABASE"), 0));
        Assert.Equal("База из буфера", Cell(FindRow(resultTables[4], "DB_PRO_PRODUCTS_DATABASE"), 3));
        Assert.Equal("Новый параметр из буфера", Cell(FindRow(resultTables[4], "CUSTOM_FLAG"), 0));
        Assert.Contains("Детали из буфера", result.HtmlFragment);
        Assert.Contains("Текст из буфера", result.HtmlFragment);
        Assert.Contains("data-macro-parameters=\"hide=true|name=test_passport_microservice\"", result.HtmlFragment);
        Assert.DoesNotContain("data-macro-parameters=\"hide=false\"", result.HtmlFragment);
    }

    [Fact]
    public void YamlLayersUseJ2MagicWandOrderAndShallowOverride()
    {
        using var fixture = new Fixture();
        fixture.Write("app-params/application.yaml", "cpu_req: 0.1\nsettings:\n  global: yes\n");
        fixture.Write("app-params/sample-service/application.yaml", "cpu_req: 0.2\nsettings:\n  service: yes\n");
        fixture.Write("app-params/application-prod.yaml", "cpu_req: 0.3\n");
        fixture.Write("app-params/sample-service/sample-service-prod.yaml", "cpu_req: 0.4\n");

        var config = YamlConfiguration.Load(fixture.PathTo("app-params"), "sample-service");

        Assert.Equal("0.4", config.Required("cpu_req"));
        Assert.Contains("service", config.Required("settings"));
        Assert.DoesNotContain("global", config.Required("settings"));
        Assert.Contains("cpu_req", config.GlobalKeys);
        Assert.DoesNotContain("settings_from_service", config.GlobalKeys);
    }

    [Fact]
    public void GlobalApplicationWorksWithoutServiceDirectory()
    {
        using var fixture = new Fixture();
        fixture.Write("app-params/.git/config", "[remote \"origin\"]\n  url = git@gitlab.example:team/app-params.git\n");
        fixture.Write("component/.git/config", "[remote \"origin\"]\n  url = git@gitlab.example:team/component.git\n");
        fixture.Write("app-params/application.yaml", "cpu_req: 0.4\nmem_req: 512\nSECTION_LOGGING: INFO\n");
        fixture.Write("app-params/application-prod.yaml", "replicas: 9\nnamespace: prod-only\n");
        fixture.Write("component/My.App.TestApp.Daemon/appsettings.deploy.j2",
            "{{ SVC_GLOBAL_HOST }} {{ SECTION_LOGGING }}");
        var root = fixture.PathTo("app-params");
        const string service = "my-app-testapp-daemon";
        Assert.False(Directory.Exists(fixture.PathTo("app-params/my-app-testapp-daemon")));

        var yaml = YamlConfiguration.Load(root, service);
        Assert.Equal("0.4", yaml.Required("cpu_req"));
        Assert.False(yaml.Values.ContainsKey("replicas"));
        Assert.False(yaml.Values.ContainsKey("namespace"));

        var result = PassportGenerator.Generate(CfHtml.Build(TestTemplate.FullPage), new PassportOptions(
            service, root, fixture.PathTo("component/My.App.TestApp.Daemon/appsettings.deploy.j2"),
            "Тестовый сервис", "Команда А"));
        var document = new HtmlDocument();
        document.LoadHtml(result.HtmlFragment);
        var card = document.DocumentNode.Descendants("table").First();
        Assert.Equal("указываем количество", CardValue(card, "Количество Pod"));
        Assert.Equal("исходный namespace", CardValue(card, "Namespace в K8S"));
        Assert.Contains("0.4", CardValue(card, "Requests"));
        Assert.Contains("href=\"https://gitlab.example/team/app-params/-/tree/master\"", result.HtmlFragment);
        Assert.Contains("SVC_GLOBAL_HOST", result.HtmlFragment);
    }

    [Fact]
    public void SuggestsServiceNameFromJ2FolderWhenYamlDirectoryIsAbsent()
    {
        const string j2Folder = "/component/My.App.TestApp.Daemon";
        Assert.Equal("my-app-testapp-daemon", ServiceInputs.SuggestServiceName(j2Folder, []));
        Assert.Equal("my-app-testapp-daemon", ServiceInputs.SuggestServiceName(j2Folder,
            ["other-service", "my-app-testapp-daemon"]));
        Assert.Equal("myapp-legal-testapp-daemon", ServiceInputs.SuggestServiceName(j2Folder,
            ["other-service", "myapp-legal-testapp-daemon"]));
        Assert.Equal("my-app-testapp-daemon", ServiceInputs.SuggestServiceName(j2Folder, ["only-service"]));
    }

    [Fact]
    public void FindsServiceAndDeployTemplateFromSelectedFolders()
    {
        using var fixture = new Fixture();
        fixture.Write("app-params/sample-service/sample-service.yml", "replicas: 2\n");
        fixture.Write("app-params/not-a-service/readme.txt", "No YAML here");
        fixture.Write("component/My.App.TestApp.Daemon/appsettings.deploy.j2", "{{ SVC_HOST }}");
        fixture.Write("component/My.App.TestApp.Daemon/appsettings.secrets.j2", "{{ SECRET_KEY }}");

        Assert.Equal(new[] { "sample-service" }, ServiceInputs.FindServices(fixture.PathTo("app-params")));
        Assert.Equal(new[] { "appsettings.deploy.j2" },
            ServiceInputs.FindTemplates(fixture.PathTo("component/My.App.TestApp.Daemon")));
    }

    [Fact]
    public void FillsRealClipboardTemplateAndPreservesUnmappedCellsAndMacros()
    {
        using var fixture = new Fixture();
        fixture.Write("app-params/.git/config", "[remote \"origin\"]\n  url = git@gitlab.example:team/app-params.git\n");
        fixture.Write("component/.git/config", "[remote \"origin\"]\n  url = https://gitlab.example/team/component.git\n");
        fixture.Write("app-params/application.yml", "cpu_req: 0.2\nmem_req: 256\ndeploy_num: 2\nautoscale: yes\nnamespace: base\nSECTION_LOGGING: INFO\n");
        fixture.Write("app-params/application-prod.yml", "deploy_num: 3\nnamespace: prod-global\n");
        fixture.Write("app-params/sample-service/sample-service.yml", "namespace: service-base\nDB_PRO_PRODUCTS_DATABASE: mydb\n");
        fixture.Write("app-params/sample-service/sample-service-prod.yml", "namespace: service-prod\nmem_lim: 512\n");
        fixture.Write("component/demo_api/appsettings.deploy.j2",
            "{\"svc\": \"{{ SVC_PAYMENT_API }}\", \"s3\": \"{{ S3_TEST_URL }}\", " +
            "\"db\": \"{{ DB_PRO_PRODUCTS_DATABASE }}\", " +
            "\"logging\": \"{{ SECTION_LOGGING }}\", \"extra\": \"{{ NEW_SETTING }}\"}");
        fixture.Write("component/demo_api/secrets.j2",
            "{\"s3Secret\": \"{{ S3_TEST_SECRET_KEY }}\", \"private\": \"{{ PRIVATE_TOKEN }}\"}");
        var source = CfHtml.Build(TestTemplate.FullPage);

        var result = PassportGenerator.Generate(source, new PassportOptions(
            "sample-service", fixture.PathTo("app-params"),
            fixture.PathTo("component/demo_api/appsettings.deploy.j2"), "Платежи", "Команда А"));

        var document = new HtmlDocument();
        document.LoadHtml(result.HtmlFragment);
        var text = HtmlEntity.DeEntitize(document.DocumentNode.InnerText);
        Assert.Contains("Платежи", text);
        Assert.Contains("Команда А", text);
        Assert.Contains("demo-api", text);
        Assert.Contains("service-prod", text);
        Assert.Contains("0.15", text); // absent cpu_lim receives the agreed default
        Assert.Contains("512", text);
        Assert.Contains("Кратко назначение компонента", text);
        Assert.Contains("да/нет", text);
        Assert.Contains("Для доменного API", text);
        Assert.Contains("https://gitlab.example/team/app-params/-/tree/master/sample-service", result.HtmlFragment);
        Assert.Contains("https://gitlab.example/team/component/-/blob/master/demo_api/appsettings.deploy.j2", result.HtmlFragment);
        Assert.Contains("data-macro-name=\"toc\"", result.HtmlFragment);
        Assert.Contains("data-macro-name=\"table-excerpt\"", result.HtmlFragment);
        Assert.Contains("data-macro-name=\"ui-expand\"", result.HtmlFragment);
        var macros = document.DocumentNode.Descendants()
            .Where(node => node.Attributes["data-macro-name"] is not null).ToArray();
        Assert.Equal("title=Подсказка по объектам доступа", macros.Single(node =>
            node.GetAttributeValue("data-macro-name", "") == "ui-expand")
            .GetAttributeValue("data-macro-parameters", ""));
        Assert.Equal("hide=true|name=test_passport_microservice", macros.Single(node =>
            node.GetAttributeValue("data-macro-name", "") == "table-excerpt")
            .GetAttributeValue("data-macro-parameters", ""));

        var tables = document.DocumentNode.Descendants("table").ToArray();
        Assert.Equal(1, tables[1].Descendants("tr").Count() - 1); // one SVC_ row
        Assert.Equal(1, Count(tables[2], "S3"));
        Assert.Equal(0, Count(tables[2], "Kafka test"));
        Assert.Equal(1, Count(tables[2], "S3_TEST_SECRET_KEY"));
        Assert.DoesNotContain("S3_TEST_ACCESS_KEY", tables[2].InnerText);
        Assert.Equal(1, Count(tables[4], "PRIVATE_TOKEN"));
        var privateToken = FindRow(tables[4], "PRIVATE_TOKEN");
        Assert.Equal("Да", Cell(privateToken, 2));
        Assert.Equal("", Cell(privateToken, 0));
        Assert.Equal("", Cell(privateToken, 3));
        Assert.Equal("ИМЯ_БД_СЕРВИСА", Cell(FindRow(tables[4], "DB_PRO_PRODUCTS_DATABASE"), 3));
        Assert.Equal("Дефолтное значение в базовых параметрах", Cell(FindRow(tables[4], "SECTION_LOGGING"), 3));
        Assert.Equal("Нет", Cell(FindRow(tables[4], "NEW_SETTING"), 2));
    }

    [Fact]
    public void SavedKeyDescriptionsFillParametersAndServiceNames()
    {
        using var fixture = new Fixture();
        fixture.Write("app-params/.git/config", "[remote \"origin\"]\n  url = git@gitlab.example:team/app-params.git\n");
        fixture.Write("component/.git/config", "[remote \"origin\"]\n  url = git@gitlab.example:team/component.git\n");
        fixture.Write("app-params/application.yml", "SECTION_LOGGING: INFO\n");
        fixture.Write("component/demo/appsettings.deploy.j2",
            "{{ SVC_PAYMENT_API }} {{ DB_PRO_PRODUCTS_DATABASE }} {{ SECTION_LOGGING }} {{ NEW_SETTING }}");
        fixture.Write("component/demo/secrets.j2", "{{ PRIVATE_TOKEN }}");
        var descriptions = new Dictionary<string, string>
        {
            ["SVC_PAYMENT_API"] = "Платёжный API",
            ["DB_PRO_PRODUCTS_DATABASE"] = "Основная БД & <хранилище>",
            ["NEW_SETTING"] = "Новая опция",
            ["PRIVATE_TOKEN"] = "Секретный токен"
        };

        var result = PassportGenerator.Generate(CfHtml.Build(TestTemplate.FullPage), new PassportOptions(
            "demo", fixture.PathTo("app-params"), fixture.PathTo("component/demo/appsettings.deploy.j2"),
            "Демо", "Команда А", descriptions));

        var document = new HtmlDocument();
        document.LoadHtml(result.HtmlFragment);
        var tables = document.DocumentNode.Descendants("table").ToArray();
        var serviceRow = tables[1].Descendants("tr").Skip(1).Single();
        Assert.Equal("Платёжный API", Cell(serviceRow, 0));
        Assert.Equal("SVC_PAYMENT_API", Cell(serviceRow, 1));
        Assert.Equal("Основная БД & <хранилище>",
            Cell(FindRow(tables[4], "DB_PRO_PRODUCTS_DATABASE"), 0));
        Assert.Contains("Основная БД &amp; &lt;хранилище&gt;", result.HtmlFragment);
        Assert.Equal("Новая опция", Cell(FindRow(tables[4], "NEW_SETTING"), 0));
        Assert.Equal("Секретный токен", Cell(FindRow(tables[4], "PRIVATE_TOKEN"), 0));
        Assert.Equal("Логгирование", Cell(FindRow(tables[4], "SECTION_LOGGING"), 0));
    }

    [Fact]
    public void ImportsExcelAndSpaceSeparatedKeyDescriptions()
    {
        var imported = KeyDescriptionImport.Parse(
            "Ключ\tОписание\r\nSVC_PAYMENT_API\tПлатёжный API\r\n" +
            "PRIVATE_TOKEN    Секретный токен доступа\n\n");

        Assert.Equal(2, imported.Count);
        Assert.Equal("Платёжный API", imported["SVC_PAYMENT_API"]);
        Assert.Equal("Секретный токен доступа", imported["PRIVATE_TOKEN"]);
    }

    [Theory]
    [InlineData("SVC_PAYMENT_API")]
    [InlineData("SVC_PAYMENT_API\tОписание\nSVC_PAYMENT_API\tДругое описание")]
    [InlineData("SVC_PAYMENT_API\t")]
    public void RejectsIncompleteOrDuplicateKeyDescriptionRows(string input)
    {
        Assert.Throws<InvalidDataException>(() => KeyDescriptionImport.Parse(input));
    }

    [Fact]
    public void SupportsSampleAppNamesAndYamlKeys()
    {
        using var fixture = new Fixture();
        fixture.Write("app-params/.git/config", "[remote \"origin\"]\n  url = git@gitlab.example:team/app-params.git\n");
        fixture.Write("component/.git/config", "[remote \"origin\"]\n  url = git@gitlab.example:team/component.git\n");
        fixture.Write("app-params/sample-service/sample-service.yml",
            "namespace_name: test-platform\ncpu_req: 100m\nmem_req: 200Mi\n");
        fixture.Write("app-params/sample-service/sample-service-prod.yml",
            "replicas: 2\ncpu_lim: 600m\nmem_lim: 1200Mi\n");
        fixture.Write("component/My.App.TestApp.Daemon/appsettings.deploy.j2",
            "{{ SVC_CRYPTOPRO_SIGN_HOST }}");
        fixture.Write("component/My.App.TestApp.Daemon/appsettings.secrets.j2",
            "{{ SAMPLE_SECRET_TOKEN }}");
        var source = CfHtml.Build(TestTemplate.FullPage);

        var result = PassportGenerator.Generate(source, new PassportOptions(
            "sample-service", fixture.PathTo("app-params"),
            fixture.PathTo("component/My.App.TestApp.Daemon/appsettings.deploy.j2"),
            "Подпись", "Команда А"));

        var document = new HtmlDocument();
        document.LoadHtml(result.HtmlFragment);
        var tables = document.DocumentNode.Descendants("table").ToArray();
        Assert.Equal("2", CardValue(tables[0], "Количество Pod"));
        Assert.Equal("test-platform", CardValue(tables[0], "Namespace в K8S"));
        Assert.Equal("max N /нет", CardValue(tables[0], "Автомасштабирование"));
        Assert.Contains("600m", CardValue(tables[0], "Limits"));
        Assert.Contains("1200Mi", CardValue(tables[0], "Limits"));
        Assert.Equal("Да", Cell(FindRow(tables[4], "SAMPLE_SECRET_TOKEN"), 2));
    }

    private static string CardValue(HtmlNode table, string label) => Cell(
        table.Descendants("tr").Single(row =>
            HtmlEntity.DeEntitize(row.InnerText).Contains(label, StringComparison.Ordinal)), 2);

    private static int Count(HtmlNode node, string value) =>
        node.Descendants("tr").Count(row => HtmlEntity.DeEntitize(row.InnerText).Contains(value, StringComparison.Ordinal));

    private static HtmlNode FindRow(HtmlNode table, string variable) => table.Descendants("tr")
        .Single(row => Cell(row, 1) == variable);

    private static string Cell(HtmlNode row, int index) => HtmlEntity.DeEntitize(row.ChildNodes
        .Where(node => node.Name is "td" or "th").ElementAt(index).InnerText).Trim();

    private static HtmlNode[] Cells(HtmlNode row) => row.ChildNodes
        .Where(node => node.Name is "td" or "th").ToArray();

    private static int Offset(string payload, string label)
    {
        var start = payload.IndexOf(label, StringComparison.Ordinal) + label.Length;
        return int.Parse(payload.Substring(start, 10));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "ServiceAutoPassportTests-" + Guid.NewGuid().ToString("N"));

        public string PathTo(string relative) => System.IO.Path.Combine(_root,
            relative.Replace('/', System.IO.Path.DirectorySeparatorChar));

        public void Write(string relative, string content)
        {
            var path = PathTo(relative);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        public void Dispose()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
    }
}
