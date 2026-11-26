using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace ServiceAutoPassport.Core;

internal sealed partial class PassportTemplateRenderer
{
    private const string DefaultPhrase = "Дефолтное значение в базовых параметрах";
    private readonly HtmlDocument _document = new();

    public PassportTemplateRenderer(string fragment)
    {
        _document.LoadHtml(fragment);
    }

    public string Render(
        PassportOptions options,
        YamlConfiguration yaml,
        IReadOnlyList<string> mainVariables,
        IReadOnlyList<string> secretVariables,
        GitLinks j2Git,
        GitLinks yamlGit,
        string serviceFolder,
        ComponentType componentType,
        string? allowedServicesVariable)
    {
        var tables = _document.DocumentNode.Descendants("table").ToArray();
        var card = FindTable(tables, "Название сервиса", "Команда", matchAnyRow: true);
        var services = FindTable(tables, "Название сервиса", "Переменная адреса компонента");
        var access = FindTable(tables, "Объект доступа", "Ключи в Vault");
        var parameters = FindTable(tables, "Описание", "Название переменной");
        if (!_document.DocumentNode.Descendants().Any(node =>
                node.Name is "h1" or "h2" or "h3" or "h4" or "h5" or "h6" &&
                Normalize(node.InnerText).Contains("Дополнительная информация", StringComparison.Ordinal)))
        {
            throw new InvalidDataException("В буфере нет полного шаблона: отсутствует блок «Дополнительная информация».");
        }

        FillCard(card, options, yaml, j2Git, yamlGit, serviceFolder, componentType, allowedServicesVariable);
        if (string.IsNullOrWhiteSpace(options.J2File)) return _document.DocumentNode.InnerHtml;
        FillServices(services, mainVariables, options.KeyDescriptions, options.HtmlDescriptionKeys);
        var allVariables = mainVariables.Concat(secretVariables).ToHashSet(StringComparer.Ordinal);
        var shownAccessVariables = FilterAccess(access, allVariables);
        FillParameters(parameters, mainVariables, secretVariables, shownAccessVariables, yaml,
            options.KeyDescriptions, options.HtmlDescriptionKeys);
        return _document.DocumentNode.InnerHtml;
    }

    private static void FillCard(
        HtmlNode table,
        PassportOptions options,
        YamlConfiguration yaml,
        GitLinks j2Git,
        GitLinks yamlGit,
        string serviceFolder,
        ComponentType componentType,
        string? allowedServicesVariable)
    {
        if (!string.IsNullOrWhiteSpace(options.DisplayName))
            SetCardValue(table, "Название сервиса", options.DisplayName,
                options.DisplayNamePriority, options.DisplayNameIsHtml);
        if (!string.IsNullOrWhiteSpace(options.Team))
            SetCardValue(table, "Команда", options.Team,
                options.TeamPriority, options.TeamIsHtml);
        if (!string.IsNullOrWhiteSpace(options.DomainApiVariable))
            SetCardValueByCaption(table, "Переменная содержащая адрес доменного API", options.DomainApiVariable);
        if (allowedServicesVariable is not null)
            SetCardValueByCaption(table, "Переменная с ключами для предоставления доступа", allowedServicesVariable);
        var deployName = ServiceComponent.DeploymentName(serviceFolder);
        SetCardValue(table, "Название сервиса в кубере или на сервере", deployName);
        SetCardValue(table, "Сервис в кубере?", "да");

        SetResources(table, "Requests", yaml.ResolveResource("cpu_req", componentType, "REQUEST", "cpu"),
            yaml.ResolveResource("mem_req", componentType, "REQUEST", "memory"));
        SetResources(table, "Limits", yaml.ResolveResource("cpu_lim", componentType, "LIMIT", "cpu"),
            yaml.ResolveResource("mem_lim", componentType, "LIMIT", "memory"));
        SetCardValue(table, "Количество Pod", yaml.OptionalAny("deploy_num", "replicas") ?? "1");
        SetCardValue(table, "Автомасштабирование", yaml.OptionalAny("autoscale") ?? "нет");
        if (yaml.OptionalAny("namespace", "namespace_name") is { } namespaceName)
            SetCardValue(table, "Namespace в K8S", namespaceName);

        SetCardLink(table, "Репозиторий", j2Git.RepositoryUrl);
        var serviceDirectory = Path.Combine(options.YamlRoot, options.ServiceName);
        SetCardLink(table, "Папка в app-params", yamlGit.TreeUrl(
            Directory.Exists(serviceDirectory) ? serviceDirectory : options.YamlRoot));
        if (string.IsNullOrWhiteSpace(options.J2File))
            SetCardValue(table, "Шаблон конфига", "Не используется");
        else SetCardLink(table, "Шаблон конфига", j2Git.BlobUrl(options.J2File));
        // All other card cells retain their original HTML, including hints and links.
    }

