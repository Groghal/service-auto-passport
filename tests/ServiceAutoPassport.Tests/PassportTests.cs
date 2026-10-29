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
    public void EmptyIdentityFieldsPreserveClipboardAndPriorityIsIndependent()
    {
        using var fixture = new Fixture();
        fixture.Write("app-params/.git/config", "[remote \"origin\"]\n  url = git@gitlab.example:team/app-params.git\n");
        fixture.Write("component/.git/config", "[remote \"origin\"]\n  url = git@gitlab.example:team/component.git\n");
        fixture.Write("app-params/application.yml", "cpu_req: 0.2\n");
        fixture.Write("component/demo/appsettings.deploy.j2", "{{ TEST_VALUE }}");

        var edited = new HtmlDocument();
        edited.LoadHtml(TestTemplate.FullPage);
        var card = edited.DocumentNode.Descendants("table").First();
        Cells(card.Descendants("tr").First())[^1].InnerHtml = "<p><strong>Имя из буфера</strong></p>";
        Cells(card.Descendants("tr").Single(row => Cell(row, 0) == "Команда"))[^1].InnerHtml =
            "<p><em>Команда из буфера</em></p>";
        var clipboard = CfHtml.Build(edited.DocumentNode.InnerHtml);
        var merged = TemplateValueMerger.Merge(CfHtml.Build(TestTemplate.FullPage), clipboard);

        foreach (var source in new[] { clipboard, merged })
        {
            PassportResult Generate(string name = "", string team = "",
                bool namePriority = true, bool teamPriority = true) =>
                PassportGenerator.Generate(source, new PassportOptions(
                    "demo", fixture.PathTo("app-params"), fixture.PathTo("component/demo/appsettings.deploy.j2"),
                    name, team, DisplayNamePriority: namePriority, TeamPriority: teamPriority));

            var unchanged = Generate();
            Assert.Contains("<strong>Имя из буфера</strong>", unchanged.HtmlFragment);
            Assert.Contains("<em>Команда из буфера</em>", unchanged.HtmlFragment);

            var nameWins = Generate("Имя из формы", "Команда из формы", teamPriority: false);
            Assert.Equal("Имя из формы", IdentityValue(nameWins, "Название сервиса"));
            Assert.Equal("Команда из буфера", IdentityValue(nameWins, "Команда"));

            var teamWins = Generate("Имя из формы", "Команда из формы", namePriority: false);
            Assert.Equal("Имя из буфера", IdentityValue(teamWins, "Название сервиса"));
            Assert.Equal("Команда из формы", IdentityValue(teamWins, "Команда"));
        }

        var emptyTemplateResult = PassportGenerator.Generate(CfHtml.Build(TestTemplate.FullPage),
            new PassportOptions("demo", fixture.PathTo("app-params"),
                fixture.PathTo("component/demo/appsettings.deploy.j2"),
                "Запасное имя", "Запасная команда", DisplayNamePriority: false, TeamPriority: false));
        Assert.Equal("Запасное имя", IdentityValue(emptyTemplateResult, "Название сервиса"));
        Assert.Equal("Запасная команда", IdentityValue(emptyTemplateResult, "Команда"));
    }

    [Fact]
    public void GeneratedPageCanBeRegeneratedAfterTablesBecomeHeaderOnly()
    {
        using var fixture = new Fixture();
        fixture.Write("app-params/.git/config", "[remote \"origin\"]\n  url = git@gitlab.example:team/app-params.git\n");
        fixture.Write("component/.git/config", "[remote \"origin\"]\n  url = git@gitlab.example:team/component.git\n");
        fixture.Write("app-params/application.yml", "cpu_req: 0.2\n");
        fixture.Write("component/demo/appsettings.deploy.j2", "no variables");
        var options = new PassportOptions("demo", fixture.PathTo("app-params"),
            fixture.PathTo("component/demo/appsettings.deploy.j2"));

        var first = PassportGenerator.Generate(CfHtml.Build(TestTemplate.FullPage), options);
        var firstDocument = new HtmlDocument();
        firstDocument.LoadHtml(first.HtmlFragment);
        var firstTables = firstDocument.DocumentNode.Descendants("table").ToArray();
        Assert.Single(firstTables[1].Descendants("tr"));
        Assert.Single(firstTables[4].Descendants("tr"));

        fixture.Write("component/demo/appsettings.deploy.j2", "{{ SVC_TEST_API }} {{ TEST_SETTING }}");
        var second = PassportGenerator.Generate(CfHtml.Build(first.HtmlFragment), options);
        var secondDocument = new HtmlDocument();
        secondDocument.LoadHtml(second.HtmlFragment);
        var secondTables = secondDocument.DocumentNode.Descendants("table").ToArray();
        Assert.Equal("SVC_TEST_API", Cell(FindRow(secondTables[1], "SVC_TEST_API"), 0));
        Assert.Equal("TEST_SETTING", Cell(FindRow(secondTables[4], "TEST_SETTING"), 1));
        Assert.All(secondTables[1].Descendants("tr").Skip(1), row =>
            Assert.All(Cells(row), cell => Assert.Equal("td", cell.Name)));
    }

    [Fact]
    public void IdentityFieldsPreserveConfluenceHtmlFragmentsAndMacroMetadata()
    {
        using var fixture = new Fixture();
        fixture.Write("app-params/.git/config", "[remote \"origin\"]\n  url = git@gitlab.example:team/app-params.git\n");
        fixture.Write("component/.git/config", "[remote \"origin\"]\n  url = git@gitlab.example:team/component.git\n");
        fixture.Write("app-params/application.yml", "cpu_req: 0.2\n");
        fixture.Write("component/demo/appsettings.deploy.j2", "{{ TEST_VALUE }}");

        const string macro = "<img class=\"editor-inline-macro\" " +
            "src=\"https://confluence.example/plugins/servlet/confluence/placeholder/macro?definition=TEST\" " +
            "data-macro-name=\"jira\" data-macro-id=\"96b2b87c-30a7-44bb-add7-bbcfa819ec7b\" " +
            "data-macro-parameters=\"key=TEST-7|serverId=d873c574-729a-33cf-b479-11128a000000\" " +
            "data-macro-schema-version=\"1\" />";
        var imported = CfHtml.ExtractFragment(CfHtml.Build(macro));
        var result = PassportGenerator.Generate(CfHtml.Build(TestTemplate.FullPage), new PassportOptions(
            "demo", fixture.PathTo("app-params"), fixture.PathTo("component/demo/appsettings.deploy.j2"),
            imported, "<section data-layout=\"two-column\"><p>Команда <strong>А</strong></p></section>",
            DisplayNameIsHtml: true, TeamIsHtml: true));
        var clipboard = CfHtml.Build(result.HtmlFragment);
        var document = new HtmlDocument();
        document.LoadHtml(CfHtml.ExtractFragment(clipboard));
        var card = document.DocumentNode.Descendants("table").First();
        var nameRow = card.Descendants("tr").First(row => Cell(row, 0) == "Название сервиса");
        var image = Cells(nameRow)[^1].Descendants("img").Single();
        Assert.Equal("jira", image.GetAttributeValue("data-macro-name", ""));
        Assert.Equal("96b2b87c-30a7-44bb-add7-bbcfa819ec7b",
            image.GetAttributeValue("data-macro-id", ""));
        Assert.Equal("key=TEST-7|serverId=d873c574-729a-33cf-b479-11128a000000",
            image.GetAttributeValue("data-macro-parameters", ""));
        Assert.Equal("1", image.GetAttributeValue("data-macro-schema-version", ""));
        Assert.Contains("placeholder/macro?definition=TEST", image.GetAttributeValue("src", ""));
        var teamRow = card.Descendants("tr").First(row => Cell(row, 0) == "Команда");
        var section = Cells(teamRow)[^1].Descendants("section").Single();
        Assert.Equal("two-column", section.GetAttributeValue("data-layout", ""));
        Assert.Equal("А", section.Descendants("strong").Single().InnerText);

        var existing = new HtmlDocument();
        existing.LoadHtml(TestTemplate.FullPage);
        var existingCard = existing.DocumentNode.Descendants("table").First();
        var existingName = existingCard.Descendants("tr").First(row => Cell(row, 0) == "Название сервиса");
        Cells(existingName)[^1].InnerHtml = macro;
        var retained = PassportGenerator.Generate(CfHtml.Build(existing.DocumentNode.InnerHtml),
            new PassportOptions("demo", fixture.PathTo("app-params"),
                fixture.PathTo("component/demo/appsettings.deploy.j2"),
                "Замена", DisplayNamePriority: false));
        Assert.Contains("data-macro-id=\"96b2b87c-30a7-44bb-add7-bbcfa819ec7b\"",
            retained.HtmlFragment);
        Assert.DoesNotContain("Замена", retained.HtmlFragment);
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

    [Theory]
    [InlineData("")]
    [InlineData("  \n")]
    [InlineData("# No overrides yet\n")]
    [InlineData("---\n")]
    public void EmptyYamlFilesDoNotPreventGenerationOrLayering(string emptyYaml)
    {
        using var fixture = new Fixture();
        fixture.Write("app-params/.git/config", "[remote \"origin\"]\n  url = git@gitlab.example:team/app-params.git\n");
        fixture.Write("component/.git/config", "[remote \"origin\"]\n  url = git@gitlab.example:team/component.git\n");
        fixture.Write("app-params/application.yml", emptyYaml);
        fixture.Write("component/demo/appsettings.deploy.j2", "{{ TEST_VALUE }}");

        var root = fixture.PathTo("app-params");
        Assert.Empty(YamlConfiguration.Load(root, "demo").Values);
        var result = PassportGenerator.Generate(CfHtml.Build(TestTemplate.FullPage), new PassportOptions(
            "demo", root, fixture.PathTo("component/demo/appsettings.deploy.j2")));
        var document = new HtmlDocument();
        document.LoadHtml(result.HtmlFragment);
        var card = document.DocumentNode.Descendants("table").First();
        Assert.Equal("1", CardValue(card, "Количество Pod"));
        Assert.Equal("нет", CardValue(card, "Автомасштабирование"));
        Assert.Contains("RAM (Mb): значение не найдено", CardValue(card, "Requests"));
        Assert.Contains("RAM (Mb): значение не найдено", CardValue(card, "Limits"));

        fixture.Write("app-params/application.yml", "cpu_req: 0.4\n");
        fixture.Write("app-params/demo/demo.yml", emptyYaml);
        var layered = YamlConfiguration.Load(root, "demo");
        Assert.Equal("0.4", layered.Required("cpu_req"));

        File.Delete(fixture.PathTo("app-params/application.yml"));
        Assert.Empty(YamlConfiguration.Load(root, "demo").Values);
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
        Assert.Equal("1", CardValue(card, "Количество Pod"));
        Assert.Equal("исходный namespace", CardValue(card, "Namespace в K8S"));
        Assert.Contains("0.4", CardValue(card, "Requests"));
        Assert.Contains("RAM (Mb): 512Mi", CardValue(card, "Requests"));
        Assert.Contains("RAM (Mb): значение не найдено", CardValue(card, "Limits"));
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
        Assert.Contains("CPU (доля от ядра): значение не найдено", text);
        Assert.Contains("RAM (Mb): 256Mi", CardValue(document.DocumentNode.Descendants("table").First(), "Requests"));
        Assert.Contains("RAM (Mb): 512Mi", CardValue(document.DocumentNode.Descendants("table").First(), "Limits"));
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
        Assert.Equal("3", CardValue(tables[0], "Количество Pod"));
        Assert.Equal("yes", CardValue(tables[0], "Автомасштабирование"));
        Assert.Equal(1, Count(tables[2], "S3"));
        Assert.Equal(0, Count(tables[2], "Kafka test"));
        Assert.Equal(1, Count(tables[2], "S3_TEST_SECRET_KEY"));
        Assert.DoesNotContain("S3_TEST_ACCESS_KEY", tables[2].InnerText);
        Assert.Equal(1, Count(tables[4], "PRIVATE_TOKEN"));
        var privateToken = FindRow(tables[4], "PRIVATE_TOKEN");
        Assert.Equal("Да", Cell(privateToken, 2));
        Assert.Equal("", Cell(privateToken, 0));
        Assert.Equal("", Cell(privateToken, 3));
        Assert.Equal("mydb", Cell(FindRow(tables[4], "DB_PRO_PRODUCTS_DATABASE"), 3));
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
            ["SVC_PAYMENT_API"] = "<p>Платёжный <strong>API</strong></p>",
            ["DB_PRO_PRODUCTS_DATABASE"] = "Основная БД & <хранилище>",
            ["NEW_SETTING"] = "<p>См. <a href=\"https://jira.example/browse/TEST-7\">TEST-7</a> " +
                              "<img class=\"editor-inline-macro\" data-macro-name=\"jira\" " +
                              "data-macro-parameters=\"key=TEST-7\" src=\"https://cnfl.example/placeholder\" /></p>",
            ["PRIVATE_TOKEN"] = "Секретный токен"
        };
        var htmlKeys = new HashSet<string> { "SVC_PAYMENT_API", "NEW_SETTING" };

        var result = PassportGenerator.Generate(CfHtml.Build(TestTemplate.FullPage), new PassportOptions(
            "demo", fixture.PathTo("app-params"), fixture.PathTo("component/demo/appsettings.deploy.j2"),
            "Демо", "Команда А", descriptions, HtmlDescriptionKeys: htmlKeys));

        var document = new HtmlDocument();
        document.LoadHtml(result.HtmlFragment);
        var tables = document.DocumentNode.Descendants("table").ToArray();
        var serviceRow = tables[1].Descendants("tr").Skip(1).Single();
        Assert.Equal("Платёжный API", Cell(serviceRow, 0));
        Assert.Contains("<strong>API</strong>", serviceRow.InnerHtml);
        Assert.Equal("SVC_PAYMENT_API", Cell(serviceRow, 1));
        Assert.Equal("Основная БД & <хранилище>",
            Cell(FindRow(tables[4], "DB_PRO_PRODUCTS_DATABASE"), 0));
        Assert.Contains("Основная БД &amp; &lt;хранилище&gt;", result.HtmlFragment);
        var jiraDescription = FindRow(tables[4], "NEW_SETTING");
        Assert.Equal("TEST-7", jiraDescription.Descendants("a").Single().InnerText);
        Assert.Equal("jira", jiraDescription.Descendants("img").Single()
            .GetAttributeValue("data-macro-name", ""));
        Assert.Equal("key=TEST-7", jiraDescription.Descendants("img").Single()
            .GetAttributeValue("data-macro-parameters", ""));
        Assert.Equal("Секретный токен", Cell(FindRow(tables[4], "PRIVATE_TOKEN"), 0));
        Assert.Equal("Логгирование", Cell(FindRow(tables[4], "SECTION_LOGGING"), 0));
    }

    [Fact]
    public void HtmlDescriptionDetectionAndValidation()
    {
        Assert.True(DescriptionHtml.LooksLikeMarkup("<a href=\"https://jira.example/browse/TEST-7\">TEST-7</a>"));
        Assert.False(DescriptionHtml.LooksLikeMarkup("Основная БД & <хранилище>"));
        Assert.Throws<InvalidDataException>(() => DescriptionHtml.Normalize("<script>alert(1)</script>"));
        Assert.Throws<InvalidDataException>(() => DescriptionHtml.Normalize(
            "<a href=\"javascript:alert(1)\">TEST-7</a>"));
        Assert.Throws<InvalidDataException>(() => DescriptionHtml.Normalize(
            "<img src=\"https://cnfl.example/placeholder\" onerror=\"alert(1)\">"));
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

    [Fact]
    public void AccessRowsKeepOnlyVariablesPresentInDeployOrSecrets()
    {
        using var fixture = new Fixture();
        fixture.Write("app-params/.git/config", "[remote \"origin\"]\n  url = git@gitlab.example:team/app-params.git\n");
        fixture.Write("component/.git/config", "[remote \"origin\"]\n  url = git@gitlab.example:team/component.git\n");
        fixture.Write("app-params/application.yml", "cpu_req: 0.2\n");
        fixture.Write("component/demo/appsettings.deploy.j2",
            "{{ DB_PRO_PRODUCTS_HOST }} {{ DB_PRO_PLATFORM_HOST }}");
        fixture.Write("component/demo/appsettings.secrets.j2",
            "ConnectionStrings__TestAppDB: Host={{ DB_PRO_PLATFORM_HOST }};" +
            "Password={{ DB_PRO_PRODUCTS_PASSWORD }}\n");

        var template = new HtmlDocument();
        template.LoadHtml(TestTemplate.FullPage);
        var access = template.DocumentNode.Descendants("table").ElementAt(2);
        access.AppendChild(HtmlNode.CreateNode("<tr><td>БД</td>" +
            "<td><p>DB_PRO_PRODUCTS_HOST DB_PRO_PRODUCTS_PORT</p></td>" +
            "<td><p><strong>DB_PRO_PRODUCTS_PASSWORD</strong> DB_PRO_PRODUCTS_USER</p></td>" +
            "<td><a href=\"https://example.test/db\">Документация БД</a></td></tr>"));
        var source = CfHtml.Build(template.DocumentNode.InnerHtml);
        var options = new PassportOptions("demo", fixture.PathTo("app-params"),
            fixture.PathTo("component/demo/appsettings.deploy.j2"));

        var result = PassportGenerator.Generate(source, options);
        var document = new HtmlDocument();
        document.LoadHtml(result.HtmlFragment);
        var tables = document.DocumentNode.Descendants("table").ToArray();
        var database = tables[2].Descendants("tr").Single(row => Cell(row, 0) == "БД");
        Assert.Equal("DB_PRO_PRODUCTS_HOST", Cell(database, 1));
        Assert.Equal("DB_PRO_PRODUCTS_PASSWORD", Cell(database, 2));
        Assert.Equal("DB_PRO_PRODUCTS_PASSWORD", Cells(database)[2].Descendants("strong").Single().InnerText);
        Assert.Equal("https://example.test/db", Cells(database)[3].Descendants("a").Single()
            .GetAttributeValue("href", ""));
        Assert.DoesNotContain("DB_PRO_PRODUCTS_PORT", tables[2].InnerText);
        Assert.DoesNotContain("DB_PRO_PRODUCTS_USER", tables[2].InnerText);
        Assert.DoesNotContain("DB_PRO_PLATFORM_HOST", tables[2].InnerText);
        Assert.DoesNotContain("DB_PRO_PRODUCTS_PASSWORD", tables[4].InnerText);

        fixture.Write("component/demo/appsettings.deploy.j2", "{{ DB_PRO_PLATFORM_HOST }}");
        fixture.Write("component/demo/appsettings.secrets.j2",
            "ConnectionStrings__TestAppDB: Host={{ DB_PRO_PLATFORM_HOST }}\n");
        var withoutMatches = PassportGenerator.Generate(source, options);
        var withoutMatchesDocument = new HtmlDocument();
        withoutMatchesDocument.LoadHtml(withoutMatches.HtmlFragment);
        Assert.DoesNotContain(withoutMatchesDocument.DocumentNode.Descendants("table").ElementAt(2)
            .Descendants("tr"), row => Cell(row, 0) == "БД");
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
        Assert.Equal("нет", CardValue(tables[0], "Автомасштабирование"));
        Assert.Contains("600m", CardValue(tables[0], "Limits"));
        Assert.Contains("1200Mi", CardValue(tables[0], "Limits"));
        Assert.Equal("Да", Cell(FindRow(tables[4], "SAMPLE_SECRET_TOKEN"), 2));
    }

    [Theory]
    [InlineData("My.App.WebApi", "API", "125m", "400Mi")]
    [InlineData("My.App.Worker", "DAEMON", "150m", "500Mi")]
    [InlineData("my-frontend", "UI", "25m", "100Mi")]
    public void ResourceDefaultsDependOnComponentAndExplicitValuesWin(
        string folder, string group, string requestCpu, string limitMemory)
    {
        using var fixture = new Fixture();
        fixture.Write("app-params/.git/config", "[remote \"origin\"]\n url = https://gitlab.example/app-params.git\n");
        fixture.Write("component/.git/config", "[remote \"origin\"]\n url = https://gitlab.example/component.git\n");
        fixture.Write("app-params/application.yaml", """
            DEFAULT_RESOURCES_UI_REQUEST: {"cpu": "25m", "memory": "50Mi"}
            DEFAULT_RESOURCES_UI_LIMIT: {"cpu": "50m", "memory": "100Mi"}
            DEFAULT_RESOURCES_API_REQUEST: {"cpu": "125m", "memory": "200Mi"}
            DEFAULT_RESOURCES_API_LIMIT: {"cpu": "200m", "memory": "400Mi"}
            DEFAULT_RESOURCES_DAEMON_REQUEST: {"cpu": "150m", "memory": "200Mi"}
            DEFAULT_RESOURCES_DAEMON_LIMIT: {"cpu": "200m", "memory": "500Mi"}
            """);
        fixture.Write($"component/{folder}/config.j2", "{{ TEST_VALUE }}");
        var options = new PassportOptions("demo", fixture.PathTo("app-params"),
            fixture.PathTo($"component/{folder}/config.j2"));
        HtmlNode Card()
        {
            var result = PassportGenerator.Generate(CfHtml.Build(TestTemplate.FullPage), options);
            var document = new HtmlDocument();
            document.LoadHtml(result.HtmlFragment);
            return document.DocumentNode.Descendants("table").First();
        }
        var card = Card();
        Assert.Contains($"{requestCpu} (значение по умолчанию)", CardValue(card, "Requests"));
        Assert.Contains($"{limitMemory} (значение по умолчанию)", CardValue(card, "Limits"));

        // A later layer replaces the whole resource map, without reviving missing base fields.
        fixture.Write("app-params/demo/demo.yml", $"DEFAULT_RESOURCES_{group}_REQUEST: {{cpu: 333m}}\nmem_req: 256\n");
        fixture.Write("app-params/application-prod.yml", $"DEFAULT_RESOURCES_{group}_LIMIT: {{cpu: 444m}}\n");
        fixture.Write("app-params/demo/demo-prod.yml", "cpu_lim: 900m\n");
        card = Card();
        Assert.Contains("333m (значение по умолчанию)", CardValue(card, "Requests"));
        Assert.Contains("RAM (Mb): 256Mi", CardValue(card, "Requests"));
        Assert.DoesNotContain("256Mi (значение по умолчанию)", CardValue(card, "Requests"));
        Assert.Contains("CPU (доля от ядра): 900m", CardValue(card, "Limits"));
        Assert.Contains("RAM (Mb): значение не найдено", CardValue(card, "Limits"));
        Assert.DoesNotContain("(значение по умолчанию)", CardValue(card, "Limits"));
    }

    [Fact]
    public void FrontendWithoutJ2PreservesVariableTablesAndUsesFolderLinks()
    {
        using var fixture = new Fixture();
        fixture.Write("app-params/.git/config", "[remote \"origin\"]\n url = https://gitlab.example/app-params.git\n");
        fixture.Write("component/.git/config", "[remote \"origin\"]\n url = https://gitlab.example/component.git\n");
        fixture.Write("component/My.Front/package.json", "{}");
        fixture.Write("app-params/application.yml", "DEFAULT_RESOURCES_UI_REQUEST: {cpu: 25m, memory: 50Mi}\n");
        var result = PassportGenerator.Generate(CfHtml.Build(TestTemplate.FullPage),
            new PassportOptions("demo", fixture.PathTo("app-params"), null,
                ServiceFolder: fixture.PathTo("component/My.Front")));
        var document = new HtmlDocument();
        document.LoadHtml(result.HtmlFragment);
        var tables = document.DocumentNode.Descendants("table").ToArray();
        Assert.Equal("my-front", CardValue(tables[0], "Название сервиса в кубере"));
        Assert.Equal("Не используется", CardValue(tables[0], "Шаблон конфига"));
        Assert.Contains("https://gitlab.example/component", CardValue(tables[0], "Репозиторий"));
        Assert.Contains("25m (значение по умолчанию)", CardValue(tables[0], "Requests"));
        var original = new HtmlDocument();
        original.LoadHtml(TestTemplate.FullPage);
        foreach (var index in new[] { 1, 2, 4 })
            Assert.Equal(original.DocumentNode.Descendants("table").ElementAt(index).OuterHtml, tables[index].OuterHtml);
    }

    [Theory]
    [InlineData("service-api", ComponentType.WebApi)]
    [InlineData("service-bff", ComponentType.WebApi)]
    [InlineData("service-grpc", ComponentType.WebApi)]
    [InlineData("service-scheduler", ComponentType.Daemon)]
    [InlineData("service-integration", ComponentType.Daemon)]
    [InlineData("service-webapi-worker", ComponentType.WebApi)]
    public void DeploymentRegexHasPriorityOverMissingAppsettings(string name, ComponentType expected)
    {
        using var fixture = new Fixture();
        fixture.Write($"component/{name}/readme.txt", "No appsettings or J2");
        var folder = fixture.PathTo($"component/{name}");
        Assert.Equal(expected, ServiceComponent.Detect(folder));
        Assert.Throws<InvalidDataException>(() => PassportGenerator.Generate(CfHtml.Build(TestTemplate.FullPage),
            new PassportOptions("demo", fixture.PathTo("app-params"), null, ServiceFolder: folder)));
    }

    [Fact]
    public void AppsettingsSearchIsRecursiveAndUnknownTypeDoesNotUseDefaults()
    {
        using var fixture = new Fixture();
        fixture.Write("component/demo/src/APPSETTINGS.deploy.J2", "");
        fixture.Write("app-params/application.yml", "DEFAULT_RESOURCES_UI_REQUEST: {cpu: 25m}\ncpu_lim: 500m\n");
        Assert.Equal(ComponentType.Unknown, ServiceComponent.Detect(fixture.PathTo("component/demo")));
        var config = YamlConfiguration.Load(fixture.PathTo("app-params"), "demo");
        Assert.Null(config.ResolveResource("cpu_req", ComponentType.Unknown, "REQUEST", "cpu").Value);
        Assert.Equal(new ResourceValue("500m"), config.ResolveResource("cpu_lim", ComponentType.Unknown, "LIMIT", "cpu"));
    }

    [Fact]
    public void ServiceParameterValuesUseFinalYamlAndPreserveMissingClipboardValues()
    {
        using var fixture = new Fixture();
        fixture.Write("app-params/.git/config", "[remote \"origin\"]\n url = https://gitlab.example/app-params.git\n");
        fixture.Write("component/.git/config", "[remote \"origin\"]\n url = https://gitlab.example/component.git\n");
        fixture.Write("app-params/application.yml", "SECTION_LOGGING: INFO\n");
        fixture.Write("app-params/demo/demo.yml", "DB_PRO_PRODUCTS_DATABASE: old_db\nRETRY_COUNT: 0\nFEATURE_ENABLED: false\nEMPTY_SETTING: ''\n");
        fixture.Write("app-params/demo/demo-prod.yml", "DB_PRO_PRODUCTS_DATABASE: 'prod&<database>'\nSECTION_LOGGING: DEBUG\n");
        fixture.Write("component/demo/appsettings.deploy.j2",
            "{{ DB_PRO_PRODUCTS_DATABASE }} {{ SECTION_LOGGING }} {{ RETRY_COUNT }} {{ FEATURE_ENABLED }} {{ EMPTY_SETTING }} {{ UNKNOWN_SETTING }}");
        fixture.Write("component/demo/appsettings.secrets.j2", "{{ PRIVATE_TOKEN }}");

        var template = new HtmlDocument();
        template.LoadHtml(TestTemplate.FullPage);
        var parameters = template.DocumentNode.Descendants("table").ElementAt(4);
        foreach (var name in new[] { "EMPTY_SETTING", "UNKNOWN_SETTING", "PRIVATE_TOKEN" })
            parameters.AppendChild(HtmlNode.CreateNode(
                $"<tr><td></td><td>{name}</td><td></td><td><strong>Из буфера</strong></td></tr>"));
        var clipboard = CfHtml.Build(template.DocumentNode.InnerHtml);
        var merged = TemplateValueMerger.Merge(CfHtml.Build(TestTemplate.FullPage), clipboard);
        foreach (var source in new[] { clipboard, merged })
        {
            var result = PassportGenerator.Generate(source, new PassportOptions("demo",
                fixture.PathTo("app-params"), fixture.PathTo("component/demo/appsettings.deploy.j2")));
            var document = new HtmlDocument();
            document.LoadHtml(result.HtmlFragment);
            var table = document.DocumentNode.Descendants("table").ElementAt(4);
            var database = Cells(FindRow(table, "DB_PRO_PRODUCTS_DATABASE"))[3];
            Assert.Equal("prod&<database>", HtmlEntity.DeEntitize(database.InnerText));
            Assert.DoesNotContain(database.Descendants(), node => node.Name == "database");
            Assert.Equal("Дефолтное значение в базовых параметрах", Cell(FindRow(table, "SECTION_LOGGING"), 3));
            Assert.Equal("0", Cell(FindRow(table, "RETRY_COUNT"), 3));
            Assert.Equal("false", Cell(FindRow(table, "FEATURE_ENABLED"), 3));
            Assert.Equal("", Cell(FindRow(table, "EMPTY_SETTING"), 3));
            Assert.Contains("<strong>Из буфера</strong>", Cells(FindRow(table, "UNKNOWN_SETTING"))[3].InnerHtml);
            Assert.Contains("<strong>Из буфера</strong>", Cells(FindRow(table, "PRIVATE_TOKEN"))[3].InnerHtml);
            Assert.Equal("Да", Cell(FindRow(table, "PRIVATE_TOKEN"), 2));
        }
    }

    [Fact]
    public void DomainApiVariableComesFromYamlChoicesAndOnlyUpdatesItsCardField()
    {
        using var fixture = new Fixture();
        fixture.Write("app-params/.git/config", "[remote \"origin\"]\n url = https://gitlab.example/app-params.git\n");
        fixture.Write("component/.git/config", "[remote \"origin\"]\n url = https://gitlab.example/component.git\n");
        fixture.Write("app-params/application.yml", "SVC_OWN_API: https://base.example\nSVC_OTHER_API: https://other.example\n");
        fixture.Write("app-params/application-prod.yml", "SVC_OWN_API: https://prod.example\n");
        fixture.Write("app-params/demo/demo.yml", "DOMAIN_ADDRESS: https://local.example\n");
        fixture.Write("component/demo/appsettings.deploy.j2", "{{ SVC_OTHER_API }}");
        var choices = ServiceInputs.FindDomainApiVariables(fixture.PathTo("app-params"), "demo");
        Assert.Equal(new[] { "DOMAIN_ADDRESS", "SVC_OTHER_API", "SVC_OWN_API" }, choices);

        const string label = "app-params. Переменная содержащая адрес доменного API";
        var template = new HtmlDocument();
        template.LoadHtml(TestTemplate.FullPage);
        var card = template.DocumentNode.Descendants("table").First();
        Cells(card.Descendants("tr").Single(row => Cell(row, 0) == label))[^1].InnerHtml =
            "<strong>Значение страницы</strong>";
        Cells(card.Descendants("tr").Single(row => Cell(row, 0) == "Для доменного API"))[^1].InnerHtml =
            "Не менять";
        var clipboard = CfHtml.Build(template.DocumentNode.InnerHtml);
        foreach (var source in new[] { clipboard, TemplateValueMerger.Merge(CfHtml.Build(TestTemplate.FullPage), clipboard) })
        {
            var options = new PassportOptions("demo", fixture.PathTo("app-params"),
                fixture.PathTo("component/demo/appsettings.deploy.j2"));
            var unchanged = PassportGenerator.Generate(source, options);
            Assert.Contains("<strong>Значение страницы</strong>", unchanged.HtmlFragment);
            var result = PassportGenerator.Generate(source, options with { DomainApiVariable = "SVC_OWN_API" });
            Assert.Equal("SVC_OWN_API", IdentityValue(result, label));
            Assert.Equal("Не менять", IdentityValue(result, "Для доменного API"));
            Assert.DoesNotContain("https://prod.example", result.HtmlFragment);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void CardValuesMergeAcrossMacroWrappersAndDifferentColumnCounts(bool wrapSource, bool wrapTarget)
    {
        using var fixture = new Fixture();
        fixture.Write("app-params/.git/config", "[remote \"origin\"]\n url = https://gitlab.example/app-params.git\n");
        fixture.Write("component/.git/config", "[remote \"origin\"]\n url = https://gitlab.example/component.git\n");
        fixture.Write("app-params/application.yml", "namespace: runtime\n");
        fixture.Write("component/demo/appsettings.deploy.j2", "");
        static string Wrap(string card) => "<table data-macro-name=\"table-excerpt\" data-macro-parameters=\"name=card\">" +
            "<tr><td><table data-macro-name=\"panel\"><tr><td>" + card + "</td></tr></table></td></tr></table>";
        var source = new HtmlDocument();
        source.LoadHtml(TestTemplate.FullPage);
        var card = source.DocumentNode.Descendants("table").First();
        foreach (var row in card.Descendants("tr").ToArray())
            if (Cells(row).Length == 3 && Cell(row, 1).Length == 0) Cells(row)[1].Remove();
        var team = card.Descendants("tr").Single(row => Cell(row, 0) == "Команда");
        Cells(team)[^1].InnerHtml = "<table data-macro-name=\"panel\" data-macro-parameters=\"title=Team\">" +
            "<tr><td><a href=\"https://example.test/team\">Команда из буфера</a></td></tr></table>";
        var name = card.Descendants("tr").First();
        Cells(name)[^1].InnerHtml = "<strong>Имя из буфера</strong>";
        source.DocumentNode.Descendants("table").Single(table =>
            table.GetAttributeValue("data-macro-name", "") == "table-excerpt").Descendants("td").First().InnerHtml =
            "Детали дополнительной информации";
        var sourceHtml = source.DocumentNode.InnerHtml;
        if (wrapSource) sourceHtml = sourceHtml.Replace(card.OuterHtml, Wrap(card.OuterHtml));
        var targetHtml = wrapTarget
            ? TestTemplate.FullPage.Replace(TestTemplate.CardBlock, Wrap(TestTemplate.CardBlock))
            : TestTemplate.FullPage;
        var merged = TemplateValueMerger.Merge(CfHtml.Build(targetHtml), CfHtml.Build(sourceHtml));
        var result = PassportGenerator.Generate(merged, new PassportOptions("demo", fixture.PathTo("app-params"),
            fixture.PathTo("component/demo/appsettings.deploy.j2")));
        var document = new HtmlDocument();
        document.LoadHtml(result.HtmlFragment);
        var teamRow = document.DocumentNode.Descendants("tr").Single(row => Cell(row, 0) == "Команда");
        Assert.Contains("Команда из буфера", Cells(teamRow)[^1].InnerText);
        Assert.Contains("data-macro-parameters=\"title=Team\"", Cells(teamRow)[^1].InnerHtml);
        Assert.Contains("<strong>Имя из буфера</strong>", result.HtmlFragment);
        Assert.Contains("Детали дополнительной информации", result.HtmlFragment);
        Assert.Single(document.DocumentNode.Descendants("tr").Where(row => Cell(row, 0) == "Команда"));
        var nsRow = document.DocumentNode.Descendants("tr").Single(row => Cell(row, 0) == "Namespace в K8S");
        Assert.Equal("runtime", Cell(nsRow, Cells(nsRow).Length - 1));
    }

    [Fact]
    public void StandaloneAdditionalInformationMacroStillMergesWithoutHeading()
    {
        var merged = TemplateValueMerger.Merge(CfHtml.Build(TestTemplate.FullPage), CfHtml.Build(
            "<table data-macro-name=\"table-excerpt\"><tr><td>Детали отдельного макроса</td></tr></table>"));
        var document = new HtmlDocument();
        document.LoadHtml(CfHtml.ExtractFragment(merged));
        var macro = document.DocumentNode.Descendants("table").Single(table =>
            table.GetAttributeValue("data-macro-name", "") == "table-excerpt");
        Assert.Equal("Детали отдельного макроса", macro.InnerText.Trim());
    }

    [Fact]
    public void EntireCardMergesDespiteDifferentHintsAndResourceSubheadings()
    {
        var template = new HtmlDocument();
        template.LoadHtml(TestTemplate.CardBlock);
        var expected = new Dictionary<string, string>();
        var oldCard = new StringBuilder("<table>");
        foreach (var row in template.DocumentNode.Descendants("tr"))
        {
            var cells = Cells(row);
            var label = cells.Take(cells.Length - 1).Select(cell => cell.InnerText.Trim())
                .First(text => text.Length > 0);
            var value = $"<table data-macro-name=\"panel\" data-macro-parameters=\"title=field-{expected.Count}\">" +
                $"<tr><td><a href=\"https://example.test/{expected.Count}\">Значение {expected.Count}</a></td></tr></table>";
            expected.Add(label, value);
            oldCard.Append($"<tr><th><p><strong>{System.Net.WebUtility.HtmlEncode(label)}:</strong></p>" +
                "<p><em>Подсказка из старой версии страницы</em></p></th>" + $"<td>{value}</td></tr>");
        }
        oldCard.Append("</table>");
        static string Wrap(string card) => "<table data-macro-name=\"table-excerpt\"><tr><td>" + card + "</td></tr></table>";
        var saved = TestTemplate.FullPage.Replace(TestTemplate.CardBlock, Wrap(TestTemplate.CardBlock));
        var merged = TemplateValueMerger.Merge(CfHtml.Build(saved), CfHtml.Build(Wrap(oldCard.ToString())));
        var document = new HtmlDocument();
        document.LoadHtml(CfHtml.ExtractFragment(merged));
        foreach (var row in document.DocumentNode.Descendants("tr").Where(row => Cells(row).Length == 3))
        {
            var cells = Cells(row);
            var label = cells.Take(cells.Length - 1).Select(cell => cell.InnerText.Trim()).FirstOrDefault(text => text.Length > 0);
            if (label is null || !expected.Remove(label, out var value)) continue;
            Assert.Equal(value, cells[^1].InnerHtml);
        }
        Assert.Empty(expected);
    }

    private static string IdentityValue(PassportResult result, string label)
    {
        var document = new HtmlDocument();
        document.LoadHtml(result.HtmlFragment);
        return Cell(document.DocumentNode.Descendants("table").First().Descendants("tr")
            .Single(row => Cell(row, 0) == label), 2);
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
