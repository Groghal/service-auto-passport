using System.Text.RegularExpressions;

namespace ServiceAutoPassport.Core;

public static class ServiceInputs
{
    public static IReadOnlyList<string> FindDomainApiVariables(string appParamsRoot, string service) =>
        YamlConfiguration.Load(appParamsRoot, service).Values.Keys
            .OrderBy(name => name, StringComparer.Ordinal).ToArray();

    public static IReadOnlyList<string> FindServices(string appParamsRoot)
    {
        if (!Directory.Exists(appParamsRoot)) return Array.Empty<string>();

        return Directory.EnumerateDirectories(appParamsRoot)
            .Where(directory => Directory.EnumerateFiles(directory)
                .Any(file => IsExtension(file, ".yml") || IsExtension(file, ".yaml")))
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static IReadOnlyList<string> FindTemplates(string serviceFolder)
    {
        if (!Directory.Exists(serviceFolder)) return Array.Empty<string>();

        return Directory.EnumerateFiles(serviceFolder)
            .Where(file => IsExtension(file, ".j2"))
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrWhiteSpace(name) &&
                           !name.EndsWith("secrets.j2", StringComparison.OrdinalIgnoreCase))
            .Select(name => name!)
            .OrderByDescending(name => name.EndsWith(".deploy.j2", StringComparison.OrdinalIgnoreCase))
            .ThenBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static string SuggestServiceName(string serviceFolder, IReadOnlyList<string> availableServices)
    {
        var folderName = string.IsNullOrWhiteSpace(serviceFolder)
            ? "" : Path.GetFileName(serviceFolder.TrimEnd('/', '\\'));
        if (string.IsNullOrWhiteSpace(folderName)) return "";

        var exact = availableServices.FirstOrDefault(name =>
            name.Equals(folderName, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact;

        var normalizedFolder = NormalizeForComparison(folderName);
        var normalizedMatches = availableServices.Where(name =>
            NormalizeForComparison(name).Equals(normalizedFolder, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (normalizedMatches.Length == 1) return normalizedMatches[0];

        var parts = Regex.Split(folderName, @"[^\p{L}\p{N}]+")
            .Where(part => part.Length > 0).ToArray();
        if (parts.Length >= 2)
        {
            var suffix = NormalizeForComparison(parts[^2] + parts[^1]);
            if (suffix.Length >= 6)
            {
                var suffixMatches = availableServices.Where(name =>
                    NormalizeForComparison(name).EndsWith(suffix, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (suffixMatches.Length == 1) return suffixMatches[0];
            }
        }

        return Regex.Replace(folderName.ToLowerInvariant(), @"[^\p{L}\p{N}]+", "-").Trim('-');
    }

    private static string NormalizeForComparison(string name) =>
        new(name.Where(char.IsLetterOrDigit).ToArray());

    private static bool IsExtension(string path, string extension) =>
        Path.GetExtension(path).Equals(extension, StringComparison.OrdinalIgnoreCase);
}
