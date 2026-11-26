using System.Text.Json;
using ServiceAutoPassport.Core;

namespace ServiceAutoPassport.Tests;

public sealed class SettingsTableImportTests
{
    [Fact]
    public void KeyDescriptionsKeepTextAndExplicitHtmlFlags()
    {
        var entries = new Dictionary<string, KeyDescriptionEntry>
        {
            ["RICH_DESCRIPTION"] = new("<p>Первая; \"строка\"</p>\r\n\r\n<p>Вторая\tстрока</p>", true),
            ["PLAIN_DESCRIPTION"] = new("Пример <b>разметки</b>", false)
        };
        var export = KeyDescriptionImport.Export(entries);
        Assert.StartsWith("Ключ из J2;Описание;HTML\r\n", export);
        var imported = KeyDescriptionImport.ParseEntries(export);
        Assert.Equal(entries.ToArray(), imported.ToArray());
        Assert.True(KeyDescriptionImport.ParseEntries("KEY\t<b>Описание</b>")["KEY"].IsHtml);
    }

    [Theory]
    [InlineData("Ключ;Описание\r\nKEY;\"Описание; с разделителем\"", "Описание; с разделителем")]
    [InlineData("KEY\tОписание; с разделителем", "Описание; с разделителем")]
    [InlineData("KEY Описание; с разделителем", "Описание; с разделителем")]
    public void ImportsKeyDescriptionPairsWithEitherSeparator(string text, string description)
    {
        Assert.Equal(description, KeyDescriptionImport.ParseEntries(text)["KEY"].Description);
    }

    [Fact]
    public void ImportsEarlierTabSeparatedExports()
    {
        var descriptions = KeyDescriptionImport.ParseEntries("Ключ из J2\tОписание\tHTML\r\nKEY\t<b>Text</b>\tНет");
        Assert.False(descriptions["KEY"].IsHtml);
        var networks = NetworkParameterCatalog.ParseEntries(
            "Найденный параметр\tНазвание элемента\tБлок\tБлок-родитель\tАвтоназвание\tАвтоблок\tРодитель задан\tКлюч подключения\tФайл сервиса\tИмя сервиса\tПеременные пути\r\n" +
            "API_HOST\tУдалённый; API\t\t\tДа\tНет\tДа\t\t\t\t", new Dictionary<string, NetworkTargetSettings>(),
            new Dictionary<string, NetworkParameterEdit>(), []);
        Assert.Equal(new NetworkParameterEdit("Удалённый; API", "") { LabelIsAutomatic = true, ParentBlock = "" },
            networks["parameter:API_HOST"]);
    }

    [Theory]
    [InlineData("API_HOST;\"Удалённый; API\";;Родитель")]
    [InlineData("API_HOST\tУдалённый; API\t\tРодитель")]
    public void ImportsShortNetworkTablesWithEitherSeparator(string text)
    {
        var edits = NetworkParameterCatalog.ParseExcel(text, new Dictionary<string, NetworkTargetSettings>());
        Assert.Equal(new NetworkParameterEdit("Удалённый; API", "") { ParentBlock = "Родитель" }, edits["parameter:API_HOST"]);
    }

    [Theory]
    [InlineData(true, 2)]
    [InlineData(false, 1)]
    [InlineData(null, 0)]
    public void SelectsChangesWithOneDecisionForAllMatches(bool? decision, int expected)
    {
        var entries = new Dictionary<string, int> { ["Existing"] = 42, ["New"] = 7 };
        var calls = 0;
        var selected = SettingsTableImport.SelectChanges(entries, key => key == "Existing", count =>
        {
            Assert.Equal(1, count);
            calls++;
            return decision;
        });
        Assert.Equal(1, calls);
        if (decision is null)
        {
            Assert.Null(selected);
            return;
        }
        Assert.Equal(expected, selected!.Count);
        Assert.Equal(7, selected["New"]);
        Assert.Equal(decision.Value, selected.ContainsKey("Existing"));
    }