    private static void SetResources(HtmlNode table, string label, ResourceValue cpu, ResourceValue memory)
    {
        var row = FindCardRow(table, label);
        var cells = Cells(row);
        if (cells.Length < 3) throw MissingCell(label);
        cells[^1].InnerHtml = $"<p>CPU (доля от ядра): {FormatResource(cpu)}</p>" +
                              $"<p>RAM (Mb): {FormatResource(memory, isMemory: true)}</p>";
    }

    private static string FormatResource(ResourceValue resource, bool isMemory = false)
    {
        if (resource.Value is null) return "значение не найдено";
        var value = resource.Value.Trim();
        if (isMemory && Regex.IsMatch(value, @"^(?:\d+(?:\.\d+)?|\.\d+)$")) value += "Mi";
        return WebUtility.HtmlEncode(value) + (resource.IsDefault ? " (значение по умолчанию)" : "");
    }

    private static void SetCardValue(HtmlNode table, string label, string value,
        bool hasPriority = true, bool isHtml = false)
    {
        var row = FindCardRow(table, label);
        var cells = Cells(row);
        if (cells.Length < 2) throw MissingCell(label);
        if (!hasPriority && (Normalize(cells[^1].InnerText).Length > 0 ||
                             cells[^1].Descendants().Any(node => node.Name is not ("p" or "br"))))
            return;
        cells[^1].InnerHtml = isHtml
            ? DescriptionHtml.Normalize(value)
            : $"<p>{WebUtility.HtmlEncode(value)}</p>";
    }

    private static void SetCardValueByCaption(HtmlNode table, string label, string value)
    {
        var row = Rows(table).FirstOrDefault(row => Cells(row).Take(1).Any(cell =>
            Normalize(cell.InnerText).Contains(label, StringComparison.OrdinalIgnoreCase)))
            ?? throw new InvalidDataException($"В шаблоне не найдена строка карточки: {label}");
        var cells = Cells(row);
        if (cells.Length < 2) throw MissingCell(label);
        cells[^1].InnerHtml = $"<p>{WebUtility.HtmlEncode(value)}</p>";
    }

    private static void SetCardLink(HtmlNode table, string label, string url)
    {
        var row = FindCardRow(table, label);
        var cells = Cells(row);
        if (cells.Length < 2) throw MissingCell(label);
        var encoded = WebUtility.HtmlEncode(url);
        cells[^1].InnerHtml = $"<p><a href=\"{encoded}\">{encoded}</a></p>";
    }

    private static HtmlNode FindCardRow(HtmlNode table, string label)
    {
        return Rows(table).FirstOrDefault(row =>
                   Cells(row).Any(cell => Normalize(cell.InnerText).StartsWith(label, StringComparison.OrdinalIgnoreCase)))
               ?? throw new InvalidDataException($"В шаблоне не найдена строка карточки: {label}");
    }

    private static void FillServices(HtmlNode table, IReadOnlyList<string> mainVariables,
        IReadOnlyDictionary<string, string>? keyDescriptions, IReadOnlySet<string>? htmlDescriptionKeys)
    {
        var rows = Rows(table).ToArray();
        if (rows.Length == 0) throw new InvalidDataException("В таблице микросервисов нет заголовка.");
        var sample = rows.Length > 1 ? rows[1].CloneNode(true) : SampleRowFromHeader(rows[0]);
        var parent = rows[0].ParentNode ?? throw MissingCell("Используемые микросервисы test");
        var comments = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in rows.Skip(1))
        {
            var cells = Cells(row);
            if (cells.Length < 3 || string.IsNullOrWhiteSpace(Normalize(cells[2].InnerText))) continue;
            var name = VariableNames(cells[1].InnerText).FirstOrDefault(value =>
                value.StartsWith("SVC_", StringComparison.Ordinal));
            if (name is not null) comments.TryAdd(name, cells[2].InnerHtml);
        }
        foreach (var row in rows.Skip(1)) row.Remove();

