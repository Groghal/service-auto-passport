using System.Net;
using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace ServiceAutoPassport.Core;

public static partial class DescriptionHtml
{
    public static bool LooksLikeMarkup(string value) => MarkupRegex().IsMatch(value);

    public static string Normalize(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var document = new HtmlDocument();
        document.LoadHtml(value);
        foreach (var node in document.DocumentNode.Descendants())
        {
            if (node.Name is "html" or "head" or "body" or "script" or "style" or "iframe" or
                "object" or "embed" or "form" or "input" or "button" or "svg" or "math" or
                "link" or "meta" or "base")
                throw new InvalidDataException($"HTML описания содержит недопустимый элемент <{node.Name}>.");

            foreach (var attribute in node.Attributes)
            {
                if (attribute.Name.StartsWith("on", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"HTML описания содержит недопустимый атрибут {attribute.Name}.");
                var isHref = attribute.Name.Equals("href", StringComparison.OrdinalIgnoreCase);
                var isSrc = attribute.Name.Equals("src", StringComparison.OrdinalIgnoreCase);
                if ((isHref || isSrc) && !SafeUrl(attribute.Value ?? "", isHref))
                    throw new InvalidDataException($"HTML описания содержит недопустимый адрес в {attribute.Name}.");
            }
        }

        return document.DocumentNode.InnerHtml;
    }

    private static bool SafeUrl(string value, bool isHref)
    {
        var url = WebUtility.HtmlDecode(value).Trim();
        if (url.Any(char.IsControl)) return false;
        var scheme = UrlSchemeRegex().Match(url);
        if (!scheme.Success) return true;
        return scheme.Groups[1].Value.ToLowerInvariant() switch
        {
            "http" or "https" => true,
            "mailto" when isHref => true,
            _ => false
        };
    }

    [GeneratedRegex(@"<\s*/?\s*[a-z][a-z0-9:._-]*(?=[\s/>])", RegexOptions.IgnoreCase)]
    private static partial Regex MarkupRegex();

    [GeneratedRegex(@"^([a-z][a-z0-9+.-]*):", RegexOptions.IgnoreCase)]
    private static partial Regex UrlSchemeRegex();
}
