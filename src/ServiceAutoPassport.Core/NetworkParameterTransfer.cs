namespace ServiceAutoPassport.Core;

public static partial class NetworkParameterCatalog
{
    private static readonly string[] TransferHeader =
    [
        "Найденный параметр", "Название элемента", "Блок", "Блок-родитель",
        "Автоназвание", "Автоблок", "Родитель задан",
        "Ключ подключения", "Файл сервиса", "Имя сервиса", "Переменные пути"
    ];

    public static string Export(IReadOnlyDictionary<string, NetworkParameterEdit> entries,
        IReadOnlyDictionary<string, NetworkTargetSettings> targets, IReadOnlyList<NetworkServiceInput> services)
    {
        var files = ServiceFiles(services);
        return DelimitedTable.Write(new[] { TransferHeader }.Concat(entries.Select(pair =>
        {
            var (key, edit) = pair;
            var target = targets.GetValueOrDefault(key) ?? new NetworkTargetSettings();
            var source = IsLiteralKey(key) ? FindSource(key, target, edit.Source, files) : null;
            return new[]
            {
                DisplayParameter(key, target, source), edit.Label, edit.Block, edit.ParentBlock ?? "",
                DelimitedTable.WriteFlag(edit.LabelIsAutomatic), DelimitedTable.WriteFlag(edit.BlockIsAutomatic),
                DelimitedTable.WriteFlag(edit.ParentBlock is not null),
                source is null ? "" : key, source?.J2 ?? "", source?.Service ?? "", source?.PathVariables ?? ""
            };
        })));
    }

    public static IReadOnlyDictionary<string, NetworkParameterEdit> ParseEntries(string text,
        IReadOnlyDictionary<string, NetworkTargetSettings> targets, IReadOnlyDictionary<string, NetworkParameterEdit> draft,
        IReadOnlyList<NetworkServiceInput> services)
    {
        var separator = DelimitedTable.DetectSeparator(text);
        if (!DelimitedTable.HasHeader(text, TransferHeader, separator)) return ParseExcel(text, targets, draft);

        var files = ServiceFiles(services);
        var existing = targets.Keys.Concat(draft.Keys).Where(IsLiteralKey).Distinct(StringComparer.Ordinal)
            .ToDictionary(key => key, key => FindSource(key, targets.GetValueOrDefault(key), draft.GetValueOrDefault(key)?.Source, files),
                StringComparer.Ordinal);
        var entries = new Dictionary<string, NetworkParameterEdit>(StringComparer.Ordinal);
        foreach (var (number, cells) in DelimitedTable.Read(text.TrimStart('\uFEFF'), separator).Skip(1))
        {
            if (cells.Length != TransferHeader.Length)
                throw new InvalidDataException($"Строка {number}: число ячеек не совпадает с заголовками экспорта.");
            var label = cells[1].Trim();
            if (label.Length == 0)
                throw new InvalidDataException($"Строка {number}: укажите название элемента.");

            var originalKey = cells[7].Trim();
            NetworkParameterSource? source = null;
            string key;
            if (originalKey.Length == 0)
            {
                var variables = ParseAddressVariables(cells[0].Trim()) ??
                    throw new InvalidDataException($"Строка {number}: укажите J2-переменную.");
                key = AddressKey(variables);
            }
            else
            {
                if (!TryLiteralContext(originalKey, out _, out _))
                    throw new InvalidDataException($"Строка {number}: неверный ключ подключения.");
                source = new NetworkParameterSource(Nonempty(cells[8]), Nonempty(cells[9]), Nonempty(cells[10]));
                key = originalKey;
            }

            var parent = cells[3].Trim();
            var hasParent = DelimitedTable.ReadFlag(cells[6], number, TransferHeader[6]);
            var entry = new NetworkParameterEdit(label, cells[2].Trim())
            {
                ParentBlock = hasParent || parent.Length > 0 ? parent : null,
                LabelIsAutomatic = DelimitedTable.ReadFlag(cells[4], number, TransferHeader[4]),
                BlockIsAutomatic = DelimitedTable.ReadFlag(cells[5], number, TransferHeader[5]),
                Source = source
            };
            if (!entries.TryAdd(key, entry))
                throw new InvalidDataException($"Строка {number}: параметр «{cells[0]}» уже указан выше.");
        }
        if (entries.Count == 0) throw new InvalidDataException("Нет строк с параметрами для вставки.");
        return ResolveImportedEntries(entries, existing);
    }

    private static IReadOnlyDictionary<string, NetworkParameterEdit> ResolveImportedEntries(
        IReadOnlyDictionary<string, NetworkParameterEdit> entries, IReadOnlyDictionary<string, NetworkParameterSource> existing)
    {
        var result = new Dictionary<string, NetworkParameterEdit>(StringComparer.Ordinal);
        var groups = entries.GroupBy(pair => pair.Value.Source is { } source
            ? ResolveImportedKey(pair.Key, source, existing) : pair.Key, StringComparer.Ordinal);
        foreach (var group in groups)
        {
            // Preserve distinct source rows when several could map to the same connection.
            var unique = !group.Skip(1).Any();
            foreach (var (originalKey, edit) in group)
            {
                var key = unique && !entries.ContainsKey(group.Key) ? group.Key : originalKey;
                var source = edit.Source is { } entrySource
                    ? entrySource with { AwaitingConnection = existing.GetValueOrDefault(key)?.AwaitingConnection ?? true }
                    : null;
                result.Add(key, edit with { Source = source });
            }
        }
        return result;
    }

