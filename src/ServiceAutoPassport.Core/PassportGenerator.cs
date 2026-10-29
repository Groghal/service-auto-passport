using HtmlAgilityPack;

namespace ServiceAutoPassport.Core;

public sealed record PassportOptions(
    string ServiceName,
    string YamlRoot,
    string? J2File = null,
    string DisplayName = "",
    string Team = "",
    IReadOnlyDictionary<string, string>? KeyDescriptions = null,
    bool DisplayNamePriority = true,
    bool TeamPriority = true,
    IReadOnlySet<string>? HtmlDescriptionKeys = null,
    bool DisplayNameIsHtml = false,
    bool TeamIsHtml = false,
    string? ServiceFolder = null,
    string DomainApiVariable = "");

public sealed record PassportResult(string HtmlFragment, string PlainText);

public static class PassportGenerator
{
    public static PassportResult Generate(string clipboardHtml, PassportOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var hasJ2 = !string.IsNullOrWhiteSpace(options.J2File);
        var serviceFolder = options.ServiceFolder ?? (hasJ2
            ? Path.GetDirectoryName(Path.GetFullPath(options.J2File!))!
            : throw new InvalidDataException("Укажите папку сервиса."));
        var componentType = ServiceComponent.Detect(serviceFolder);
        if (!hasJ2 && componentType != ComponentType.UI)
            throw new InvalidDataException("Выберите основной J2 файл. Генерация без J2 доступна только для UI.");
        IReadOnlyList<string> mainVariables = Array.Empty<string>();
        IReadOnlyList<string> secretVariables = Array.Empty<string>();
        string? allowedServicesVariable = null;
        if (hasJ2)
        {
            if (!File.Exists(options.J2File))
                throw new FileNotFoundException("Не найден основной J2 файл.", options.J2File);
            mainVariables = J2Variables.Extract(File.ReadAllText(options.J2File!));
            var j2Directory = Path.GetDirectoryName(Path.GetFullPath(options.J2File!))!;
            var j2Name = Path.GetFileName(options.J2File!);
            var namedSecretFile = j2Name.EndsWith(".deploy.j2", StringComparison.OrdinalIgnoreCase)
                ? Path.Combine(j2Directory, j2Name[..^".deploy.j2".Length] + ".secrets.j2")
                : null;
            var secretFile = namedSecretFile is not null && File.Exists(namedSecretFile)
                ? namedSecretFile : Path.Combine(j2Directory, "secrets.j2");
            if (File.Exists(secretFile))
            {
                var secrets = File.ReadAllText(secretFile);
                secretVariables = J2Variables.Extract(secrets);
                allowedServicesVariable = J2Variables.ExtractAssignedVariable(secrets,
                    "GrpcSettings__ServerSettings__AllowedServices");
            }
        }
        var yaml = YamlConfiguration.Load(options.YamlRoot, options.ServiceName);
        var j2Git = GitLinks.FromPath(serviceFolder);
        var yamlGit = GitLinks.FromPath(options.YamlRoot);

        var fragment = CfHtml.ExtractFragment(clipboardHtml);
        var renderer = new PassportTemplateRenderer(fragment);
        var resultHtml = renderer.Render(options, yaml, mainVariables, secretVariables, j2Git, yamlGit,
            serviceFolder, componentType, allowedServicesVariable);
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
