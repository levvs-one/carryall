using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using Handinpack.Core;

namespace Handinpack.Tests;

[TestClass]
public sealed class HostilePackagesTests
{
    [TestMethod]
    [DataRow(PackageFormat.Folder, "MissingEntry")]
    [DataRow(PackageFormat.Zip, "MissingEntry")]
    [DataRow(PackageFormat.Folder, "ExtraEntry")]
    [DataRow(PackageFormat.Zip, "ExtraEntry")]
    [DataRow(PackageFormat.Folder, "HashMismatch")]
    [DataRow(PackageFormat.Zip, "HashMismatch")]
    [DataRow(PackageFormat.Folder, "SizeMismatch")]
    [DataRow(PackageFormat.Zip, "SizeMismatch")]
    public async Task VerifyReportsPayloadChangesWithoutRepairingThem(PackageFormat format, string change)
    {
        using FileFixture fixture = new();
        string source = fixture.WriteFile("original.txt", [1, 2, 3]);
        PackagePlan plan = await PackagePlanner.ScanAsync([source]);
        string package = Path.Combine(fixture.Root, format == PackageFormat.Zip ? "package.zip" : "package");
        await PackageWriter.WriteAsync(plan, package, format);
        string entry = plan.Items[0].ArchivePath;
        if (change == "MissingEntry") ReplaceEntry(package, format, entry, null);
        else if (change == "ExtraEntry") ReplaceEntry(package, format, "extra.bin", [7]);
        else ReplaceEntry(package, format, entry, change == "HashMismatch" ? [9, 8, 7] : [9]);
        Dictionary<string, FileSnapshot> before = FileFixture.Snapshot(fixture.Root);

        VerificationResult result = await PackageVerifier.VerifyAsync(package, format);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Issues.Any(issue => issue.Code == change), string.Join("; ", result.Issues));
        Assert.AreEqual(before.Count, FileFixture.Snapshot(fixture.Root).Count);
        foreach ((string path, FileSnapshot expected) in before)
            Assert.AreEqual(expected, FileFixture.Snapshot(fixture.Root)[path]);
    }

    [TestMethod]
    [DataRow(PackageFormat.Folder)]
    [DataRow(PackageFormat.Zip)]
    public async Task VerifyDetectsChangedReadableInventory(PackageFormat format)
    {
        using FileFixture fixture = new();
        string source = fixture.WriteFile("original.txt", [1]);
        PackagePlan plan = await PackagePlanner.ScanAsync([source]);
        string package = Path.Combine(fixture.Root, format == PackageFormat.Zip ? "package.zip" : "package");
        await PackageWriter.WriteAsync(plan, package, format);
        byte[] inventory = WriteVerifyTests.ReadEntry(package, format, PackageWriter.ContentsFileName);
        inventory[0] ^= 1;
        ReplaceEntry(package, format, PackageWriter.ContentsFileName, inventory);

        VerificationResult result = await PackageVerifier.VerifyAsync(package, format);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Issues.Any(issue => issue.Code == "HashMismatch"));
    }

    [TestMethod]
    [DataRow(PackageFormat.Folder, "{")]
    [DataRow(PackageFormat.Zip, "{")]
    [DataRow(PackageFormat.Folder, "null")]
    [DataRow(PackageFormat.Zip, "[]")]
    [DataRow(PackageFormat.Folder, "{}")]
    [DataRow(PackageFormat.Zip, "{\"schemaVersion\":1,\"schemaVersion\":1}")]
    public async Task MalformedManifestIsReportedAsInvalid(PackageFormat format, string malformed)
    {
        using FileFixture fixture = new();
        string source = fixture.WriteFile("original.txt", [1]);
        PackagePlan plan = await PackagePlanner.ScanAsync([source]);
        string package = Path.Combine(fixture.Root, format == PackageFormat.Zip ? "package.zip" : "package");
        await PackageWriter.WriteAsync(plan, package, format);
        ReplaceEntry(package, format, PackageWriter.ManifestFileName, Encoding.UTF8.GetBytes(malformed));

        VerificationResult result = await PackageVerifier.VerifyAsync(package, format);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Issues.Any(issue => issue.Code == "InvalidManifest"), string.Join("; ", result.Issues));
    }

    [TestMethod]
    [DataRow("unsupportedVersion")]
    [DataRow("unknownProperty")]
    [DataRow("negativeLength")]
    [DataRow("oversizedPayload")]
    [DataRow("badHash")]
    [DataRow("traversal")]
    [DataRow("duplicatePath")]
    [DataRow("nullFile")]
    public async Task InvalidManifestFieldsNeverReachSuccessfulVerification(string mutation)
    {
        using FileFixture fixture = new();
        string source = fixture.WriteFile("original.txt", [1, 2, 3]);
        PackagePlan plan = await PackagePlanner.ScanAsync([source]);
        string package = Path.Combine(fixture.Root, "package.zip");
        await PackageWriter.WriteAsync(plan, package, PackageFormat.Zip);
        JsonNode manifest = JsonNode.Parse(WriteVerifyTests.ReadEntry(package, PackageFormat.Zip, PackageWriter.ManifestFileName))!;
        JsonArray files = manifest["files"]!.AsArray();
        switch (mutation)
        {
            case "unsupportedVersion": manifest["schemaVersion"] = 999; break;
            case "unknownProperty": manifest["unexpected"] = true; break;
            case "negativeLength": files[0]!["length"] = -1; break;
            case "oversizedPayload": files[0]!["length"] = long.MaxValue; break;
            case "badHash": files[0]!["sha256"] = "not-a-hash"; break;
            case "traversal": files[0]!["archivePath"] = "../outside.bin"; break;
            case "duplicatePath": files.Add(files[0]!.DeepClone()); break;
            case "nullFile": files.Add(null); break;
        }
        ReplaceEntry(package, PackageFormat.Zip, PackageWriter.ManifestFileName, Encoding.UTF8.GetBytes(manifest.ToJsonString()));

        VerificationResult result = await PackageVerifier.VerifyAsync(package, PackageFormat.Zip);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Issues.Any(issue => issue.Code == "InvalidManifest"), string.Join("; ", result.Issues));
        Assert.IsFalse(File.Exists(Path.Combine(fixture.Root, "outside.bin")));
    }

    [TestMethod]
    [DataRow("../outside.bin")]
    [DataRow("/absolute.bin")]
    [DataRow("C:/absolute.bin")]
    [DataRow("a/../outside.bin")]
    [DataRow("report.txt:stream")]
    [DataRow("folder\\file.txt")]
    public async Task HostileZipEntryIsRejectedWithoutExtraction(string entryName)
    {
        using FileFixture fixture = new();
        string source = fixture.WriteFile("original.txt", [1]);
        PackagePlan plan = await PackagePlanner.ScanAsync([source]);
        string package = Path.Combine(fixture.Root, "package.zip");
        await PackageWriter.WriteAsync(plan, package, PackageFormat.Zip);
        ReplaceEntry(package, PackageFormat.Zip, entryName, [9]);
        Dictionary<string, FileSnapshot> before = FileFixture.Snapshot(fixture.Root);

        VerificationResult result = await PackageVerifier.VerifyAsync(package, PackageFormat.Zip);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Issues.Any(issue => issue.Code is "InvalidPath" or "InvalidArchive"));
        Assert.IsTrue(result.Issues.All(issue => !string.IsNullOrWhiteSpace(issue.Message)));
        Assert.AreEqual(before.Count, FileFixture.Snapshot(fixture.Root).Count);
        Assert.IsFalse(File.Exists(Path.Combine(fixture.Root, "outside.bin")));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task DuplicateZipEntriesAreRejected(bool duplicateManifest)
    {
        using FileFixture fixture = new();
        string source = fixture.WriteFile("original.txt", [1]);
        PackagePlan plan = await PackagePlanner.ScanAsync([source]);
        string package = Path.Combine(fixture.Root, "package.zip");
        await PackageWriter.WriteAsync(plan, package, PackageFormat.Zip);
        string name = duplicateManifest ? PackageWriter.ManifestFileName : plan.Items[0].ArchivePath;
        byte[] original = WriteVerifyTests.ReadEntry(package, PackageFormat.Zip, name);
        using (ZipArchive archive = ZipFile.Open(package, ZipArchiveMode.Update))
        using (Stream stream = archive.CreateEntry(name).Open()) stream.Write(original);

        VerificationResult result = await PackageVerifier.VerifyAsync(package, PackageFormat.Zip);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Issues.Any(issue => issue.Code is "DuplicateEntry" or "InvalidManifest"));
    }

    [TestMethod]
    [DataRow("original.txt", "ORIGINAL.TXT")]
    [DataRow("café.txt", "cafe\u0301.txt")]
    public async Task ZipCaseAndUnicodeAliasesCannotBecomeDistinctVerifiedFiles(string originalName, string alias)
    {
        using FileFixture fixture = new();
        string source = fixture.WriteFile(originalName, [1]);
        PackagePlan plan = await PackagePlanner.ScanAsync([source]);
        string package = Path.Combine(fixture.Root, "package.zip");
        await PackageWriter.WriteAsync(plan, package, PackageFormat.Zip);
        ReplaceEntry(package, PackageFormat.Zip, alias, [1]);

        VerificationResult result = await PackageVerifier.VerifyAsync(package, PackageFormat.Zip);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Issues.Any(issue => issue.Code == "DuplicateEntry"));
    }

    [TestMethod]
    public async Task OtherwiseValidManifestRejectsDuplicateJsonProperty()
    {
        using FileFixture fixture = new();
        string source = fixture.WriteFile("original.txt", [1]);
        PackagePlan plan = await PackagePlanner.ScanAsync([source]);
        string package = Path.Combine(fixture.Root, "package.zip");
        await PackageWriter.WriteAsync(plan, package, PackageFormat.Zip);
        JsonNode manifest = JsonNode.Parse(WriteVerifyTests.ReadEntry(package, PackageFormat.Zip, PackageWriter.ManifestFileName))!;
        string original = manifest.ToJsonString();
        string duplicated = original.Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":1", StringComparison.Ordinal);
        Assert.AreNotEqual(original, duplicated);
        ReplaceEntry(package, PackageFormat.Zip, PackageWriter.ManifestFileName, Encoding.UTF8.GetBytes(duplicated));

        VerificationResult result = await PackageVerifier.VerifyAsync(package, PackageFormat.Zip);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Issues.Any(issue => issue.Code == "InvalidManifest"));
    }

    [TestMethod]
    [DataRow(PackageFormat.Folder)]
    [DataRow(PackageFormat.Zip)]
    public async Task MissingManifestCannotBeTreatedAsUnverifiedSuccess(PackageFormat format)
    {
        using FileFixture fixture = new();
        string source = fixture.WriteFile("original.txt", [1]);
        PackagePlan plan = await PackagePlanner.ScanAsync([source]);
        string package = Path.Combine(fixture.Root, format == PackageFormat.Zip ? "package.zip" : "package");
        await PackageWriter.WriteAsync(plan, package, format);
        ReplaceEntry(package, format, PackageWriter.ManifestFileName, null);

        VerificationResult result = await PackageVerifier.VerifyAsync(package, format);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Issues.Any(issue => issue.Code is "InvalidManifest" or "MissingEntry"));
    }

    [TestMethod]
    public async Task ExtraEmptyUnicodeAliasDirectoryInvalidatesFolderComposition()
    {
        using FileFixture fixture = new();
        string source = fixture.WriteFile("original.txt", [1]);
        PackagePlan plan = await PackagePlanner.ScanAsync([source]);
        plan = PackagePlanner.Validate(plan with { Items = [plan.Items[0] with { ArchivePath = "café/file.txt" }] });
        string package = Path.Combine(fixture.Root, "package");
        await PackageWriter.WriteAsync(plan, package, PackageFormat.Folder);
        Directory.CreateDirectory(Path.Combine(package, "cafe\u0301"));
        Assert.AreEqual(2, Directory.GetDirectories(package).Length);

        VerificationResult result = await PackageVerifier.VerifyAsync(package, PackageFormat.Folder);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Issues.Any(issue => issue.Code is "DuplicateEntry" or "ExtraEntry"));
    }

    [TestMethod]
    [DataRow("../excluded.bin")]
    [DataRow("/absolute.bin")]
    [DataRow("C:/private/excluded.bin")]
    public async Task ManifestExclusionsRequireSafeRelativeArchivePaths(string archivePath)
    {
        using FileFixture fixture = new();
        string source = fixture.WriteFile("original.txt", [1]);
        string excluded = fixture.WriteFile("excluded.txt", [2]);
        PackagePlan plan = await PackagePlanner.ScanAsync([source, excluded]);
        plan = PackagePlanner.Validate(plan with
        {
            Items = [.. plan.Items.Select(item => item.SourcePath == excluded
                ? item with { Included = false, ExclusionReason = "Outside this handover" } : item)]
        });
        string package = Path.Combine(fixture.Root, "package.zip");
        await PackageWriter.WriteAsync(plan, package, PackageFormat.Zip);
        JsonNode manifest = JsonNode.Parse(WriteVerifyTests.ReadEntry(package, PackageFormat.Zip, PackageWriter.ManifestFileName))!;
        manifest["exclusions"]![0]!["archivePath"] = archivePath;
        ReplaceEntry(package, PackageFormat.Zip, PackageWriter.ManifestFileName, Encoding.UTF8.GetBytes(manifest.ToJsonString()));

        VerificationResult result = await PackageVerifier.VerifyAsync(package, PackageFormat.Zip);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Issues.Any(issue => issue.Code == "InvalidManifest"));
    }

    [TestMethod]
    [DataRow(PackageFormat.Folder)]
    [DataRow(PackageFormat.Zip)]
    public async Task OversizedManifestFailsBeforeParsingItsContents(PackageFormat format)
    {
        using FileFixture fixture = new();
        string source = fixture.WriteFile("original.txt", [1]);
        PackagePlan plan = await PackagePlanner.ScanAsync([source]);
        string package = Path.Combine(fixture.Root, format == PackageFormat.Zip ? "package.zip" : "package");
        await PackageWriter.WriteAsync(plan, package, format);
        byte[] oversized = new byte[16 * 1024 * 1024 + 1];
        Array.Fill(oversized, (byte)' ');
        ReplaceEntry(package, format, PackageWriter.ManifestFileName, oversized);

        VerificationResult result = await PackageVerifier.VerifyAsync(package, format);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Issues.Any(issue => issue.Code == "InvalidManifest"));
    }

    [TestMethod]
    public async Task TruncatedZipIsReportedAsInvalidArchive()
    {
        using FileFixture fixture = new();
        string source = fixture.WriteFile("original.txt", [1]);
        PackagePlan plan = await PackagePlanner.ScanAsync([source]);
        string package = Path.Combine(fixture.Root, "package.zip");
        await PackageWriter.WriteAsync(plan, package, PackageFormat.Zip);
        using (FileStream stream = new(package, FileMode.Open, FileAccess.Write)) stream.SetLength(12);

        VerificationResult result = await PackageVerifier.VerifyAsync(package, PackageFormat.Zip);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Issues.Any(issue => issue.Code == "InvalidArchive"));
    }

    private static void ReplaceEntry(string packagePath, PackageFormat format, string archivePath, byte[]? contents)
    {
        if (format == PackageFormat.Folder)
        {
            string path = Path.Combine(packagePath, archivePath.Replace('/', Path.DirectorySeparatorChar));
            if (contents is null) File.Delete(path);
            else File.WriteAllBytes(path, contents);
            return;
        }

        using ZipArchive archive = ZipFile.Open(packagePath, ZipArchiveMode.Update);
        archive.GetEntry(archivePath)?.Delete();
        if (contents is not null)
        {
            using Stream stream = archive.CreateEntry(archivePath).Open();
            stream.Write(contents);
        }
    }
}
