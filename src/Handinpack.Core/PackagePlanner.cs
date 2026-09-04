using System.Collections.Immutable;

namespace Handinpack.Core;

public static class PackagePlanner
{
    public static Task<PackagePlan> ScanAsync(IEnumerable<string> sourcePaths, PackageRules? rules = null,
        IProgress<PackageProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourcePaths);
        string[] sources = sourcePaths.ToArray();
        return Task.Run(() => Scan(sources, rules ?? new PackageRules(), progress, cancellationToken), cancellationToken);
    }

    public static PackagePlan Validate(PackagePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(plan.Rules);
        var issues = plan.ScanIssues.IsDefault ? ImmutableArray.CreateBuilder<PackageIssue>() : plan.ScanIssues.ToBuilder();
        var items = ImmutableArray.CreateBuilder<PackageItem>();
        var destinations = new HashSet<string>(StringComparer.Ordinal);
        var directoryNames = new Dictionary<string, string>(StringComparer.Ordinal);
        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        int included = 0;

        foreach (long? limit in new[] { plan.Rules.MaxFileBytes, plan.Rules.MaxTotalBytes, plan.Rules.MaxOutputBytes })
            if (limit < 0)
                issues.Add(new("InvalidRule", "Лимит не может быть отрицательным."));

        if (!plan.Rules.AllowedExtensions.IsDefault)
        {
            foreach (string extension in plan.Rules.AllowedExtensions)
            {
                if (string.IsNullOrWhiteSpace(extension))
                {
                    issues.Add(new("InvalidRule", "Расширение не может быть пустым."));
                    continue;
                }
                string normalized = extension.Trim();
                if (!normalized.StartsWith('.')) normalized = "." + normalized;
                if (normalized.Length < 2 || normalized[1..].Any(character => !char.IsLetterOrDigit(character)))
                    issues.Add(new("InvalidRule", "Расширение должно иметь вид .pdf или .jpg.", extension));
                else extensions.Add(normalized);
            }
        }

        foreach (PackageItem original in plan.Items.IsDefault ? [] : plan.Items)
        {
            if (original is null)
            {
                issues.Add(new("InvalidRule", "План содержит пустую позицию."));
                continue;
            }
            PackageItem item = original;
            if (!PackagePaths.TryNormalize(item.SourceRelativePath, out string relativeSource, out _) || relativeSource != item.SourceRelativePath)
                issues.Add(new("InvalidPath", "В описи нужен безопасный относительный путь исходника.", item.SourcePath));
            if (!item.Included)
            {
                if (string.IsNullOrWhiteSpace(item.ExclusionReason))
                    issues.Add(new("InvalidRule", "Нужна причина исключения.", item.ArchivePath));
                if (item.ExclusionReason is { Length: > 4096 })
                    issues.Add(new("InvalidRule", "Причина исключения превышает 4096 символов.", item.ArchivePath));
                if (!PackagePaths.TryNormalize(item.ArchivePath, out string excludedPath, out string excludedPathError))
                    issues.Add(new("InvalidPath", excludedPathError, item.ArchivePath));
                else item = item with { ArchivePath = excludedPath };
                items.Add(item);
                continue;
            }

            included++;
            if (item.SourceError is not null)
                issues.Add(new("SourceUnavailable", item.SourceError, item.SourcePath));
            if (string.IsNullOrEmpty(item.SourcePath) || !Path.IsPathFullyQualified(item.SourcePath) ||
                string.IsNullOrEmpty(item.SourceRootPath) || !Path.IsPathFullyQualified(item.SourceRootPath) || item.Length < 0)
                issues.Add(new("SourceUnavailable", "Некорректный снимок исходного файла.", item.SourcePath));
            if (!PackagePaths.TryNormalize(item.ArchivePath, out string destination, out string error))
                issues.Add(new("InvalidPath", error, item.ArchivePath));
            else
            {
                item = item with { ArchivePath = destination };
                string key = PackagePaths.Key(destination);
                if (!destinations.Add(key))
                    issues.Add(new("Collision", "Несколько файлов имеют одно имя в комплекте.", destination));
                string[] segments = destination.Split('/');
                for (int index = 1; index < segments.Length; index++)
                {
                    string prefix = string.Join('/', segments.Take(index));
                    string prefixKey = PackagePaths.Key(prefix);
                    if (directoryNames.TryGetValue(prefixKey, out string? previous) && previous != prefix)
                        issues.Add(new("Collision", "Разные написания имени одной папки в комплекте.", destination));
                    else directoryNames.TryAdd(prefixKey, prefix);
                }
                if (extensions.Count > 0 && !extensions.Contains(Path.GetExtension(destination)))
                    issues.Add(new("ExtensionNotAllowed", "Расширение не входит в разрешённый список.", destination));
            }

            if (plan.Rules.MaxFileBytes is long maxFile && item.Length > maxFile)
                issues.Add(new("FileTooLarge", "Файл превышает установленный лимит.", item.ArchivePath));
            try { total = checked(total + Math.Max(0, item.Length)); }
            catch (OverflowException) { issues.Add(new("TotalTooLarge", "Суммарный размер не представим.")); }
            items.Add(item);
        }

        foreach (string key in destinations)
        {
            string[] segments = key.Split('/');
            if (segments[0] == PackagePaths.Key(PackageWriter.ManifestFileName) ||
                segments[0] == PackagePaths.Key(PackageWriter.ContentsFileName))
                issues.Add(new("Collision", "Имя занято служебной описью.", key));
            for (int index = 1; index < segments.Length; index++)
            {
                if (destinations.Contains(string.Join('/', segments.Take(index))))
                    issues.Add(new("Collision", "Один путь используется как файл и папка.", key));
            }
        }

        if (!plan.Rules.RequiredPaths.IsDefault)
        {
            foreach (string path in plan.Rules.RequiredPaths)
            {
                if (!PackagePaths.TryNormalize(path, out string normalized, out string error))
                    issues.Add(new("InvalidRule", error, path));
                else if (!destinations.Contains(PackagePaths.Key(normalized)))
                    issues.Add(new("MissingRequired", "Обязательный файл отсутствует в комплекте.", normalized));
            }
        }

        if (included == 0) issues.Add(new("EmptyPackage", "Нечего собирать."));
        if (included > PackageVerifier.MaxFileCount)
            issues.Add(new("TotalTooLarge", $"Поддерживается не более {PackageVerifier.MaxFileCount} файлов."));
        if (total > PackageVerifier.MaxPayloadBytes || plan.Rules.MaxTotalBytes is long maxTotal && total > maxTotal)
            issues.Add(new("TotalTooLarge", "Материалы превышают суммарный лимит."));
        return plan with { Items = items.ToImmutable(), Issues = issues.ToImmutable(), TotalBytes = total };
    }

    private static PackagePlan Scan(string[] sourcePaths, PackageRules rules, IProgress<PackageProgress>? progress,
        CancellationToken cancellationToken)
    {
        var sources = ImmutableArray.CreateBuilder<string>();
        var items = ImmutableArray.CreateBuilder<PackageItem>();
        var issues = ImmutableArray.CreateBuilder<PackageIssue>();
        var seenRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long discoveredBytes = 0;
        int entriesVisited = 0;

        foreach (string source in sourcePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (items.Count >= PackageVerifier.MaxFileCount)
            {
                issues.Add(new("TotalTooLarge", "Список источников прочитан не полностью: достигнут лимит позиций."));
                break;
            }
            string root;
            try { root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source)); }
            catch (Exception exception) when (PackagePaths.IsIoError(exception))
            {
                issues.Add(new("SourceUnavailable", exception.Message, source));
                continue;
            }
            if (!seenRoots.Add(root)) continue;
            sources.Add(root);
            try
            {
                PackagePaths.CheckAncestors(root);
                FileAttributes rootAttributes = File.GetAttributes(root);
                bool directory = rootAttributes.HasFlag(FileAttributes.Directory);
                string label = Path.GetFileName(root).Replace(':', '_');
                if (string.IsNullOrEmpty(label)) label = "volume";
                if (!directory)
                {
                    AddFile(root, root, label, rootAttributes);
                    continue;
                }

                var pending = new Stack<string>();
                pending.Push(root);
                while (pending.TryPop(out string? folder))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        PackagePaths.CheckAncestors(folder);
                        foreach (string entry in Directory.EnumerateFileSystemEntries(folder, "*", new EnumerationOptions
                        {
                            AttributesToSkip = 0,
                            IgnoreInaccessible = false,
                            RecurseSubdirectories = false
                        }))
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            if (items.Count >= PackageVerifier.MaxFileCount || ++entriesVisited > PackageVerifier.MaxFileCount * 2)
                            {
                                issues.Add(new("TotalTooLarge", "Папка прочитана не полностью: достигнут лимит позиций.", root));
                                return Validate(new(sources.ToImmutable(), items.ToImmutable(), rules,
                                    issues.ToImmutable(), [], discoveredBytes));
                            }
                            string relative = label + "/" + Path.GetRelativePath(root, entry).Replace('\\', '/');
                            try
                            {
                                FileAttributes attributes = File.GetAttributes(entry);
                                if (PackagePaths.Unsupported(attributes)) AddFile(entry, root, relative, attributes);
                                else if (attributes.HasFlag(FileAttributes.Directory)) pending.Push(entry);
                                else AddFile(entry, root, relative, attributes);
                            }
                            catch (Exception exception) when (PackagePaths.IsIoError(exception))
                            {
                                items.Add(new(entry, root, relative, relative, 0, DateTime.MinValue,
                                    SourceError: exception.Message));
                            }
                        }
                    }
                    catch (Exception exception) when (PackagePaths.IsIoError(exception))
                    {
                        issues.Add(new("SourceUnavailable", "Папка прочитана не полностью: " + exception.Message, folder));
                    }
                }
            }
            catch (Exception exception) when (PackagePaths.IsIoError(exception))
            {
                issues.Add(new("SourceUnavailable", exception.Message, root));
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return Validate(new(sources.ToImmutable(), items.ToImmutable(), rules, issues.ToImmutable(), [], discoveredBytes));

        void AddFile(string path, string root, string relative, FileAttributes attributes)
        {
            if (!seenFiles.Add(path)) return;
            if (PackagePaths.Unsupported(attributes))
                items.Add(new(path, root, relative, relative, 0, DateTime.MinValue, false,
                    "Ссылка или облачный заполнитель не поддерживается.", "Этот тип исходника нельзя включить."));
            else
            {
                var info = new FileInfo(path);
                items.Add(new(path, root, relative, relative, info.Length, info.LastWriteTimeUtc));
                discoveredBytes = checked(discoveredBytes + info.Length);
            }
            progress?.Report(new(PackagePhase.Scanning, relative, items.Count, 0, discoveredBytes, 0));
            cancellationToken.ThrowIfCancellationRequested();
        }
    }
}
