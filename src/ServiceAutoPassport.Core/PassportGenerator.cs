using HtmlAgilityPack;

namespace ServiceAutoPassport.Core;

public sealed record PassportOptions(
    string ServiceName,
    string YamlRoot,
    string J2File,
    string DisplayName = "",
    string Team = "",
    IReadOnlyDictionary<string, string>? KeyDescriptions = null,
    bool DisplayNamePriority = true,
    bool TeamPriority = true,
    IReadOnlySet<string>? HtmlDescriptionKeys = null,
    bool DisplayNameIsHtml = false,
    bool TeamIsHtml = false);

public sealed record PassportResult(string HtmlFragment, string PlainText);

public static class PassportGenerator
{
    public static PassportResult Generate(string clipboardHtml, PassportOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!File.Exists(options.J2File))
            throw new FileNotFoundException("Не найден основной J2 файл.", options.J2File);
        var mainVariables = J2Variables.Extract(File.ReadAllText(options.J2File));
        var j2Directory = Path.GetDirectoryName(Path.GetFullPath(options.J2File))!;
        var j2Name = Path.GetFileName(options.J2File);
        var namedSecretFile = j2Name.EndsWith(".deploy.j2", StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(j2Directory, j2Name[..^".deploy.j2".Length] + ".secrets.j2")
            : null;
        var secretFile = namedSecretFile is not null && File.Exists(namedSecretFile)
            ? namedSecretFile
            : Path.Combine(j2Directory, "secrets.j2");
        var secretVariables = File.Exists(secretFile)
            ? J2Variables.Extract(File.ReadAllText(secretFile))
            : Array.Empty<string>();
        var yaml = YamlConfiguration.Load(options.YamlRoot, options.ServiceName);
        var j2Git = GitLinks.FromPath(options.J2File);
        var yamlGit = GitLinks.FromPath(options.YamlRoot);

        var fragment = CfHtml.ExtractFragment(clipboardHtml);
        var renderer = new PassportTemplateRenderer(fragment);
        var resultHtml = renderer.Render(options, yaml, mainVariables, secretVariables, j2Git, yamlGit);
        return new PassportResult(resultHtml, ToPlainText(resultHtml));
    }

    private static string ToPlainText(string html)
    {
        var document = new HtmlDocument();
        document.LoadHtml(html);
        foreach (var node in document.DocumentNode.Descendants().Where(node => node.Name is "br" or "p" or "tr").ToArray())
        {
            if (node.Name == "br") node.ParentNode?.ReplaceChild(document.CreateTextNode("\n"), node);
            else node.AppendChild(document.CreateTextNode(node.Name == "tr" ? "\n" : " "));
        }

        return HtmlEntity.DeEntitize(document.DocumentNode.InnerText).Trim();
    }
}
