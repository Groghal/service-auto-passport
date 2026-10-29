using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace ServiceAutoPassport.Core;

internal static class TemplateTables
{
    public static HtmlNode? Find(IEnumerable<HtmlNode> tables, string first, string second, bool anyRow = false) =>
        tables.FirstOrDefault(table =>
        {
            var rows = table.Descendants("tr").Where(row => row.Ancestors("table").FirstOrDefault() == table);
            var texts = (anyRow ? rows : rows.Take(1))
                .Select(row => row.ChildNodes.Where(node => node.Name is "td" or "th").ToArray())
                .Where(cells => cells.Length >= 2)
                .SelectMany(cells => cells)
                .Select(cell => Normalize(string.Concat(cell.DescendantsAndSelf()
                    .Where(node => node.NodeType == HtmlNodeType.Text &&
                        node.Ancestors("table").FirstOrDefault() == table)
                    .Select(node => node.InnerText)))).ToArray();
            return texts.Any(text => text.Contains(first, StringComparison.OrdinalIgnoreCase)) &&
                   texts.Any(text => text.Contains(second, StringComparison.OrdinalIgnoreCase));
        });

    private static string Normalize(string text) =>
        Regex.Replace(HtmlEntity.DeEntitize(text), @"\s+", " ").Trim();
}
