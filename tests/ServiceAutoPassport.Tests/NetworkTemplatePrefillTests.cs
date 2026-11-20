using System.Text.Json;
using ServiceAutoPassport.Core;

namespace ServiceAutoPassport.Tests;

public sealed class NetworkTemplatePrefillTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "network-prefill-" + Guid.NewGuid().ToString("N"));

    private static string Template(string headings, string rows) =>
        """
        <table data-macro-name="table-joiner" data-macro-body-type="RICH_TEXT"><tbody><tr><td class="wysiwyg-macro-body">
        <table><tbody>
        """ + headings + rows + "</tbody></table></td></tr></tbody></table>";

    private NetworkAnalysis Analyze(NetworkMapSettings settings, string name, string json)
    {
        Directory.CreateDirectory(_root);
        var j2 = Path.Combine(_root, name + ".j2");
        File.WriteAllText(j2, json);
        settings.Services = [new() { J2File = j2, Label = name }];
        settings.Environments = ["test"];
        return NetworkMapGenerator.Analyze(_root, settings);
    }

    [Fact]
    public void PrefillAddsCanonicalNamesAcrossMapsAndKeepsLegacyAndManualFields()
    {
        // The old save has no provenance flags.
        var settings = JsonSerializer.Deserialize<NetworkMapSettings>("""
            {"Targets":{"parameter:LEGACY_HOST":{"Label":"Исторический URL","Block":""}}}
            """)!;
        var analysis = Analyze(settings, "First", """
            {"Pay":{"Host":"{{ PAY_HOST }}"},"Legacy":{"Host":"{{ LEGACY_HOST }}"}}
            """);
        var pay = settings.Targets["parameter:PAY_HOST"];
        Assert.True(pay.LabelIsAutomatic);
        Assert.True(pay.BlockIsAutomatic);

        var draft = new Dictionary<string, NetworkParameterEdit>(StringComparer.Ordinal);
        draft["parameter:PAY_HOST"] = new(pay.Label, "Вручную") { LabelIsAutomatic = true };
        draft["parameter:LEGACY_HOST"] = new("Исторический URL", "");
        var template = Template("<tr><th>Элемент</th><th>Адресный параметр</th><th>Блок</th></tr>", """
            <tr><td>Платежи &amp; отчёты</td><td>PAY_HOST</td><td>Из шаблона</td></tr>
            <tr><td>Другое</td><td>LEGACY_HOST</td><td>Из шаблона</td></tr>
            <tr><td>Новый URL</td><td>NEW_HOST</td><td></td></tr>
            """);
        var imported = NetworkTemplatePrefill.Read(template, analysis.Connections, draft);
        Assert.Equal(new NetworkParameterEdit("Платежи & отчёты", "Вручную"), imported.Edits["parameter:PAY_HOST"]);
        Assert.Equal(new NetworkParameterEdit("Новый URL", ""), imported.Edits["parameter:NEW_HOST"]);
        Assert.Single(imported.Skipped);
        Assert.Equal("Pay", pay.Label); // The preview can be cancelled without affecting the current analysis.
        Assert.DoesNotContain("parameter:NEW_HOST", settings.Targets.Keys);

        string? saved = null;
        NetworkParameterCatalog.SaveEdits(settings, imported.Edits, () => saved = JsonSerializer.Serialize(settings));
        Assert.Equal("Платежи & отчёты", pay.Label);
        Assert.False(pay.LabelIsAutomatic);
        var second = Analyze(JsonSerializer.Deserialize<NetworkMapSettings>(saved!)!, "Second", """
            {"OtherwiseNamed":{"Host":"{{ NEW_HOST }}"}}
            """);
        var other = Assert.Single(second.Connections).ExternalTarget;
        Assert.Equal("Новый URL", other.Label);
        Assert.Equal("", other.Block);
        var page = NetworkMapGenerator.RenderTable(NetworkTableTemplates.BasicHtml, second);
        Assert.Contains("Новый URL", NetworkMapGenerator.CreateDiagram(second));
        Assert.Contains("Новый URL", page.HtmlFragment);
        var roundTrip = NetworkTemplatePrefill.Read(CfHtml.Build(page.HtmlFragment), [], new Dictionary<string, NetworkParameterEdit>());
        Assert.Equal(new NetworkParameterEdit(other.Label, other.Block), roundTrip.Edits["parameter:NEW_HOST"]);
    }

    [Fact]
    public void ParentBlockRoundTripsFromGeneratedTableAndPreservesManualValues()
    {
        var settings = new NetworkMapSettings();
        var analysis = Analyze(settings, "Gateway", """
            {"Kafka":{"Host":"{{ KAFKA_HOST }}"},"Direct":{"Host":"{{ DIRECT_HOST }}"}}
            """);
        Assert.Null(Assert.Single(analysis.Services).ParentBlock);
        var service = analysis.Services[0];
        service.Block = " Pipeline ";
        service.ParentBlock = "k8s";
        settings.Targets["parameter:KAFKA_HOST"].Block = "Pipeline";
        var direct = settings.Targets["parameter:DIRECT_HOST"];
        direct.Block = "";
        direct.ParentBlock = "k8s";
        var page = NetworkMapGenerator.RenderTable(NetworkTableTemplates.BasicHtml, analysis);
        var edits = NetworkTemplatePrefill.Read(page.HtmlFragment, analysis.Connections, new Dictionary<string, NetworkParameterEdit>()).Edits;
        Assert.Equal(new NetworkParameterEdit("Kafka", "Pipeline") { ParentBlock = "k8s" }, edits["parameter:KAFKA_HOST"]);
        Assert.Equal(new NetworkParameterEdit("Direct", "") { ParentBlock = "k8s" }, edits["parameter:DIRECT_HOST"]);

        var draft = new Dictionary<string, NetworkParameterEdit>
        {
            ["parameter:KAFKA_HOST"] = edits["parameter:KAFKA_HOST"] with { ParentBlock = "", LabelIsAutomatic = true },
            ["parameter:DIRECT_HOST"] = new("Manual", "Other") { ParentBlock = "Manual parent", LabelIsAutomatic = true }
        };
        var imported = NetworkTemplatePrefill.Read(page.HtmlFragment, analysis.Connections, draft).Edits;
        Assert.Equal("", imported["parameter:KAFKA_HOST"].ParentBlock);
        Assert.Equal("Manual parent", imported["parameter:DIRECT_HOST"].ParentBlock);

        var legacy = Template("<tr><th>Элемент</th><th>Адресный параметр</th><th>Блок</th></tr>",
            "<tr><td>Previous</td><td>DIRECT_HOST</td><td>Old block</td></tr>");
        var restored = NetworkTemplatePrefill.Read(legacy, analysis.Connections, draft).Edits["parameter:DIRECT_HOST"];
        Assert.Equal("Previous", restored.Label);
        Assert.Equal("Other", restored.Block);
        Assert.Equal("Manual parent", restored.ParentBlock);
    }

    [Fact]
    public void DeletedParameterCanBeReimportedWithoutRepeatingAnalysis()
    {
        var settings = new NetworkMapSettings();
        var analysis = Analyze(settings, "Gateway", """{"Payments":{"Host":"{{ PAY_HOST }}"}}""");
        var connection = Assert.Single(analysis.Connections);
        var target = connection.ExternalTarget;
        target.Label = "Старое имя";
        target.Block = "Старый блок";
        target.LabelIsAutomatic = target.BlockIsAutomatic = false;
        var draft = new Dictionary<string, NetworkParameterEdit>
        {
            [connection.TargetKey] = new(target.Label, target.Block)
        };
        var template = Template("<tr><th>Элемент</th><th>Адресный параметр</th><th>Блок</th></tr>", """
            <tr><td>Имя из шаблона</td><td>PAY_HOST</td><td></td></tr>
            """);
        Assert.Empty(NetworkTemplatePrefill.Read(template, analysis.Connections, draft).Edits);

        draft.Remove(connection.TargetKey);
        var preview = NetworkTemplatePrefill.ReadFirstWithRows(
            [("Текущий шаблон", NetworkTableTemplates.BasicHtml), ("Сохранённый шаблон", template)],
            analysis.Connections, draft);
        Assert.Equal("Сохранённый шаблон", preview.Source);
        Assert.Equal("Имя из шаблона", Assert.Single(preview.Edits).Value.Label);
        Assert.Equal("Старое имя", settings.Targets[connection.TargetKey].Label);

        NetworkParameterCatalog.SaveCatalog(settings, draft, analysis, () => Assert.Empty(settings.Targets));
        Assert.Empty(settings.Targets);
        Assert.Equal("Payments", target.Label);
        Assert.Equal("k8s", target.Block);
        Assert.True(target.LabelIsAutomatic);
        Assert.Same(connection.Choice, settings.Choices[connection.Key]);

        NetworkParameterCatalog.SaveCatalog(settings, preview.Edits, analysis, () => { });
        Assert.Same(target, settings.Targets[connection.TargetKey]);
        Assert.Equal("Имя из шаблона", target.Label);
        Assert.Equal("", target.Block);
        var result = NetworkMapGenerator.RenderTable(NetworkTableTemplates.BasicHtml, analysis);
        Assert.Contains("Имя из шаблона", NetworkMapGenerator.CreateDiagram(analysis));
        Assert.Contains("Имя из шаблона", result.HtmlFragment);
    }

    [Fact]
    public void EmptyAndUnusableTemplatesFallBackToTheCopiedConfluenceTable()
    {
        var empty = Template("<tr><th>Элемент</th></tr>", "<tr><td><p><br /></p></td></tr>");
        var populated = Template("<tr><th>Элемент</th><th>Адресный параметр</th></tr>",
            "<tr><td>Кэш</td><td>CACHE_HOST</td></tr>");
        var draft = new Dictionary<string, NetworkParameterEdit>();

        var result = NetworkTemplatePrefill.ReadFirstWithRows(
            [("Текущий шаблон", empty), ("Сохранённый шаблон", "<p>Без сетевой таблицы</p>"),
                ("Буфер обмена", CfHtml.Build(populated))], [], draft);
        Assert.Equal("Буфер обмена", result.Source);
        Assert.Equal("Кэш", result.Edits["parameter:CACHE_HOST"].Label);

        var withoutRows = NetworkTemplatePrefill.ReadFirstWithRows([("Текущий шаблон", empty), ("Буфер обмена", null)], [], draft);
        Assert.Empty(withoutRows.Edits);
        Assert.Empty(withoutRows.Skipped);
    }

    [Fact]
    public void PopulatedCurrentTemplateRemainsTheSourceEvenWhenItsRowsAreProtected()
    {
        var populated = Template("<tr><th>Элемент</th><th>Адресный параметр</th></tr>",
            "<tr><td>Кэш</td><td>CACHE_HOST</td></tr>");
        IEnumerable<(string Name, string? Html)> Sources()
        {
            yield return ("Текущий шаблон", populated);
            throw new InvalidOperationException("A populated current template must not trigger a clipboard read.");
        }

        var result = NetworkTemplatePrefill.ReadFirstWithRows(Sources(), [],
            new Dictionary<string, NetworkParameterEdit> { ["parameter:CACHE_HOST"] = new("Ручное имя", "k8s") });
        Assert.Equal("Текущий шаблон", result.Source);
        Assert.Empty(result.Edits);
        Assert.Single(result.Skipped);
    }

    [Fact]
    public void LegacyRowsUseAddressAndPortMappingsAndReportAmbiguity()
    {
        var analysis = Analyze(new NetworkMapSettings(), "Db", """
            {"Db":{"Host":"{{ DB_HOST }}","Port":"{{ DB_PORT }}"}}
            """);
        var template = Template("<tr><th>Параметры</th><th>Элемент</th></tr>", """
            <tr><td>DB_PORT, DB_HOST</td><td>База</td></tr>
            <tr><td>A_HOST, B_PORT</td><td>Неоднозначный</td></tr>
            <tr><td>DB_PORT</td><td>Только порт</td></tr>
            <tr><td>ANOTHER_HOST</td><td>Имя 1</td></tr>
            <tr><td>ANOTHER_HOST</td><td>Имя 2</td></tr>
            """);
        var edits = NetworkTemplatePrefill.Read(template, analysis.Connections, new Dictionary<string, NetworkParameterEdit>());
        var edit = Assert.Single(edits.Edits);
        Assert.Equal("parameter:DB_HOST", edit.Key);
        Assert.Equal("База", edit.Value.Label);
        Assert.Equal("k8s", edit.Value.Block);
        Assert.True(edit.Value.BlockIsAutomatic);
        Assert.Equal(4, edits.Skipped.Count);
        var empty = NetworkTemplatePrefill.Read(NetworkTableTemplates.BasicHtml, [], new Dictionary<string, NetworkParameterEdit>());
        Assert.Empty(empty.Edits);
        Assert.Empty(empty.Skipped);
    }

    [Theory]
    [InlineData("PAYMENTS_HOST_URL", "PAYMENTS")]
    [InlineData("payments_url_host", "payments")]
    [InlineData("PaymentsUrl", "Payments")]
    [InlineData("PaymentsuRL_HoSt", "PaymentsuRL")]
    [InlineData("PaymentHostURL", "Payment")]
    [InlineData("Ghost", "Ghost")]
    public void AutomaticNamesDropSuffixesButKeepJ2Keys(string parameter, string name)
    {
        var analysis = Analyze(new NetworkMapSettings(), "Gateway", "{\"Host\":\"{{ " + parameter + " }}\"}");
        var connection = Assert.Single(analysis.Connections);
        Assert.Equal(name, connection.ExternalTarget.Label);
        Assert.Equal(parameter, Assert.Single(connection.HostKeys));
    }

    [Fact]
    public void PathBasedAutomaticNameDropsCamelCaseSuffix()
    {
        var analysis = Analyze(new NetworkMapSettings(), "Gateway", """{"ОплатаUrl":{"Host":"{{ URI_HOST }}"}}""");
        Assert.Equal("Оплата", Assert.Single(analysis.Connections).ExternalTarget.Label);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
