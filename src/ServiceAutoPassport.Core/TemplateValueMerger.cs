using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace ServiceAutoPassport.Core;

public static class TemplateValueMerger
{
    public static void ValidateFullTemplate(string clipboardHtml)
    {
        var document = Parse(clipboardHtml);
        var tables = document.DocumentNode.Descendants("table").ToArray();
        if (FindTable(tables, "Название сервиса", "Команда", anyRow: true) is null ||
            FindTable(tables, "Название сервиса", "Переменная адреса компонента") is null ||
            FindTable(tables, "Объект доступа", "Ключи в Vault") is null ||
            FindTable(tables, "Описание", "Название переменной") is null ||
            !document.DocumentNode.Descendants().Any(node =>
                node.Name is "h1" or "h2" or "h3" or "h4" or "h5" or "h6" &&
                Normalize(node.InnerText).Contains("Дополнительная информация", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException("В буфере нет полной страницы шаблона Confluence.");
        }
    }

    public static string Merge(string savedTemplateHtml, string clipboardHtml)
    {
        ValidateFullTemplate(savedTemplateHtml);
        var target = Parse(savedTemplateHtml);
        var source = Parse(clipboardHtml);
        var targetTables = target.DocumentNode.Descendants("table").ToArray();
        var sourceTables = source.DocumentNode.Descendants("table").ToArray();

        MergeCard(FindTable(targetTables, "Название сервиса", "Команда", anyRow: true)!,
            FindTable(sourceTables, "Название сервиса", "Команда", anyRow: true));
        MergeServices(FindTable(targetTables, "Название сервиса", "Переменная адреса компонента")!,
            FindTable(sourceTables, "Название сервиса", "Переменная адреса компонента"));
        MergeAccess(FindTable(targetTables, "Объект доступа", "Ключи в Vault")!,
            FindTable(sourceTables, "Объект доступа", "Ключи в Vault"));
        MergeParameters(FindTable(targetTables, "Описание", "Название переменной")!,
            FindTable(sourceTables, "Описание", "Название переменной"));
        MergeParagraphsByHeading(target, source);
        MergeAdditionalInformation(target, source);

        return CfHtml.Build(target.DocumentNode.InnerHtml);
    }

    private static HtmlDocument Parse(string clipboardHtml)
    {
        var document = new HtmlDocument();
        document.LoadHtml(CfHtml.ExtractFragment(clipboardHtml));
        return document;
    }

    private static void MergeCard(HtmlNode target, HtmlNode? source)
    {
        if (source is null) return;
        var sourceRows = Rows(source).Where(row => CardRowKey(row).Length > 0)
            .GroupBy(CardRowKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        foreach (var row in Rows(target))
        {
            if (!sourceRows.TryGetValue(CardRowKey(row), out var sourceRow)) continue;
            var cells = Cells(row);
            var sourceCells = Cells(sourceRow);
            if (cells.Length < 2 || sourceCells.Length < 2 || !HasContent(sourceCells[^1])) continue;
            cells[^1].InnerHtml = sourceCells[^1].InnerHtml;
        }
    }

    private static string CardRowKey(HtmlNode row)
    {
        var cells = Cells(row);
        return cells.Take(Math.Max(0, cells.Length - 1)).Select(CardLabel)
            .FirstOrDefault(text => text.Length > 0) ?? "";
    }

    private static string CardLabel(HtmlNode cell)
    {
        var label = cell.CloneNode(true);
        foreach (var hint in label.Descendants().Where(node => node.Name is "em" or "i" or "table").ToArray())
            hint.Remove();
        var text = label.Descendants("p").Select(paragraph => Normalize(paragraph.InnerText))
            .FirstOrDefault(text => text.Length > 0) ?? Normalize(label.InnerText);
        return text.TrimEnd(' ', ':', '.');
    }

    private static void MergeServices(HtmlNode target, HtmlNode? source)
    {
        if (source is null) return;
        var sourceRows = Rows(source).Skip(1).ToArray();
        if (sourceRows.Length == 0) return;
        var targetRows = Rows(target).ToArray();
        var parent = targetRows[0].ParentNode!;
        foreach (var row in targetRows.Skip(1)) row.Remove();
        foreach (var row in sourceRows) parent.AppendChild(row.CloneNode(true));
    }

    private static void MergeAccess(HtmlNode target, HtmlNode? source)
    {
        if (source is null) return;
        var sourceRows = Rows(source).Skip(1)
            .Where(row => Cells(row).Length >= 4)
            .GroupBy(row => Normalize(Cells(row)[0].InnerText), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        foreach (var row in Rows(target).Skip(1))
        {
            var cells = Cells(row);
            if (cells.Length < 4 || !sourceRows.TryGetValue(Normalize(cells[0].InnerText), out var sourceRow)) continue;
            var sourceCells = Cells(sourceRow);
            for (var index = 3; index < Math.Min(cells.Length, sourceCells.Length); index++)
            {
                if (HasContent(sourceCells[index])) cells[index].InnerHtml = sourceCells[index].InnerHtml;
            }
        }
    }

    private static void MergeParameters(HtmlNode target, HtmlNode? source)
    {
        if (source is null) return;
        var targetRows = Rows(target).ToArray();
        var parent = targetRows[0].ParentNode!;
        var existing = targetRows.Skip(1)
            .Where(row => Cells(row).Length >= 4)
            .Select(row => (Name: VariableName(Cells(row)[1]), Row: row))
            .Where(entry => entry.Name is not null)
            .GroupBy(entry => entry.Name!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Row, StringComparer.Ordinal);
        foreach (var row in Rows(source).Skip(1))
        {
            var cells = Cells(row);
            if (cells.Length < 4 || VariableName(cells[1]) is not { } name) continue;
            if (!existing.TryGetValue(name, out var targetRow))
            {
                targetRow = row.CloneNode(true);
                parent.AppendChild(targetRow);
                existing.Add(name, targetRow);
                continue;
            }

            var targetCells = Cells(targetRow);
            if (HasContent(cells[0])) targetCells[0].InnerHtml = cells[0].InnerHtml;
            if (HasContent(cells[3])) targetCells[3].InnerHtml = cells[3].InnerHtml;
        }
    }

    private static void MergeAdditionalInformation(HtmlDocument target, HtmlDocument source)
    {
        var sourceMacro = AdditionalInformationMacro(source);
        var targetMacro = AdditionalInformationMacro(target);
        var sourceBody = sourceMacro?.Descendants("td").FirstOrDefault();
        var targetBody = targetMacro?.Descendants("td").FirstOrDefault();
        if (sourceBody is not null && targetBody is not null && HasContent(sourceBody))
            targetBody.InnerHtml = sourceBody.InnerHtml;
    }

    private static HtmlNode? AdditionalInformationMacro(HtmlDocument document)
    {
        var inSection = false;
        foreach (var node in document.DocumentNode.Descendants())
        {
            if (IsHeading(node))
            {
                if (inSection) return null;
                inSection = Normalize(node.InnerText).Contains("Дополнительная информация", StringComparison.OrdinalIgnoreCase);
            }
            if (inSection && node.Name == "table" &&
                node.GetAttributeValue("data-macro-name", "") == "table-excerpt") return node;
        }
        // A copied macro can omit its section heading. Accept it only when unambiguous
        // and when it does not wrap a service card.
        if (inSection) return null;
        var standalone = document.DocumentNode.Descendants("table").Where(table =>
            table.GetAttributeValue("data-macro-name", "") == "table-excerpt" &&
            TemplateTables.Find(table.Descendants("table"), "Название сервиса", "Команда", anyRow: true) is null)
            .ToArray();
        return standalone.Length == 1 ? standalone[0] : null;
    }

    private static void MergeParagraphsByHeading(HtmlDocument target, HtmlDocument source)
    {
        var sourceHeadings = source.DocumentNode.Descendants()
            .Where(node => IsHeading(node) && Normalize(node.InnerText).Length > 0)
            .GroupBy(node => Normalize(node.InnerText), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        foreach (var targetHeading in target.DocumentNode.Descendants().Where(IsHeading))
        {
            if (!sourceHeadings.TryGetValue(Normalize(targetHeading.InnerText), out var sourceHeading)) continue;
            var sourceParagraphs = SectionParagraphs(sourceHeading).ToArray();
            var targetParagraphs = SectionParagraphs(targetHeading).ToArray();
            for (var index = 0; index < Math.Min(sourceParagraphs.Length, targetParagraphs.Length); index++)
            {
                if (HasContent(sourceParagraphs[index]))
                    targetParagraphs[index].InnerHtml = sourceParagraphs[index].InnerHtml;
            }
        }
    }

    private static IEnumerable<HtmlNode> SectionParagraphs(HtmlNode heading)
    {
        for (var node = heading.NextSibling; node is not null && !IsHeading(node); node = node.NextSibling)
        {
            if (node.Name == "p") yield return node;
        }
    }

    private static bool IsHeading(HtmlNode node) =>
        node.Name is "h1" or "h2" or "h3" or "h4" or "h5" or "h6";

    private static HtmlNode? FindTable(IEnumerable<HtmlNode> tables, string first, string second, bool anyRow = false) =>
        TemplateTables.Find(tables, first, second, anyRow);

    private static IEnumerable<HtmlNode> Rows(HtmlNode table) =>
        table.Descendants("tr").Where(row => row.Ancestors("table").FirstOrDefault() == table);

    private static HtmlNode[] Cells(HtmlNode row) => row.ChildNodes
        .Where(node => node.Name is "td" or "th").ToArray();

    private static string? VariableName(HtmlNode cell) => Regex.Match(Normalize(cell.InnerText),
        @"\b[A-Z][A-Z0-9]*(?:_[A-Z0-9]+)+\b").Value is { Length: > 0 } name ? name : null;

    private static bool HasContent(HtmlNode cell) => Normalize(cell.InnerText).Length > 0 ||
        cell.Descendants().Any(node => node.Name is "img" or "a" || node.Attributes["data-macro-name"] is not null);

    private static string Normalize(string text) =>
        Regex.Replace(HtmlEntity.DeEntitize(text), @"\s+", " ").Trim();
}
