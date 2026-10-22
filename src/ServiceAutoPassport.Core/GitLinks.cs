using System.Text.RegularExpressions;

namespace ServiceAutoPassport.Core;

public sealed record GitLinks(string RepositoryUrl, string RootDirectory)
{
    public static GitLinks FromPath(string path)
    {
        var current = Directory.Exists(path) ? Path.GetFullPath(path) : Path.GetDirectoryName(Path.GetFullPath(path));
        while (current is not null)
        {
            var gitEntry = Path.Combine(current, ".git");
            if (Directory.Exists(gitEntry) || File.Exists(gitEntry))
            {
                var gitDirectory = ResolveGitDirectory(gitEntry);
                var config = Path.Combine(gitDirectory, "config");
                var commonDir = Path.Combine(gitDirectory, "commondir");
                if (!File.Exists(config) && File.Exists(commonDir))
                {
                    var common = File.ReadAllText(commonDir).Trim();
                    config = Path.GetFullPath(Path.Combine(gitDirectory, common, "config"));
                }

                if (!File.Exists(config))
                    throw new InvalidDataException($"Не найден .git/config для {current}");

                var remote = ReadOrigin(config);
                return new GitLinks(ToWebUrl(remote), current);
            }

            current = Directory.GetParent(current)?.FullName;
        }

        throw new InvalidDataException($"Не найден Git репозиторий для {path}");
    }

    public string TreeUrl(string directory) => FileOrDirectoryUrl("tree", directory);
    public string BlobUrl(string file) => FileOrDirectoryUrl("blob", file);

    private string FileOrDirectoryUrl(string kind, string path)
    {
        var relative = Path.GetRelativePath(RootDirectory, Path.GetFullPath(path));
        if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidDataException($"Путь находится вне Git репозитория: {path}");
        if (relative == ".")
            return RepositoryUrl.TrimEnd('/') + "/-/" + kind + "/master";

        var segments = relative.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.EscapeDataString);
        return RepositoryUrl.TrimEnd('/') + "/-/" + kind + "/master/" + string.Join('/', segments);
    }

    private static string ResolveGitDirectory(string gitEntry)
    {
        if (Directory.Exists(gitEntry)) return gitEntry;
        var content = File.ReadAllText(gitEntry).Trim();
        if (!content.StartsWith("gitdir:", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Некорректный файл {gitEntry}");
        var target = content[7..].Trim();
        return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(gitEntry)!, target));
    }

    private static string ReadOrigin(string config)
    {
        var inOrigin = false;
        foreach (var rawLine in File.ReadLines(config))
        {
            var line = rawLine.Trim();
            if (line.StartsWith('['))
            {
                inOrigin = Regex.IsMatch(line, "^\\[remote\\s+\"origin\"\\]$", RegexOptions.IgnoreCase);
                continue;
            }

            if (inOrigin && line.StartsWith("url", StringComparison.OrdinalIgnoreCase))
            {
                var equals = line.IndexOf('=');
                if (equals >= 0) return line[(equals + 1)..].Trim();
            }
        }

        throw new InvalidDataException($"В {config} не указан remote origin.");
    }

    private static string ToWebUrl(string remote)
    {
        if (Uri.TryCreate(remote, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            var builder = new UriBuilder(uri) { UserName = "", Password = "" };
            return StripGitSuffix(builder.Uri.GetLeftPart(UriPartial.Path));
        }

        if (Uri.TryCreate(remote, UriKind.Absolute, out uri) && uri.Scheme == "ssh")
            return StripGitSuffix($"https://{uri.Host}{uri.AbsolutePath}");

        var scp = Regex.Match(remote, @"^(?:[^@]+@)?(?<host>[^:]+):(?<path>.+)$");
        if (scp.Success)
            return StripGitSuffix($"https://{scp.Groups["host"].Value}/{scp.Groups["path"].Value}");

        throw new InvalidDataException($"Не удалось построить веб-ссылку из Git remote: {remote}");
    }

    private static string StripGitSuffix(string url)
    {
        url = url.TrimEnd('/');
        return url.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? url[..^4] : url;
    }
}
