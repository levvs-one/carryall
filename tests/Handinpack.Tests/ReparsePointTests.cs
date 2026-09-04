using System.Diagnostics;
using Handinpack.Core;

namespace Handinpack.Tests;

[TestClass]
public sealed class ReparsePointTests
{
    private static readonly string? ReparseFixtureRoot = OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp", "handinpack-reparse-tests")
        : null;

    [TestMethod]
    public async Task ScanReportsJunctionWithoutReadingItsTarget()
    {
        using FileFixture fixture = new(ReparseFixtureRoot);
        fixture.WriteFile("source/ordinary.txt", [1]);
        fixture.WriteFile("outside/private.txt", [2, 3]);
        string source = Path.Combine(fixture.Root, "source");
        string link = Path.Combine(source, "linked");
        await CreateDirectoryLink(link, Path.Combine(fixture.Root, "outside"));
        fixture.DirectoryLinks.Add(link);

        PackagePlan plan = await PackagePlanner.ScanAsync([source]);

        Assert.AreEqual(1, plan.Items.Count(item => item.Included));
        Assert.IsFalse(plan.Items.Any(item => item.SourcePath.EndsWith("private.txt", StringComparison.Ordinal)));
        PackageItem excluded = plan.Items.Single(item => item.SourcePath == link);
        Assert.IsFalse(excluded.Included);
        Assert.IsFalse(string.IsNullOrWhiteSpace(excluded.ExclusionReason));
        CollectionAssert.AreEqual(new byte[] { 2, 3 }, File.ReadAllBytes(Path.Combine(fixture.Root, "outside/private.txt")));
    }

    [TestMethod]
    public async Task DirectlySelectedFileBehindJunctionIsRejected()
    {
        using FileFixture fixture = new(ReparseFixtureRoot);
        string source = fixture.WriteFile("outside/private.txt", [1, 2, 3]);
        string link = Path.Combine(fixture.Root, "selected-link");
        await CreateDirectoryLink(link, Path.GetDirectoryName(source)!);
        fixture.DirectoryLinks.Add(link);

        PackagePlan plan = await PackagePlanner.ScanAsync([Path.Combine(link, "private.txt")]);

        Assert.IsFalse(plan.CanBuild);
        Assert.IsTrue(plan.ScanIssues.Any(issue => issue.Code is "SourceUnavailable" or "SourceUnsupported"));
        Assert.IsFalse(plan.Items.Any(item => item.Included));
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, File.ReadAllBytes(source));
    }

    [TestMethod]
    public async Task DestinationThroughJunctionCannotWriteIntoTarget()
    {
        using FileFixture fixture = new(ReparseFixtureRoot);
        string source = fixture.WriteFile("source/file.txt", [1]);
        fixture.WriteFile("outside/keep.txt", [2]);
        string target = Path.Combine(fixture.Root, "outside");
        string link = Path.Combine(fixture.Root, "destination-link");
        await CreateDirectoryLink(link, target);
        fixture.DirectoryLinks.Add(link);
        PackagePlan plan = await PackagePlanner.ScanAsync([source]);

        await Assert.ThrowsAsync<IOException>(() =>
            PackageWriter.WriteAsync(plan, Path.Combine(link, "package"), PackageFormat.Folder));

        Assert.AreEqual(1, Directory.GetFileSystemEntries(target).Length);
        CollectionAssert.AreEqual(new byte[] { 2 }, File.ReadAllBytes(Path.Combine(target, "keep.txt")));
    }

    [TestMethod]
    public async Task FolderVerifierRejectsAddedJunctionWithoutTraversingIt()
    {
        using FileFixture fixture = new(ReparseFixtureRoot);
        string source = fixture.WriteFile("source/file.txt", [1]);
        fixture.WriteFile("outside/keep.txt", [2]);
        string package = Path.Combine(fixture.Root, "package");
        PackagePlan plan = await PackagePlanner.ScanAsync([source]);
        await PackageWriter.WriteAsync(plan, package, PackageFormat.Folder);
        string link = Path.Combine(package, "link");
        await CreateDirectoryLink(link, Path.Combine(fixture.Root, "outside"));
        fixture.DirectoryLinks.Add(link);

        VerificationResult result = await PackageVerifier.VerifyAsync(package, PackageFormat.Folder);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Issues.Any(issue => issue.Code is "SourceUnsupported" or "ExtraEntry" or "InvalidArchive"));
        CollectionAssert.AreEqual(new byte[] { 2 }, File.ReadAllBytes(Path.Combine(fixture.Root, "outside/keep.txt")));
    }

    private static async Task CreateDirectoryLink(string link, string target)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(link, target);
            return;
        }

        ProcessStartInfo start = new("cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string argument in new[] { "/d", "/c", "mklink", "/J", link, target })
            start.ArgumentList.Add(argument);
        using Process process = Process.Start(start)!;
        string output = await process.StandardOutput.ReadToEndAsync();
        string error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.AreEqual(0, process.ExitCode, output + error);
        Assert.IsTrue(File.GetAttributes(link).HasFlag(FileAttributes.ReparsePoint));
    }
}
