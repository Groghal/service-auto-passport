using HtmlAgilityPack;

namespace ServiceAutoPassport.Core;

public sealed class PassportVariableTrace
{
    private readonly string _mainFile;
    private readonly string? _secretFile;
    private readonly IReadOnlyList<string> _main;
    private readonly IReadOnlyList<string> _secrets;
    private readonly string _source;
    private readonly string _result;

    internal PassportVariableTrace(string mainFile, string? secretFile, IReadOnlyList<string> main,
        IReadOnlyList<string> secrets, string source, string result)
    {
        _mainFile = mainFile;
        _secretFile = secretFile;
        _main = main;
        _secrets = secrets;
        _source = source;
        _result = result;
    }

    public string Describe(string variable)
    {
        var name = variable.Trim();
        var sourceTables = Tables(_source);
        var resultTables = Tables(_result);
        var accessBefore = AccessRows(sourceTables);
        var accessAfter = AccessRows(resultTables);
        var parameters = TemplateTables.Find(resultTables, "Описание", "Название переменной");
        var services = TemplateTables.Find(resultTables, "Название сервиса", "Переменная адреса компонента");

        return string.Join(Environment.NewLine, new[]
        {
            $"Переменная: {name}",
            $"Основной J2: {_mainFile}",
            $"Файл секретов: {_secretFile ?? "не найден"}",
            $"В основном J2: {YesNo(_main.Contains(name))}",
            $"В файле секретов: {YesNo(_secrets.Contains(name))}",
            $"Объекты доступа в шаблоне перед заполнением: {accessBefore}",
            $"Объекты доступа в готовом HTML: {accessAfter}",
            $"В таблице параметров готового HTML: {NamedRow(parameters)}",
            $"В таблице микросервисов готового HTML: {NamedRow(services)}"
        });

        string AccessRows(HtmlNode[] tables)
        {
            var table = TemplateTables.Find(tables, "Объект доступа", "Ключи в Vault");
            if (table is null) return "таблица не найдена";
            var matches = DataRows(table).Where(cells => cells.Skip(1).Take(2)
                .Any(cell => PassportTemplateRenderer.AccessVariableNames(cell).Contains(name)))
                .Select(cells => TemplateTables.Normalize(cells[0].InnerText)).ToArray();
            return matches.Length == 0 ? "нет" : string.Join(", ", matches);
        }

        string NamedRow(HtmlNode? table) => table is null ? "таблица не найдена" :
            YesNo(DataRows(table).Any(cells => cells.Length > 1 && TemplateTables.Normalize(cells[1].InnerText) == name));
    }

    private static HtmlNode[] Tables(string fragment)
    {
        var document = new HtmlDocument();
        document.LoadHtml(fragment);
        return document.DocumentNode.Descendants("table").ToArray();
    }

    private static IEnumerable<HtmlNode[]> DataRows(HtmlNode table) => table.Descendants("tr")
        .Where(row => row.Ancestors("table").FirstOrDefault() == table).Skip(1)
        .Select(row => row.ChildNodes.Where(node => node.Name is "td" or "th").ToArray());

    private static string YesNo(bool value) => value ? "да" : "нет";
}
