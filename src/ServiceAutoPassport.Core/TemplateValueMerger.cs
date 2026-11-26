using System.Text.RegularExpressions;
using HtmlAgilityPack;
using static ServiceAutoPassport.Core.TemplateTables;

namespace ServiceAutoPassport.Core;

public enum PassportTemplateSource { Clipboard, Saved, SavedWithClipboard }

public static class TemplateValueMerger
{
    public static string SelectSource(PassportTemplateSource source, string savedTemplateHtml,
        Func<string> readClipboardHtml, string? generatedClipboardHtml)
    {
        if (source == PassportTemplateSource.Clipboard)
        {
            var html = readClipboardHtml();
            if (html == generatedClipboardHtml)
                throw new InvalidDataException("В буфере готовый паспорт. Скопируйте исходную страницу шаблона.");
            return html;
        }

        if (string.IsNullOrWhiteSpace(savedTemplateHtml))
            throw new InvalidDataException("Сначала сохраните полную страницу как шаблон по умолчанию.");
        return source == PassportTemplateSource.SavedWithClipboard
            ? Merge(savedTemplateHtml, readClipboardHtml())
            : savedTemplateHtml;
    }

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
        var targetRows = Rows(target).ToArray();
        var width = Cells(targetRows[0]).Length;
        var parent = targetRows.Skip(1).LastOrDefault()?.ParentNode ?? target.Element("tbody") ?? target;
        var existing = targetRows.Skip(1).Select(Cells).Where(cells => cells.Length >= 3)
            .ToList();
        foreach (var row in Rows(source).Skip(1))
        {
            var sourceCells = Cells(row);
            if (sourceCells.Length < 3) continue;
            var name = Normalize(sourceCells[0].InnerText);
            if (name.Length == 0) continue;
            var candidates = existing.Where(cells => Normalize(cells[0].InnerText)
                .Equals(name, StringComparison.OrdinalIgnoreCase));
            var cells = FindAccessMatch(sourceCells, candidates);
            if (cells is null)
            {
                var added = row.CloneNode(true);
                foreach (var extra in Cells(added).Skip(width)) extra.Remove();
                for (var index = sourceCells.Length; index < width; index++)
                    added.AppendChild(HtmlNode.CreateNode("<td><br /></td>"));
                parent.AppendChild(added);
                existing.Add(Cells(added));
                continue;
            }

            MergeAccessKeys(cells[1], sourceCells[1]);
            MergeAccessKeys(cells[2], sourceCells[2]);
            for (var index = 3; index < Math.Min(cells.Length, sourceCells.Length); index++)
                if (HasContent(sourceCells[index])) cells[index].InnerHtml = sourceCells[index].InnerHtml;
        }
    }

    private static HtmlNode[]? FindAccessMatch(HtmlNode[] source, IEnumerable<HtmlNode[]> candidates)
    {
        static HashSet<string> Keys(HtmlNode cell) =>
            PassportTemplateRenderer.AccessVariableNames(cell).ToHashSet(StringComparer.Ordinal);
        var addresses = Keys(source[1]);
        var vault = Keys(source[2]);
        var rows = candidates.Select(cells => (Cells: cells, Addresses: Keys(cells[1]), Vault: Keys(cells[2]))).ToArray();
        var exact = rows.FirstOrDefault(row => row.Addresses.SetEquals(addresses) && row.Vault.SetEquals(vault));
        if (exact.Cells is not null) return exact.Cells;

        var byAddress = rows.Where(row => row.Addresses.Overlaps(addresses) &&
            (vault.Count == 0 || row.Vault.Count == 0 || row.Vault.Overlaps(vault))).Take(2).ToArray();
        if (byAddress.Length != 0) return byAddress.Length == 1 ? byAddress[0].Cells : null;

        // Shared credentials alone cannot identify two rows with different addresses.
        var byVault = rows.Where(row => (row.Addresses.Count == 0 || addresses.Count == 0) && row.Vault.Overlaps(vault))
            .Take(2).ToArray();
        return byVault.Length == 1 ? byVault[0].Cells : null;
    }

    private static void MergeAccessKeys(HtmlNode target, HtmlNode source)
    {
        var saved = PassportTemplateRenderer.AccessVariableNames(target).ToHashSet(StringComparer.Ordinal);
        var added = PassportTemplateRenderer.AccessVariableNames(source).Where(saved.Add).ToArray();
        if (added.Length == 0) return;
        if (!HasContent(target))
        {
            target.InnerHtml = source.InnerHtml;
            return;
        }
        foreach (var name in added) target.AppendChild(HtmlNode.CreateNode($"<p>{name}</p>"));
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

}