    private static string ResolveImportedKey(string key, NetworkParameterSource source,
        IReadOnlyDictionary<string, NetworkParameterSource> existing)
    {
        if (existing.GetValueOrDefault(key) is { AwaitingConnection: false }) return key;
        var matches = existing.Where(pair => SameConnection(key, source, pair.Key, pair.Value)).ToArray();
        var active = matches.Where(pair => !pair.Value.AwaitingConnection).Take(2).ToArray();
        if (active.Length == 1) return active[0].Key;
        return active.Length == 0 && matches.Length == 1 && !existing.ContainsKey(key) ? matches[0].Key : key;
    }

    internal static string ServiceJ2(string file)
    {
        var fullPath = Path.GetFullPath(file);
        return ServiceComponent.DeploymentName(Path.GetDirectoryName(fullPath)!) + "/" + Path.GetFileName(fullPath);
    }

    internal static void ApplyUnmatchedEntries(Dictionary<string, NetworkTargetSettings> targets, HashSet<string> existingKeys,
        IReadOnlyList<NetworkConnection> connections, List<string> diagnostics)
    {
        var current = connections.Where(connection => IsLiteralKey(connection.TargetKey))
            .Select(connection => connection.TargetKey).ToHashSet(StringComparer.Ordinal);
        var pending = existingKeys.Where(key => targets[key].AwaitingConnection && !current.Contains(key)).ToArray();
        if (current.Count == 0 || pending.Length == 0) return;
        var sourceFiles = ServiceFiles(connections.Select(connection => connection.Service).Distinct());
        var active = current.ToDictionary(key => key, key => FindSource(key, targets[key], null, sourceFiles), StringComparer.Ordinal);
        var resolved = new List<(string PreviousKey, string CurrentKey)>();
        foreach (var key in pending)
        {
            var source = FindSource(key, targets[key], null, sourceFiles);
            var matches = active.Where(pair => SameConnection(key, source, pair.Key, pair.Value))
                .Select(pair => pair.Key).Take(2).ToArray();
            if (matches.Length == 1) resolved.Add((key, matches[0]));
            else if (matches.Length > 1)
                diagnostics.Add($"«{DisplayParameter(key, targets[key])}» — найдено несколько подключений.");
        }
        foreach (var group in resolved.GroupBy(pair => pair.CurrentKey))
        {
            var key = group.Key;
            var target = targets[key];
            var preserveExisting = existingKeys.Contains(key) &&
                (!target.LabelIsAutomatic || !target.BlockIsAutomatic || target.ParentBlock is not null);
            if (preserveExisting || group.Skip(1).Any())
            {
                diagnostics.Add($"«{DisplayParameter(key, target)}» — несколько настроек для одного подключения. " +
                    "Проверьте значения в «Параметры сети…».");
                continue;
            }

            var previousKey = group.First().PreviousKey;
            CopyLabels(targets[previousKey], target);
            targets.Remove(previousKey);
        }
    }

    private static Dictionary<string, string> ServiceFiles(IEnumerable<NetworkServiceInput> services) =>
        services.Where(service => !string.IsNullOrWhiteSpace(service.J2File))
            .GroupBy(service => service.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => ServiceJ2(group.First().J2File), StringComparer.Ordinal);

    private static NetworkParameterSource FindSource(string key, NetworkTargetSettings? target,
        NetworkParameterSource? source, IReadOnlyDictionary<string, string> serviceFiles)
    {
        TryLiteralContext(key, out var serviceId, out _);
        return new NetworkParameterSource(target?.SourceJ2 ?? serviceFiles.GetValueOrDefault(serviceId) ?? source?.J2,
            target?.SourceService ?? source?.Service, target?.UrlPathParameters ?? source?.PathVariables,
            target?.AwaitingConnection ?? source?.AwaitingConnection ?? false);
    }

    private static bool TryLiteralContext(string key, out string serviceId, out string path)
    {
        var separator = IsLiteralKey(key) ? key.IndexOf(':', LiteralPrefix.Length) : -1;
        if (separator <= LiteralPrefix.Length || separator == key.Length - 1)
        {
            serviceId = path = "";
            return false;
        }
        serviceId = key[LiteralPrefix.Length..separator];
        path = key[(separator + 1)..];
        return true;
    }

    private static bool SameConnection(string key, NetworkParameterSource source, string otherKey, NetworkParameterSource other)
    {
        if (!TryLiteralContext(key, out _, out var path) || !TryLiteralContext(otherKey, out _, out var otherPath) ||
            !path.Equals(otherPath, StringComparison.OrdinalIgnoreCase)) return false;
        if (source.J2 is not null && other.J2 is not null)
            return source.J2.Equals(other.J2, StringComparison.OrdinalIgnoreCase);
        return source.Service is not null && source.Service.Equals(other.Service, StringComparison.OrdinalIgnoreCase);
    }

    private static string? Nonempty(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static void CopyLabels(NetworkTargetSettings source, NetworkTargetSettings target)
    {
        target.Label = source.Label;
        target.Block = source.Block;
        target.ParentBlock = source.ParentBlock;
        target.LabelIsAutomatic = source.LabelIsAutomatic;
        target.BlockIsAutomatic = source.BlockIsAutomatic;
    }
}
