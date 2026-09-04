using System.Buffers;
using System.Collections.Immutable;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Handinpack.Core;

public static class PackageVerifier
{
    public const int MaxManifestBytes = 16 * 1024 * 1024;
    public const int MaxFileCount = 100_000;
    public const long MaxPayloadBytes = 1024L * 1024 * 1024 * 1024;

    public static Task<VerificationResult> VerifyAsync(string packagePath, PackageFormat format,
        IProgress<PackageProgress>? progress = null, CancellationToken cancellationToken = default) =>
        Task.Run(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (!Enum.IsDefined(format)) throw new ArgumentOutOfRangeException(nameof(format));
                PackagePaths.CheckAncestors(packagePath);
                return format == PackageFormat.Folder
                    ? await VerifyFolderAsync(packagePath, progress, cancellationToken).ConfigureAwait(false)
                    : await VerifyZipAsync(packagePath, progress, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (PackagePaths.IsIoError(exception) || exception is JsonException or
                InvalidOperationException or OverflowException)
            {
                return new VerificationResult(false, [new("InvalidArchive", exception.Message, packagePath)], 0, 0);
            }
        }, cancellationToken);

    private static async Task<VerificationResult> VerifyFolderAsync(string path, IProgress<PackageProgress>? progress,
        CancellationToken cancellationToken)
    {
        var issues = ImmutableArray.CreateBuilder<PackageIssue>();
        var actual = new Dictionary<string, (string Relative, string FullPath)>(StringComparer.Ordinal);
        var directories = new List<string>();
        var directoryKeys = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>();
        string root = Path.GetFullPath(path);
        pending.Push(root);
        int observed = 0;
        while (pending.TryPop(out string? folder))
        {
            cancellationToken.ThrowIfCancellationRequested();
            PackagePaths.CheckAncestors(folder);
            foreach (string entry in Directory.EnumerateFileSystemEntries(folder, "*", new EnumerationOptions
            {
                AttributesToSkip = 0,
                IgnoreInaccessible = false,
                RecurseSubdirectories = false
            }))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++observed > MaxFileCount * 2 + 2) throw new IOException("Слишком много файлов или папок в комплекте.");
                string relative = Path.GetRelativePath(root, entry).Replace('\\', '/');
                FileAttributes attributes = File.GetAttributes(entry);
                if (PackagePaths.Unsupported(attributes))
                {
                    issues.Add(new("SourceUnsupported", "В комплекте обнаружена ссылка или облачный заполнитель.", relative));
                    continue;
                }
                if (!PackagePaths.TryNormalize(relative, out string normalized, out string error) || normalized != relative)
                {
                    issues.Add(new("InvalidPath", string.IsNullOrEmpty(error)
                        ? "Путь должен использовать только '/' как разделитель папок." : error, relative));
                    continue;
                }
                if (attributes.HasFlag(FileAttributes.Directory))
                {
                    if (!directoryKeys.Add(PackagePaths.Key(relative)))
                        issues.Add(new("DuplicateEntry", "Неоднозначные имена папок.", relative));
                    directories.Add(relative);
                    pending.Push(entry);
                }
                else if (!actual.TryAdd(PackagePaths.Key(relative), (relative, entry)))
                    issues.Add(new("DuplicateEntry", "Неоднозначные имена файлов.", relative));
            }
        }

        if (!actual.TryGetValue(PackagePaths.Key(PackageWriter.ManifestFileName), out var manifestEntry))
        {
            issues.Add(new("MissingEntry", "Отсутствует handinpack.json.", PackageWriter.ManifestFileName));
            return new(false, issues.ToImmutable(), 0, 0);
        }
        PackageManifest? manifest;
        await using (FileStream manifestStream = OpenRead(manifestEntry.FullPath))
            manifest = await ReadManifestAsync(manifestStream, manifestStream.Length, issues, cancellationToken).ConfigureAwait(false);
        if (manifest is null) return new(false, issues.ToImmutable(), 0, 0);

        Dictionary<string, ManifestFile> expected = ExpectedFiles(manifest);
        CheckComposition(expected, actual.ToDictionary(pair => pair.Key, pair => pair.Value.Relative), issues);
        var expectedDirectories = new HashSet<string>(StringComparer.Ordinal);
        foreach (ManifestFile file in manifest.Files)
        {
            string[] segments = file.ArchivePath.Split('/');
            for (int index = 1; index < segments.Length; index++)
                expectedDirectories.Add(PackagePaths.Key(string.Join('/', segments.Take(index))));
        }
        foreach (string directory in directories)
            if (!expectedDirectories.Contains(PackagePaths.Key(directory)))
                issues.Add(new("ExtraEntry", "Папка не указана в составе комплекта.", directory));

        long totalBytes = manifest.Files.Sum(file => file.Length);
        long bytes = 0;
        int files = 0;
        foreach (ManifestFile file in manifest.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!actual.TryGetValue(PackagePaths.Key(file.ArchivePath), out var entry)) continue;
            await using FileStream stream = OpenRead(entry.FullPath);
            if (stream.Length != file.Length)
            {
                issues.Add(new("SizeMismatch", "Размер не совпадает с описью.", file.ArchivePath));
                continue;
            }
            (long length, string hash) = await HashStreamAsync(stream, file.Length, file.ArchivePath, files,
                manifest.Files.Length, bytes, totalBytes, progress, cancellationToken).ConfigureAwait(false);
            CheckHash(file, length, hash, issues);
            bytes += length;
            files++;
        }
        if (actual.TryGetValue(PackagePaths.Key(PackageWriter.ContentsFileName), out var contents))
        {
            await using FileStream stream = OpenRead(contents.FullPath);
            (long length, string hash) = await HashStreamAsync(stream, manifest.ContentsLength, PackageWriter.ContentsFileName,
                0, 0, 0, 0, null, cancellationToken).ConfigureAwait(false);
            CheckHash(expected[PackagePaths.Key(PackageWriter.ContentsFileName)], length, hash, issues);
        }
        return new(issues.Count == 0, issues.ToImmutable(), files, bytes);
    }

    private static async Task<VerificationResult> VerifyZipAsync(string path, IProgress<PackageProgress>? progress,
        CancellationToken cancellationToken)
    {
        var issues = ImmutableArray.CreateBuilder<PackageIssue>();
        await using FileStream input = OpenRead(path);
        await using ZipArchive archive = await ZipArchive.CreateAsync(input, ZipArchiveMode.Read, true, Encoding.UTF8,
            cancellationToken).ConfigureAwait(false);
        if (archive.Entries.Count > MaxFileCount + 2) throw new IOException("Слишком много записей в ZIP.");
        var actual = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!PackagePaths.TryNormalize(entry.FullName, out string normalized, out string error) || normalized != entry.FullName)
            {
                issues.Add(new("InvalidPath", string.IsNullOrEmpty(error)
                    ? "Путь ZIP должен использовать только '/' как разделитель папок." : error, entry.FullName));
                continue;
            }
            int unixType = (entry.ExternalAttributes >> 16) & 0xf000;
            if (unixType == 0xa000 || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
            {
                issues.Add(new("SourceUnsupported", "Ссылки в ZIP не поддерживаются.", entry.FullName));
                continue;
            }
            if (!actual.TryAdd(PackagePaths.Key(entry.FullName), entry))
                issues.Add(new("DuplicateEntry", "Повторяющееся имя записи ZIP.", entry.FullName));
        }
        if (!actual.TryGetValue(PackagePaths.Key(PackageWriter.ManifestFileName), out ZipArchiveEntry? manifestEntry))
        {
            issues.Add(new("MissingEntry", "Отсутствует handinpack.json.", PackageWriter.ManifestFileName));
            return new(false, issues.ToImmutable(), 0, 0);
        }
        PackageManifest? manifest;
        await using (Stream manifestStream = await manifestEntry.OpenAsync(cancellationToken).ConfigureAwait(false))
            manifest = await ReadManifestAsync(manifestStream, manifestEntry.Length, issues, cancellationToken).ConfigureAwait(false);
        if (manifest is null) return new(false, issues.ToImmutable(), 0, 0);

        Dictionary<string, ManifestFile> expected = ExpectedFiles(manifest);
        CheckComposition(expected, actual.ToDictionary(pair => pair.Key, pair => pair.Value.FullName), issues);
        long totalBytes = manifest.Files.Sum(file => file.Length);
        long bytes = 0;
        int files = 0;
        foreach (ManifestFile file in manifest.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!actual.TryGetValue(PackagePaths.Key(file.ArchivePath), out ZipArchiveEntry? entry)) continue;
            if (entry.Length != file.Length)
            {
                issues.Add(new("SizeMismatch", "Размер записи ZIP не совпадает с описью.", file.ArchivePath));
                continue;
            }
            await using Stream stream = await entry.OpenAsync(cancellationToken).ConfigureAwait(false);
            (long length, string hash) = await HashStreamAsync(stream, file.Length, file.ArchivePath, files,
                manifest.Files.Length, bytes, totalBytes, progress, cancellationToken).ConfigureAwait(false);
            CheckHash(file, length, hash, issues);
            bytes += length;
            files++;
        }
        if (actual.TryGetValue(PackagePaths.Key(PackageWriter.ContentsFileName), out ZipArchiveEntry? contents))
        {
            await using Stream stream = await contents.OpenAsync(cancellationToken).ConfigureAwait(false);
            (long length, string hash) = await HashStreamAsync(stream, manifest.ContentsLength, PackageWriter.ContentsFileName,
                0, 0, 0, 0, null, cancellationToken).ConfigureAwait(false);
            CheckHash(expected[PackagePaths.Key(PackageWriter.ContentsFileName)], length, hash, issues);
        }
        return new(issues.Count == 0, issues.ToImmutable(), files, bytes);
    }

    private static async Task<PackageManifest?> ReadManifestAsync(Stream stream, long length,
        ImmutableArray<PackageIssue>.Builder issues, CancellationToken cancellationToken)
    {
        try
        {
            if (length is < 1 or > MaxManifestBytes) throw new JsonException("Размер manifest должен быть от 1 байта до 16 MiB.");
            byte[] bytes = new byte[(int)length];
            await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
            byte[] extra = new byte[1];
            if (await stream.ReadAsync(extra, cancellationToken).ConfigureAwait(false) != 0)
                throw new JsonException("Реальный размер manifest больше заявленного.");
            PackageManifest manifest = JsonSerializer.Deserialize<PackageManifest>(bytes, PackageManifest.JsonOptions)
                ?? throw new JsonException("Пустой manifest.");
            if (manifest.SchemaVersion != 1 || manifest.Files.IsDefaultOrEmpty || manifest.Files.Length > MaxFileCount ||
                manifest.Exclusions.IsDefault || manifest.Exclusions.Length > MaxFileCount ||
                manifest.ContentsLength is < 0 or > MaxManifestBytes || !ValidHash(manifest.ContentsSha256))
                throw new JsonException("Неизвестная версия или некорректные поля manifest.");

            var names = new HashSet<string>(StringComparer.Ordinal);
            var directoryNames = new Dictionary<string, string>(StringComparer.Ordinal);
            long total = 0;
            foreach (ManifestFile file in manifest.Files)
            {
                if (file is null || !PackagePaths.TryNormalize(file.ArchivePath, out string path, out _) || path != file.ArchivePath ||
                    !PackagePaths.TryNormalize(file.SourceRelativePath, out string source, out _) || source != file.SourceRelativePath ||
                    file.Length is < 0 or > MaxPayloadBytes || !ValidHash(file.Sha256))
                    throw new JsonException("Некорректное описание файла в manifest.");
                string key = PackagePaths.Key(path);
                if (!names.Add(key)) throw new JsonException("Повторяющийся путь в manifest: " + path);
                string[] pathSegments = path.Split('/');
                for (int index = 1; index < pathSegments.Length; index++)
                {
                    string prefix = string.Join('/', pathSegments.Take(index));
                    string prefixKey = PackagePaths.Key(prefix);
                    if (directoryNames.TryGetValue(prefixKey, out string? previous) && previous != prefix)
                        throw new JsonException("Неоднозначные имена папок в manifest.");
                    directoryNames.TryAdd(prefixKey, prefix);
                }
                if (key.Split('/')[0] == PackagePaths.Key(PackageWriter.ManifestFileName) ||
                    key.Split('/')[0] == PackagePaths.Key(PackageWriter.ContentsFileName))
                    throw new JsonException("Путь файла пересекается со служебной описью.");
                total = checked(total + file.Length);
                if (total > MaxPayloadBytes) throw new JsonException("Размер материалов превышает 1 TiB.");
            }
            foreach (string name in names)
            {
                string[] segments = name.Split('/');
                for (int index = 1; index < segments.Length; index++)
                    if (names.Contains(string.Join('/', segments.Take(index))))
                        throw new JsonException("Путь одновременно используется как файл и папка.");
            }
            foreach (ManifestExclusion excluded in manifest.Exclusions)
                if (excluded is null || string.IsNullOrWhiteSpace(excluded.Reason) ||
                    !PackagePaths.TryNormalize(excluded.SourceRelativePath, out string source, out _) || source != excluded.SourceRelativePath ||
                    !PackagePaths.TryNormalize(excluded.ArchivePath, out string target, out _) || target != excluded.ArchivePath)
                    throw new JsonException("Некорректное исключение в manifest.");
            return manifest;
        }
        catch (Exception exception) when (exception is JsonException or IOException or OverflowException)
        {
            issues.Add(new("InvalidManifest", exception.Message, PackageWriter.ManifestFileName));
            return null;
        }
    }

    private static bool ValidHash(string? value) => value is { Length: 64 } &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');

    private static Dictionary<string, ManifestFile> ExpectedFiles(PackageManifest manifest)
    {
        var result = manifest.Files.ToDictionary(file => PackagePaths.Key(file.ArchivePath));
        result.Add(PackagePaths.Key(PackageWriter.ManifestFileName), new("", PackageWriter.ManifestFileName, 0, ""));
        result.Add(PackagePaths.Key(PackageWriter.ContentsFileName), new("", PackageWriter.ContentsFileName,
            manifest.ContentsLength, manifest.ContentsSha256));
        return result;
    }

    private static void CheckComposition(Dictionary<string, ManifestFile> expected, Dictionary<string, string> actual,
        ImmutableArray<PackageIssue>.Builder issues)
    {
        foreach ((string key, ManifestFile file) in expected)
            if (!actual.ContainsKey(key)) issues.Add(new("MissingEntry", "Файл из описи отсутствует.", file.ArchivePath));
        foreach ((string key, string path) in actual)
            if (!expected.ContainsKey(key)) issues.Add(new("ExtraEntry", "Файл не указан в описи.", path));
    }

    private static void CheckHash(ManifestFile expected, long length, string hash, ImmutableArray<PackageIssue>.Builder issues)
    {
        if (length != expected.Length)
            issues.Add(new("SizeMismatch", "Фактическая длина не совпадает с описью.", expected.ArchivePath));
        if (!hash.Equals(expected.Sha256, StringComparison.OrdinalIgnoreCase))
            issues.Add(new("HashMismatch", "SHA-256 не совпадает с описью.", expected.ArchivePath));
    }

    private static FileStream OpenRead(string path)
    {
        PackagePaths.CheckAncestors(path);
        var stream = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.Read,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
            BufferSize = PackageWriter.BufferSize
        });
        try
        {
            if (!PackagePaths.Unsupported(File.GetAttributes(stream.SafeFileHandle))) return stream;
            throw new IOException("Ссылки и облачные заполнители не поддерживаются: " + path);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private static async Task<(long Length, string Hash)> HashStreamAsync(Stream stream, long expectedLength,
        string path, int files, int totalFiles, long priorBytes, long totalBytes, IProgress<PackageProgress>? progress,
        CancellationToken cancellationToken)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(PackageWriter.BufferSize);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long bytes = 0;
        try
        {
            progress?.Report(new(PackagePhase.Verifying, path, files, totalFiles, priorBytes, totalBytes));
            cancellationToken.ThrowIfCancellationRequested();
            while (true)
            {
                int request = (int)Math.Min(PackageWriter.BufferSize, expectedLength - bytes + 1);
                int count = await stream.ReadAsync(buffer.AsMemory(0, request), cancellationToken).ConfigureAwait(false);
                if (count == 0) break;
                bytes += count;
                if (bytes > expectedLength) throw new IOException("SizeMismatch: реальная длина больше заявленной: " + path);
                hash.AppendData(buffer, 0, count);
                progress?.Report(new(PackagePhase.Verifying, path, files, totalFiles, priorBytes + bytes, totalBytes));
                cancellationToken.ThrowIfCancellationRequested();
            }
            progress?.Report(new(PackagePhase.Verifying, path, files + 1, totalFiles, priorBytes + bytes, totalBytes));
            cancellationToken.ThrowIfCancellationRequested();
            return (bytes, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }
}
