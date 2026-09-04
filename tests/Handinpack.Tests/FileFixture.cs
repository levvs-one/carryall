using System.IO;
using System.Security.Cryptography;

[assembly: DoNotParallelize]

namespace Handinpack.Tests;

internal sealed class FileFixture : IDisposable
{
    private readonly string _baseDirectory;

    public FileFixture(string? baseDirectory = null)
    {
        _baseDirectory = Path.GetFullPath(baseDirectory ?? Environment.GetEnvironmentVariable("HANDINPACK_TEST_ROOT") ??
            Path.Combine(Path.GetTempPath(), "handinpack-tests"));
        Directory.CreateDirectory(_baseDirectory);
        Root = Path.Combine(_baseDirectory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }
    public List<string> DirectoryLinks { get; } = [];

    public string WriteFile(string relativePath, byte[] contents)
    {
        string destination = Path.Combine(Root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        using FileStream stream = new(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(contents);
        return destination;
    }

    public static Dictionary<string, FileSnapshot> Snapshot(string directory) =>
        Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .ToDictionary(path => Path.GetRelativePath(directory, path), path => new FileSnapshot(
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))),
                new FileInfo(path).Length, File.GetLastWriteTimeUtc(path), File.GetAttributes(path)),
                StringComparer.Ordinal);

    public void Dispose()
    {
        string resolved = Path.GetFullPath(Root);
        string? parent = Directory.GetParent(resolved)?.FullName;
        if (!string.Equals(parent, _baseDirectory, StringComparison.OrdinalIgnoreCase) ||
            !Guid.TryParseExact(Path.GetFileName(resolved), "N", out _))
        {
            throw new InvalidOperationException("Refusing to remove a directory outside this test fixture.");
        }

        foreach (string link in DirectoryLinks)
        {
            string resolvedLink = Path.GetFullPath(link);
            if (!resolvedLink.StartsWith(resolved + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                !File.GetAttributes(resolvedLink).HasFlag(FileAttributes.ReparsePoint))
            {
                throw new InvalidOperationException("Refusing to remove an unexpected directory link.");
            }

            Directory.Delete(resolvedLink, recursive: false);
        }

        Directory.Delete(resolved, recursive: true);
    }
}

internal sealed record FileSnapshot(string Sha256, long Length, DateTime LastWriteTimeUtc, FileAttributes Attributes);
