using System.Collections.Immutable;

namespace Handinpack.Core;

public enum PackageFormat { Folder, Zip }

public enum PackagePhase { Scanning, Copying, Verifying, Finalizing }

public sealed record PackageItem(
    string SourcePath,
    string SourceRootPath,
    string SourceRelativePath,
    string ArchivePath,
    long Length,
    DateTime LastWriteTimeUtc,
    bool Included = true,
    string? ExclusionReason = null,
    string? SourceError = null);

public sealed record PackageRules(
    ImmutableArray<string> AllowedExtensions = default,
    long? MaxFileBytes = null,
    long? MaxTotalBytes = null,
    long? MaxOutputBytes = null,
    ImmutableArray<string> RequiredPaths = default);

public sealed record PackageIssue(string Code, string Message, string? Path = null);

public sealed record PackagePlan(
    ImmutableArray<string> SourcePaths,
    ImmutableArray<PackageItem> Items,
    PackageRules Rules,
    ImmutableArray<PackageIssue> ScanIssues,
    ImmutableArray<PackageIssue> Issues,
    long TotalBytes)
{
    public bool CanBuild => Issues.IsEmpty && Items.Any(item => item.Included);
}

public sealed record PackageProgress(
    PackagePhase Phase,
    string? Path,
    int FilesCompleted,
    int TotalFiles,
    long BytesCompleted,
    long TotalBytes);

public sealed record PackageResult(
    string OutputPath,
    PackageFormat Format,
    int FileCount,
    long PayloadBytes,
    long OutputBytes,
    DateTimeOffset VerifiedAt);

public sealed record VerificationResult(
    bool IsValid,
    ImmutableArray<PackageIssue> Issues,
    int FileCount,
    long TotalBytes);

public sealed class PackageWriteException(string message, string? partialPath, Exception innerException)
    : IOException(message, innerException)
{
    public string? PartialPath { get; } = partialPath;
}

public sealed class PackageCanceledException(string? partialPath, CancellationToken cancellationToken)
    : OperationCanceledException("Сборка отменена. Итоговый комплект не создан.", cancellationToken)
{
    public string? PartialPath { get; } = partialPath;
}
