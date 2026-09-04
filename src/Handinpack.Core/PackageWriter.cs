using System.Buffers;
using System.Collections.Immutable;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Handinpack.Core;

public static class PackageWriter
{
    public const string ManifestFileName = "handinpack.json";
    public const string ContentsFileName = "contents.txt";
    internal const int BufferSize = 128 * 1024;

    public static async Task<PackageResult> WriteAsync(PackagePlan plan, string outputPath, PackageFormat format,
        IProgress<PackageProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        string? partialPath = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            PackagePlan validated = PackagePlanner.Validate(plan);
            if (!validated.CanBuild)
                throw new InvalidOperationException(string.Join(Environment.NewLine, validated.Issues.Select(issue => issue.Message)));
            if (!Enum.IsDefined(format)) throw new ArgumentOutOfRangeException(nameof(format));
            string finalPath = Path.GetFullPath(outputPath);
            string? parent = Path.GetDirectoryName(finalPath);
            if (parent is null || !Directory.Exists(parent))
                throw new IOException("Выберите существующую родительскую папку результата.");
            if (!PackagePaths.TryNormalize(Path.GetFileName(finalPath), out _, out string pathError))
                throw new IOException(pathError);
            PackagePaths.CheckAncestors(parent);
            if (Path.Exists(finalPath)) throw new IOException("Итоговый путь уже существует. Перезапись запрещена.");

            var sourceFolders = validated.Items.Where(item => item.SourceRootPath != item.SourcePath)
                .Select(item => item.SourceRootPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (string source in validated.SourcePaths.Concat(validated.Items.Select(item => item.SourceRootPath))
                .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                bool sourceFolder = sourceFolders.Contains(source) || Directory.Exists(source);
                if (sourceFolder ? PackagePaths.IsWithin(finalPath, source) :
                    finalPath.Equals(Path.GetFullPath(source), StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Результат не может находиться в исходной папке или заменять исходный файл.");
            }
            foreach (PackageItem item in validated.Items)
                if (finalPath.Equals(Path.GetFullPath(item.SourcePath), StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Результат не может заменять исходный файл.");

            partialPath = Path.Combine(parent, ".handinpack-" + Guid.NewGuid().ToString("N") + ".incomplete");
            var included = validated.Items.Where(item => item.Included).OrderBy(item => item.ArchivePath, StringComparer.Ordinal).ToArray();
            var written = ImmutableArray.CreateBuilder<ManifestFile>();
            long copiedBytes = 0;
            if (format == PackageFormat.Folder)
            {
                if (Path.Exists(partialPath)) throw new IOException("Временный путь уже занят.");
                Directory.CreateDirectory(partialPath);
                for (int index = 0; index < included.Length; index++)
                {
                    PackageItem item = included[index];
                    string destination = Path.Combine(partialPath, item.ArchivePath.Replace('/', Path.DirectorySeparatorChar));
                    PackagePaths.CheckAncestors(destination);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    PackagePaths.CheckAncestors(destination);
                    await using var output = new FileStream(destination, NewOutputOptions());
                    written.Add(await CopySourceAsync(item, output, index, included.Length, copiedBytes,
                        validated.TotalBytes, progress, cancellationToken).ConfigureAwait(false));
                    copiedBytes += item.Length;
                    await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                    output.Flush(true);
                }
                (byte[] manifest, byte[] contents) = CreateMetadata(written.ToImmutable(), validated.Items);
                await WriteFolderMetadataAsync(partialPath, ManifestFileName, manifest, cancellationToken).ConfigureAwait(false);
                await WriteFolderMetadataAsync(partialPath, ContentsFileName, contents, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await using (var output = new FileStream(partialPath, NewOutputOptions()))
                {
                    await using (ZipArchive archive = await ZipArchive.CreateAsync(output, ZipArchiveMode.Create,
                        true, Encoding.UTF8, cancellationToken).ConfigureAwait(false))
                    {
                        for (int index = 0; index < included.Length; index++)
                        {
                            PackageItem item = included[index];
                            ZipArchiveEntry entry = archive.CreateEntry(item.ArchivePath, CompressionLevel.Optimal);
                            await using Stream entryStream = await entry.OpenAsync(cancellationToken).ConfigureAwait(false);
                            written.Add(await CopySourceAsync(item, entryStream, index, included.Length, copiedBytes,
                                validated.TotalBytes, progress, cancellationToken).ConfigureAwait(false));
                            copiedBytes += item.Length;
                        }
                        (byte[] manifest, byte[] contents) = CreateMetadata(written.ToImmutable(), validated.Items);
                        foreach ((string name, byte[] bytes) in new[] { (ManifestFileName, manifest), (ContentsFileName, contents) })
                        {
                            ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.Optimal);
                            await using Stream entryStream = await entry.OpenAsync(cancellationToken).ConfigureAwait(false);
                            await entryStream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                        }
                    }
                    await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                    output.Flush(true);
                }
            }

            long outputBytes = format == PackageFormat.Zip ? new FileInfo(partialPath).Length :
                checked(copiedBytes + new FileInfo(Path.Combine(partialPath, ManifestFileName)).Length +
                    new FileInfo(Path.Combine(partialPath, ContentsFileName)).Length);
            if (validated.Rules.MaxOutputBytes is long maxOutput && outputBytes > maxOutput)
                throw new IOException("OutputTooLarge: готовая запись с описью превышает лимит размера результата.");

            VerificationResult verification = await PackageVerifier.VerifyAsync(partialPath, format, progress,
                cancellationToken).ConfigureAwait(false);
            if (!verification.IsValid)
                throw new IOException("Проверка не сошлась: " + string.Join("; ", verification.Issues.Select(issue => issue.Message)));
            progress?.Report(new(PackagePhase.Finalizing, finalPath, included.Length, included.Length, copiedBytes, copiedBytes));
            cancellationToken.ThrowIfCancellationRequested();
            PackagePaths.CheckAncestors(parent);
            if (format == PackageFormat.Zip) File.Move(partialPath, finalPath, false);
            else Directory.Move(partialPath, finalPath);
            return new(finalPath, format, included.Length, copiedBytes, outputBytes, DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException)
        {
            throw new PackageCanceledException(partialPath, cancellationToken);
        }
        catch (Exception exception) when (PackagePaths.IsIoError(exception) || exception is InvalidOperationException or JsonException)
        {
            throw new PackageWriteException(exception.Message, partialPath, exception);
        }
    }

    private static FileStreamOptions NewOutputOptions() => new()
    {
        Mode = FileMode.CreateNew,
        Access = FileAccess.Write,
        Share = FileShare.None,
        Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
        BufferSize = BufferSize
    };

    private static async Task<ManifestFile> CopySourceAsync(PackageItem item, Stream destination, int index,
        int totalFiles, long priorBytes, long totalBytes, IProgress<PackageProgress>? progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        PackagePaths.CheckAncestors(item.SourcePath);
        await using var source = new FileStream(item.SourcePath, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.Read,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
            BufferSize = BufferSize
        });
        CheckSnapshot();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        long bytes = 0;
        try
        {
            progress?.Report(new(PackagePhase.Copying, item.ArchivePath, index, totalFiles, priorBytes, totalBytes));
            cancellationToken.ThrowIfCancellationRequested();
            int count;
            while ((count = await source.ReadAsync(buffer.AsMemory(0, BufferSize), cancellationToken).ConfigureAwait(false)) != 0)
            {
                bytes += count;
                if (bytes > item.Length) throw new IOException("SourceChanged: исходник вырос после сканирования: " + item.SourcePath);
                await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                hash.AppendData(buffer, 0, count);
                progress?.Report(new(PackagePhase.Copying, item.ArchivePath, index, totalFiles, priorBytes + bytes, totalBytes));
                cancellationToken.ThrowIfCancellationRequested();
            }
            if (bytes != item.Length) throw new IOException("SourceChanged: размер исходника изменился: " + item.SourcePath);
            CheckSnapshot();
            progress?.Report(new(PackagePhase.Copying, item.ArchivePath, index + 1, totalFiles, priorBytes + bytes, totalBytes));
            cancellationToken.ThrowIfCancellationRequested();
            return new(item.SourceRelativePath, item.ArchivePath, bytes, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }

        void CheckSnapshot()
        {
            if (PackagePaths.Unsupported(File.GetAttributes(source.SafeFileHandle)) ||
                RandomAccess.GetLength(source.SafeFileHandle) != item.Length ||
                File.GetLastWriteTimeUtc(source.SafeFileHandle) != item.LastWriteTimeUtc)
                throw new IOException("SourceChanged: исходник изменился после сканирования: " + item.SourcePath);
        }
    }

    private static (byte[] Manifest, byte[] Contents) CreateMetadata(ImmutableArray<ManifestFile> files,
        ImmutableArray<PackageItem> items)
    {
        var exclusions = items.Where(item => !item.Included).Select(item => new ManifestExclusion(
            item.SourceRelativePath, item.ArchivePath, item.ExclusionReason!)).ToImmutableArray();
        var text = new StringBuilder();
        int contentsBytes = 0;
        long jsonBudget = 2048;
        AppendContents("Handinpack - опись комплекта\n\n");
        foreach (ManifestFile file in files)
        {
            jsonBudget += JsonSerializer.SerializeToUtf8Bytes(file, PackageManifest.JsonOptions).Length + 128;
            if (jsonBudget > PackageVerifier.MaxManifestBytes) throw new IOException("Опись превышает безопасный предел 16 MiB.");
            AppendContents(file.ArchivePath + "\n  Исходник: " + file.SourceRelativePath + "\n  Байт: " +
                file.Length.ToString(CultureInfo.InvariantCulture) + "\n  SHA-256: " + file.Sha256 + "\n");
        }
        if (!exclusions.IsEmpty)
        {
            AppendContents("\nИсключены:\n");
            foreach (ManifestExclusion excluded in exclusions)
            {
                jsonBudget += JsonSerializer.SerializeToUtf8Bytes(excluded, PackageManifest.JsonOptions).Length + 128;
                if (jsonBudget > PackageVerifier.MaxManifestBytes) throw new IOException("Опись превышает безопасный предел 16 MiB.");
                AppendContents(excluded.SourceRelativePath + " - " + excluded.Reason.Replace('\r', ' ').Replace('\n', ' ') + "\n");
            }
        }
        byte[] contents = Encoding.UTF8.GetBytes(text.ToString());
        var manifest = new PackageManifest(1, DateTimeOffset.UtcNow, files, exclusions, contents.Length,
            Convert.ToHexString(SHA256.HashData(contents)).ToLowerInvariant());
        byte[] manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, PackageManifest.JsonOptions);
        if (manifestBytes.Length > PackageVerifier.MaxManifestBytes || contents.Length > PackageVerifier.MaxManifestBytes)
            throw new IOException("Опись превышает безопасный предел 16 MiB.");
        return (manifestBytes, contents);

        void AppendContents(string value)
        {
            contentsBytes = checked(contentsBytes + Encoding.UTF8.GetByteCount(value));
            if (contentsBytes > PackageVerifier.MaxManifestBytes) throw new IOException("Читаемая опись превышает 16 MiB.");
            text.Append(value);
        }
    }

    private static async Task WriteFolderMetadataAsync(string folder, string name, byte[] bytes, CancellationToken cancellationToken)
    {
        string path = Path.Combine(folder, name);
        PackagePaths.CheckAncestors(path);
        await using var stream = new FileStream(path, NewOutputOptions());
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        stream.Flush(true);
    }
}
