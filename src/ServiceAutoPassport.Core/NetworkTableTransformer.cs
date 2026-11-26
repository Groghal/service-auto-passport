using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace ServiceAutoPassport.Core;

internal static class NetworkTableTransformer
{
    private const string MacroName = "table-joiner";
    private const string ExpectedMacro = "Нужен один макрос Table Transformer с исходной таблицей, скопированный из редактора Confluence.";
    private const string ParametersAttribute = "data-macro-parameters";
    internal const string FirstColumn = "Элемент";
    internal const string ParametersColumn = "Параметры";
    internal const string AddressColumn = "Адресный параметр";
    internal const string BlockColumn = "Блок";
    internal const string ParentBlockColumn = "Блок-родитель";
    internal const string NodeLabelColumn = "Подпись узла";
    private static readonly Regex Parameter = new(@"(?:^|\|)(?<name>[^=|]+)=(?<value>(?:\\.|[^\\|])*)");

    private static bool IsMacro(HtmlNode node) => node.GetAttributeValue("data-macro-name", "") == MacroName;

    public static (HtmlNode Macro, HtmlNode Table) Find(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) throw new InvalidDataException(ExpectedMacro);
        var document = new HtmlDocument();
        document.LoadHtml(CfHtml.ExtractFragment(html));
        return Find(document);
    }

    public static (HtmlNode Macro, HtmlNode Table) Find(HtmlDocument document)
    {
        var macros = document.DocumentNode.Descendants().Where(IsMacro).ToArray();
        if (macros.Length != 1 || macros[0].Name != "table" || macros[0].GetAttributeValue("data-macro-body-type", "") != "RICH_TEXT")
            throw new InvalidDataException(ExpectedMacro);

        var macro = macros[0];
        var tables = macro.Descendants("table").Where(table =>
            table.Ancestors("table").FirstOrDefault() == macro &&
            table.Ancestors("td").Any(cell => cell.HasClass("wysiwyg-macro-body") && cell.Ancestors("table").FirstOrDefault() == macro)).ToArray();
        return tables.Length == 1 ? (macro, tables[0]) :
            throw new InvalidDataException("Внутри Table Transformer должна быть одна исходная таблица.");
    }

    public static void Fill((HtmlNode Macro, HtmlNode Table) template, IReadOnlyList<string> environments, IEnumerable<NetworkTarget> targets,
        IReadOnlyDictionary<string, string> blockParents, IReadOnlySet<string> sharedContainers)
    {
        var (macro, table) = template;
        var tableEnvironments = environments.OrderByDescending(env => env.Equals("prod", StringComparison.OrdinalIgnoreCase)).ToArray();
        var visible = new[] { FirstColumn }.Concat(tableEnvironments).ToArray();
        var columns = visible.Concat(new[] { ParametersColumn, "Сервисы", "Подключение", AddressColumn, BlockColumn, ParentBlockColumn, NodeLabelColumn });
        var html = new StringBuilder("<tbody><tr>");
        foreach (var column in columns) AppendCell(html, "th", column);
        html.Append("</tr>");

        foreach (var target in targets)
        {
            var vertex = target.Vertex;
            var container = vertex.Container;
            var useContainerName = !vertex.IsService && container.Length > 0 &&
                !container.Equals(NetworkTargetSettings.DefaultBlock, StringComparison.OrdinalIgnoreCase) && !sharedContainers.Contains(container);
            html.Append("<tr>");
            AppendCell(html, "td", useContainerName ? container : vertex.Label);
            foreach (var env in tableEnvironments) AppendCell(html, "td", Endpoints(target.Connections, env));
            AppendCell(html, "td", string.Join(", ", target.Connections.SelectMany(c => c.ParameterKeys).Distinct(StringComparer.Ordinal)));
            AppendCell(html, "td", string.Join("\n", target.Connections.Select(c => c.Service.Label).Distinct()));
            AppendCell(html, "td", string.Join("\n", target.Connections.Select(c => c.Service.Label + " — " + c.Path)));
            var addressKeys = target.Connections.Select(c => c.TargetKey).Distinct(StringComparer.Ordinal).ToArray();
            var address = addressKeys.Length == 1 && !NetworkParameterCatalog.IsLiteralKey(addressKeys[0])
                ? NetworkParameterCatalog.DisplayParameter(addressKeys[0], target.Connections[0].ExternalTarget) : "";
            AppendCell(html, "td", address);
            AppendCell(html, "td", vertex.Block);
            AppendCell(html, "td", vertex.Block.Length == 0 ? vertex.ParentBlock :
                blockParents.GetValueOrDefault(vertex.Block, ""));
            AppendCell(html, "td", vertex.Label);
            html.Append("</tr>");
        }
        table.InnerHtml = html.Append("</tbody>").ToString();

        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match match in Parameter.Matches(WebUtility.HtmlDecode(macro.GetAttributeValue(ParametersAttribute, ""))))
            parameters[match.Groups["name"].Value] = match.Groups["value"].Value;
        parameters.TryAdd("id", FormattableString.Invariant($"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}_{Random.Shared.Next(int.MinValue, int.MaxValue)}"));
        parameters["join-column"] = FirstColumn;
        parameters["show-source"] = "false";
        parameters["transpose"] = parameters["transposeResult"] = "false";
        parameters["sql"] = "SELECT " + string.Join(", ", visible.Select(name => "T1.'" + name.Replace("'", "\\'") + "'")) + " FROM T1";
        macro.SetAttributeValue(ParametersAttribute,
            WebUtility.HtmlEncode(string.Join("|", parameters.Select(pair => pair.Key + "=" + pair.Value))));
    }

    private static string Endpoints(NetworkConnection[] connections, string environment)
    {
        var addresses = connections.GroupBy(connection => connection.Endpoints[environment]).ToArray();
        if (addresses.Length == 1) return addresses[0].Key;
        return string.Join("\n", addresses.Select(group =>
            string.Join(", ", group.Select(c => c.Service.Label).Distinct()) + " — " + group.Key));
    }

    private static void AppendCell(StringBuilder html, string tag, string text)
    {
        var className = tag == "th" ? "confluenceTh" : "confluenceTd";
        html.Append('<').Append(tag).Append(" class=\"").Append(className).Append("\">")
            .Append(WebUtility.HtmlEncode(text).Replace("\n", "<br />"))
            .Append("</").Append(tag).Append('>');
    }
}