        foreach (var name in mainVariables.Where(name => name.StartsWith("SVC_", StringComparison.Ordinal)))
        {
            var row = sample.CloneNode(true);
            var cells = Cells(row);
            if (cells.Length < 3) throw MissingCell("Используемые микросервисы test");
            var title = keyDescriptions is not null && keyDescriptions.TryGetValue(name, out var description)
                && !string.IsNullOrWhiteSpace(description) ? description : name;
            cells[0].InnerHtml = DescriptionMarkup(name, title, htmlDescriptionKeys);
            cells[1].InnerHtml = $"<p>{WebUtility.HtmlEncode(name)}</p>";
            cells[2].InnerHtml = comments.TryGetValue(name, out var comment) ? comment : "<br />";
            parent.AppendChild(row);
        }
    }

    private static HashSet<string> FilterAccess(HtmlNode table, HashSet<string> allVariables)
    {
        var kept = new Dictionary<(string Name, string Address, string Vault), HtmlNode[]>();
        foreach (var row in Rows(table).Skip(1).ToArray())
        {
            var cells = Cells(row);
            if (cells.Length < 3) throw MissingCell("Объекты доступа");
            PruneVariableCell(cells[1], allVariables);
            PruneVariableCell(cells[2], allVariables);
            var identity = (Name: Normalize(cells[0].InnerText).ToUpperInvariant(),
                Address: KeyList(cells[1]), Vault: KeyList(cells[2]));
            if (identity.Address.Length == 0 && identity.Vault.Length == 0)
            {
                row.Remove();
                continue;
            }

            if (kept.TryAdd(identity, cells)) continue;
            var first = kept[identity];
            for (var index = 3; index < Math.Min(cells.Length, first.Length); index++)
            {
                var extra = cells[index];
                var target = first[index];
                if (!TemplateTables.HasContent(extra) || target.InnerHtml.Contains(extra.InnerHtml, StringComparison.Ordinal))
                    continue;
                target.InnerHtml = TemplateTables.HasContent(target)
                    ? target.InnerHtml + "<br />" + extra.InnerHtml : extra.InnerHtml;
            }
            row.Remove();
        }

        return Rows(table).Skip(1).Select(Cells)
            .SelectMany(cells => AccessVariableNames(cells[1]).Concat(AccessVariableNames(cells[2])))
            .ToHashSet(StringComparer.Ordinal);

        string KeyList(HtmlNode cell) => string.Join(" ", AccessVariableNames(cell).Where(allVariables.Contains)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
    }

    private static void PruneVariableCell(HtmlNode cell, HashSet<string> allVariables)
    {
        var (text, parts) = ReadAccessText(cell);
        var removed = new bool[text.Length];
        foreach (Match match in VariableRegex().Matches(text))
            if (!allVariables.Contains(match.Value))
                Array.Fill(removed, true, match.Index, match.Length);

        var changedParagraphs = new HashSet<HtmlNode>();
        foreach (var (node, start, length) in parts)
        {
            if (!removed.AsSpan(start, length).Contains(true)) continue;
            var paragraph = node.Ancestors("p").FirstOrDefault();
            if (paragraph is not null) changedParagraphs.Add(paragraph);
            var replacement = new StringBuilder(length);
            for (var index = start; index < start + length; index++)
                if (!removed[index]) replacement.Append(text[index]);
            node.InnerHtml = WebUtility.HtmlEncode(replacement.ToString());
        }

        foreach (var paragraph in changedParagraphs)
        {
            if (Normalize(paragraph.InnerText).Length == 0 &&
                !paragraph.Descendants().Any(node => node.Name is "img" or "a" or "table" ||
                    node.Attributes["data-macro-name"] is not null))
                paragraph.Remove();
        }
    }

    private static void FillParameters(
        HtmlNode table,
        IReadOnlyList<string> mainVariables,
        IReadOnlyList<string> secretVariables,
        HashSet<string> shownAccessVariables,
        YamlConfiguration yaml,
        IReadOnlyDictionary<string, string>? keyDescriptions,
        IReadOnlySet<string>? htmlDescriptionKeys)
    {
        var rows = Rows(table).ToArray();
        if (rows.Length == 0) throw new InvalidDataException("В таблице параметров нет заголовка.");
        var sample = rows.Length > 1 ? rows[1].CloneNode(true) : SampleRowFromHeader(rows[0]);
        var originals = new Dictionary<string, HtmlNode>(StringComparer.Ordinal);
        foreach (var row in rows.Skip(1))
        {
            var cells = Cells(row);
            if (cells.Length < 4) throw MissingCell("Специфичные настраиваемые параметры");
            var name = VariableNames(cells[1].InnerText).FirstOrDefault();
            if (name is not null) originals.TryAdd(name, row.CloneNode(true));
            row.Remove();
        }

        var services = mainVariables.Where(name => name.StartsWith("SVC_", StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);
        var secrets = secretVariables.ToHashSet(StringComparer.Ordinal);
        var parent = rows[0].ParentNode ?? throw MissingCell("Специфичные настраиваемые параметры");
        var parameters = mainVariables.Concat(secretVariables)
            .Distinct(StringComparer.Ordinal)
            .Where(name => !services.Contains(name) && !shownAccessVariables.Contains(name))
            .Select(name => (Name: name, IsSecret: secrets.Contains(name) && !yaml.Values.ContainsKey(name)))
            .OrderByDescending(parameter => parameter.IsSecret)
            .ThenBy(parameter => parameter.Name, StringComparer.Ordinal);
        foreach (var (name, isSecret) in parameters)
        {
            var existed = originals.TryGetValue(name, out var original);
            var row = existed ? original! : sample.CloneNode(true);
            var cells = Cells(row);
            if (!existed)
            {
                cells[0].InnerHtml = "<br />";
                cells[3].InnerHtml = "<br />";
            }

            cells[1].InnerHtml = $"<p>{WebUtility.HtmlEncode(name)}</p>";
            if (keyDescriptions is not null && keyDescriptions.TryGetValue(name, out var description)
                && !string.IsNullOrWhiteSpace(description))
                cells[0].InnerHtml = DescriptionMarkup(name, description, htmlDescriptionKeys);
            cells[2].InnerHtml = isSecret ? "Да" : "Нет";
            if (yaml.GlobalKeys.Contains(name))
                cells[3].InnerHtml = $"<p>{DefaultPhrase}</p>";
            else if (yaml.Values.TryGetValue(name, out var value))
                cells[3].InnerHtml = $"<p>{WebUtility.HtmlEncode(value)}</p>";
            parent.AppendChild(row);
        }
    }

    private static string DescriptionMarkup(string key, string value, IReadOnlySet<string>? htmlDescriptionKeys) =>
        htmlDescriptionKeys?.Contains(key) == true
            ? DescriptionHtml.Normalize(value)
            : $"<p>{WebUtility.HtmlEncode(value)}</p>";

    private static HtmlNode SampleRowFromHeader(HtmlNode header)
    {
        var row = HtmlNode.CreateNode("<tr></tr>");
        foreach (var heading in Cells(header))
        {
            var cell = HtmlNode.CreateNode("<td><br /></td>");
            var className = heading.GetAttributeValue("class", "");
            if (className.Length > 0)
                cell.SetAttributeValue("class", className.Replace("confluenceTh", "confluenceTd",
                    StringComparison.Ordinal));
            var style = heading.GetAttributeValue("style", "");
            if (style.Length > 0) cell.SetAttributeValue("style", style);
            row.AppendChild(cell);
        }

        return row;
    }

    private static HtmlNode FindTable(IEnumerable<HtmlNode> tables, string first, string second, bool matchAnyRow = false) =>
        TemplateTables.Find(tables, first, second, matchAnyRow)
        ?? throw new InvalidDataException($"В буфере нет таблицы шаблона: {first} / {second}");

    private static IEnumerable<HtmlNode> Rows(HtmlNode table) =>
        table.Descendants("tr").Where(row => row.Ancestors("table").FirstOrDefault() == table);

    private static HtmlNode[] Cells(HtmlNode row) => row.ChildNodes
        .Where(node => node.Name is "td" or "th").ToArray();

    private static IEnumerable<string> VariableNames(string text) => VariableRegex().Matches(text)
        .Select(match => match.Value);

    internal static IEnumerable<string> AccessVariableNames(HtmlNode node) => VariableNames(ReadAccessText(node).Text);

    private static (string Text, List<(HtmlNode Node, int Start, int Length)> Parts) ReadAccessText(HtmlNode cell)
    {
        var text = new StringBuilder();
        var parts = new List<(HtmlNode, int, int)>();
        Append(cell);
        return (text.ToString(), parts);

        void Append(HtmlNode node)
        {
            if (node.NodeType == HtmlNodeType.Text)
            {
                var value = WebUtility.HtmlDecode(node.InnerText);
                parts.Add((node, text.Length, value.Length));
                text.Append(value);
                return;
            }

            // Formatting spans can divide one name. Block boundaries still separate names.
            var separates = node.Name is "p" or "br" or "div" or "ul" or "ol" or "li" or "tr" or "td" or "th" or "pre";
            if (separates) text.Append(' ');
            foreach (var child in node.ChildNodes) Append(child);
            if (separates) text.Append(' ');
        }
    }

    private static string Normalize(string text) => WhitespaceRegex().Replace(WebUtility.HtmlDecode(text), " ").Trim();

    private static InvalidDataException MissingCell(string label) =>
        new($"В шаблоне повреждена строка или таблица: {label}");

    [GeneratedRegex(@"\b[A-Z][A-Z0-9]*(?:_[A-Z0-9]+)+\b")]
    private static partial Regex VariableRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
