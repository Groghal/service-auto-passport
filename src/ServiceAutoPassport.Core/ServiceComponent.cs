using System.Text.RegularExpressions;

namespace ServiceAutoPassport.Core;

public enum ComponentType { Unknown, WebApi, Daemon, UI }

public static class ServiceComponent
{
    private const string WebPattern = @"^.*(-webapi|-api|-bff|-grpc|-web|-payrollservice|-domainapi|webapplicationdocument|directbank1c-front|directbank1c-internal|dictionaries-service|salaryproject-adminbff|salarynowdmz|testdotnetcorewebapp|kafka-example-consumer|common-camunda-example).*$";
    private const string DaemonPattern = @"^.*(-daemon|-worker|-s8worker|-cftworkerservice|-processor|-scheduler|scheduler|-statementsscheduler|-cons-limits|-backgroundservice|-orchestrator|-deamon|winservice|-winservice|-router|-integration|mcb-legal-rabbitmq-demo-consumer).*$";

    public static string DeploymentName(string folder) =>
        Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)))
            .Replace('_', '-').Replace('.', '-').ToLowerInvariant();

    public static ComponentType Detect(string folder)
    {
        var name = DeploymentName(folder);
        if (Regex.IsMatch(name, WebPattern)) return ComponentType.WebApi;
        if (Regex.IsMatch(name, DaemonPattern)) return ComponentType.Daemon;
        return HasAppsettings(folder) ? ComponentType.Unknown : ComponentType.UI;
    }

    private static bool HasAppsettings(string folder)
    {
        foreach (var file in Directory.EnumerateFiles(folder))
        {
            var name = Path.GetFileName(file);
            if (name.StartsWith("appsettings", StringComparison.OrdinalIgnoreCase) &&
                name.EndsWith(".j2", StringComparison.OrdinalIgnoreCase)) return true;
        }
        foreach (var child in Directory.EnumerateDirectories(folder))
        {
            if (Path.GetFileName(child).ToLowerInvariant() is ".git" or "node_modules" or "bin" or "obj" ||
                (File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) continue;
            if (HasAppsettings(child)) return true;
        }
        return false;
    }
}
