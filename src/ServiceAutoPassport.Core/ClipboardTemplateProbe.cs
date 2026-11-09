using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace ServiceAutoPassport.Core;

public static class ClipboardTemplateProbe
{
    public static bool LooksLikeTemplate(string? clipboardHtml, bool includeNetwork = true)
    {
        if (string.IsNullOrWhiteSpace(clipboardHtml)) return false;

        string fragment;
        try
        {
            fragment = CfHtml.ExtractFragment(clipboardHtml);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidDataException)
        {
            return false;
        }

        var document = new HtmlDocument();
        document.LoadHtml(fragment);
        var text = Regex.Replace(HtmlEntity.DeEntitize(document.DocumentNode.InnerText), @"\s+", " ");
        if (includeNetwork && document.DocumentNode.Descendants().Any(n => n.GetAttributeValue("data-macro-name", "") == "drawio") &&
            TemplateTables.Find(document.DocumentNode.Descendants("table"), "Подключение", "Параметры") is not null) return true;
        return HasAll(text, "Название сервиса", "Namespace в K8S") ||
               HasAll(text, "Переменная адреса компонента в app-params", "Комментарии") ||
               HasAll(text, "Объект доступа", "Переменная адреса в app-params") ||
               HasAll(text, "Название переменной", "Значение по умолчанию") ||
               (text.Contains("Дополнительная информация", StringComparison.OrdinalIgnoreCase) &&
                document.DocumentNode.Descendants("table").Any(table =>
                    table.GetAttributeValue("data-macro-name", "") == "table-excerpt"));
    }

    private static bool HasAll(string text, string first, string second) =>
        text.Contains(first, StringComparison.OrdinalIgnoreCase) &&
        text.Contains(second, StringComparison.OrdinalIgnoreCase);
}
