using ServiceAutoPassport.Core;

namespace ServiceAutoPassport.Tests;

public sealed class NetworkParameterCatalogTests
{
    [Fact]
    public void ExcelUpdatesExistingKeysAndKeepsAnExplicitEmptyBlock()
    {
        var settings = new NetworkMapSettings();
        settings.Targets["parameter:DB_HOST|PROXY"] = new() { Label = "БД" };
        settings.Targets["literal:service:Cache.Host"] = new() { Label = "Кэш", SourceService = "Worker" };
        var edits = NetworkParameterCatalog.ParseExcel(
            "Найденный параметр\tНазвание элемента\tБлок\r\n" +
            "PROXY, DB_HOST\t\"БД \"\"Основная\"\"\"\t\r\n" +
            "Без переменной — Worker · Cache.Host\tКэш\tStorage\r\n" +
            "NEW_HOST\tНовый узел\tk8s", settings.Targets);

        Assert.Equal(new NetworkParameterEdit("БД \"Основная\"", ""), edits["parameter:DB_HOST|PROXY"]);
        Assert.Equal(new NetworkParameterEdit("Кэш", "Storage"), edits["literal:service:Cache.Host"]);
        Assert.Equal("Новый узел", edits["parameter:NEW_HOST"].Label);
        Assert.Equal("БД", settings.Targets["parameter:DB_HOST|PROXY"].Label);
        Assert.DoesNotContain("parameter:NEW_HOST", settings.Targets.Keys);
    }

    [Theory]
    [InlineData("DB, PROXY\tБаза\tk8s\n\nPROXY, DB\tПовтор\tk8s", "Строка 3", "уже указан")]
    [InlineData("DB\tБаза", "Строка 1", "три колонки")]
    [InlineData("DB\t\tk8s", "Строка 1", "название элемента")]
    [InlineData("bad key\tБаза\tk8s", "Строка 1", "J2-переменную")]
    public void ExcelReportsInvalidRow(string text, string row, string reason)
    {
        var error = Assert.Throws<InvalidDataException>(() =>
            NetworkParameterCatalog.ParseExcel(text, new Dictionary<string, NetworkTargetSettings>()));
        Assert.Contains(row, error.Message);
        Assert.Contains(reason, error.Message);
    }

    [Fact]
    public void ExcelKeepsUpdatesOrClearsParentsAccordingToTheImportedColumns()
    {
        var settings = new NetworkMapSettings();
        var target = new NetworkTargetSettings { Label = "Old", Block = "Kafka", ParentBlock = "Saved" };
        settings.Targets["parameter:DB_HOST"] = target;
        var draft = new Dictionary<string, NetworkParameterEdit>
        {
            ["parameter:DB_HOST"] = new("Draft", "Kafka") { ParentBlock = "Draft parent" }
        };
        var legacy = NetworkParameterCatalog.ParseExcel("DB_HOST\tDB\t kafka ", settings.Targets, draft);
        Assert.Equal("Draft parent", legacy["parameter:DB_HOST"].ParentBlock);
        var moved = NetworkParameterCatalog.ParseExcel("DB_HOST\tDB\tStorage", settings.Targets, draft);
        Assert.Null(moved["parameter:DB_HOST"].ParentBlock);
        var updated = NetworkParameterCatalog.ParseExcel("Параметр\tНазвание элемента\tБлок\tБлок-родитель\n" +
            "DB_HOST\tDB\tKafka\t k8s \nNEW_HOST\tNew\t\tk8s", settings.Targets, draft);
        Assert.Equal("k8s", updated["parameter:DB_HOST"].ParentBlock);
        Assert.Equal(new NetworkParameterEdit("New", "") { ParentBlock = "k8s" }, updated["parameter:NEW_HOST"]);
        NetworkParameterCatalog.SaveEdits(settings, updated, () => { });
        Assert.Same(target, settings.Targets["parameter:DB_HOST"]);
        Assert.Equal("k8s", target.ParentBlock);

        var cleared = NetworkParameterCatalog.ParseExcel("DB_HOST\tDB\tKafka\t", settings.Targets, draft);
        NetworkParameterCatalog.SaveEdits(settings, cleared, () => { });
        Assert.Equal("", target.ParentBlock);
    }

    [Fact]
    public void FailedSaveLeavesCurrentAnalysisAndCatalogUnchanged()
    {
        var settings = new NetworkMapSettings();
        var existing = new NetworkTargetSettings { Label = "База" };
        settings.Targets["parameter:DB"] = existing;
        var edits = NetworkParameterCatalog.ParseExcel("DB\tНовое имя\t\nOTHER\tДругой узел\tk8s", settings.Targets);

        Assert.Throws<IOException>(() => NetworkParameterCatalog.SaveEdits(settings, edits, () => throw new IOException()));
        Assert.Same(existing, Assert.Single(settings.Targets).Value);
        Assert.Equal("База", existing.Label);
        Assert.Equal("k8s", existing.Block);
        Assert.Throws<IOException>(() => NetworkParameterCatalog.SaveCatalog(settings,
            new Dictionary<string, NetworkParameterEdit>(), null, () => throw new IOException()));
        Assert.Same(existing, Assert.Single(settings.Targets).Value);
    }
}