    [Fact]
    public void NewEntriesDoNotTriggerAConflictPrompt()
    {
        var entries = new Dictionary<string, int> { ["New"] = 1 };
        Assert.Same(entries, SettingsTableImport.SelectChanges(entries, _ => false, _ => throw new InvalidOperationException()));
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, true)]
    public void NetworkRowsCanMoveToAnotherCheckout(bool importBeforeAnalysis, bool legacySource, bool ambiguous)
    {
        var directory = Directory.CreateTempSubdirectory("network-settings-transfer-").FullName;
        try
        {
            var root = Path.Combine(directory, "params");
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "application.yml"), "API_URL: suffix\nOTHER_HOST: other.example\n");
            var source = Settings("first-checkout", "Было");
            var sourceConnection = NetworkMapGenerator.Analyze(root, source).Connections.Single(c => c.Path == "Fixed.Url");
            var literalKey = sourceConnection.TargetKey;
            var literal = source.Targets[literalKey];
            if (legacySource) literal.SourceJ2 = null;
            var draft = new Dictionary<string, NetworkParameterEdit>
            {
                [literalKey] = new("Фиксированный \"URL\"", "") { ParentBlock = "k8s", LabelIsAutomatic = true },
                ["parameter:OTHER_HOST"] = new("Другой\tхост", "") { ParentBlock = "", BlockIsAutomatic = true }
            };
            var export = NetworkParameterCatalog.Export(draft, source.Targets, legacySource ? [] : source.Services);
            Assert.StartsWith("Найденный параметр;Название элемента;Блок;Блок-родитель;", export);

            var destination = Settings("second-checkout", legacySource ? "Было" : "Стало");
            var analysis = importBeforeAnalysis ? null : NetworkMapGenerator.Analyze(root, destination);
            var edits = NetworkParameterCatalog.ParseEntries(export, destination.Targets,
                new Dictionary<string, NetworkParameterEdit>(), destination.Services);

            Assert.Equal(importBeforeAnalysis, edits.ContainsKey(literalKey));
            var address = edits["parameter:OTHER_HOST"];
            Assert.Equal(draft["parameter:OTHER_HOST"], address);
            NetworkParameterCatalog.SaveCatalog(destination, edits, analysis, () => { });
            destination = JsonSerializer.Deserialize<NetworkMapSettings>(JsonSerializer.Serialize(destination))!;

            if (ambiguous) destination.Services.Add(Settings("third-checkout", "Клон").Services.Single());
            var restoredAnalysis = NetworkMapGenerator.Analyze(root, destination);
            if (ambiguous)
            {
                Assert.Contains(restoredAnalysis.Diagnostics, message => message.Contains("найдено несколько подключений"));
                Assert.True(destination.Targets[literalKey].AwaitingConnection);
                Assert.DoesNotContain(restoredAnalysis.Connections, call => call.ExternalTarget.Label == draft[literalKey].Label);
                return;
            }

            var restored = restoredAnalysis.Connections.Single(c => c.Path == "Fixed.Url");
            Assert.NotEqual(literalKey, restored.TargetKey);
            Assert.DoesNotContain(literalKey, destination.Targets.Keys);
            Assert.Equal(draft[literalKey].Label, restored.ExternalTarget.Label);
            Assert.Equal(draft[literalKey].ParentBlock, restored.ExternalTarget.ParentBlock);
            Assert.True(restored.ExternalTarget.LabelIsAutomatic);
            Assert.Equal("", restored.ExternalTarget.Block);
            Assert.Equal("", destination.Targets["parameter:OTHER_HOST"].ParentBlock);

            NetworkMapSettings Settings(string checkout, string label)
            {
                var folder = Path.Combine(directory, checkout, "My.Worker");
                Directory.CreateDirectory(folder);
                var template = Path.Combine(folder, "appsettings.deploy.j2");
                File.WriteAllText(template, """
                    {"Fixed":{"Url":"https://fixed.example/{{ API_URL }}"},"Other":{"Host":"{{ OTHER_HOST }}"}}
                    """);
                return new NetworkMapSettings { Services = [new() { J2File = template, Label = label }], Environments = ["prod"] };
            }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
