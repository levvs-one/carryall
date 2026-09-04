using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Handinpack.Core;

internal sealed record ManifestFile(string SourceRelativePath, string ArchivePath, long Length, string Sha256);

internal sealed record ManifestExclusion(string SourceRelativePath, string ArchivePath, string Reason);

internal sealed record PackageManifest(int SchemaVersion, DateTimeOffset CreatedUtc,
    ImmutableArray<ManifestFile> Files, ImmutableArray<ManifestExclusion> Exclusions,
    long ContentsLength, string ContentsSha256)
{
    internal static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        AllowDuplicateProperties = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        MaxDepth = 16
    };
}
