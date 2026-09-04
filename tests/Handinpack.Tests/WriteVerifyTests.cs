using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Handinpack.Core;

namespace Handinpack.Tests;

[TestClass]
public sealed class WriteVerifyTests
{
    [TestMethod]
    [DataRow(PackageFormat.Folder)]
    [DataRow(PackageFormat.Zip)]
    public async Task RoundtripMatchesIndependentHashesAndLeavesSourcesUnchanged(PackageFormat format)
    {
        using FileFixture fixture = new();
        fixture.WriteFile("first/чертежи/план.pdf", [0, 1, 2, 255]);
        fixture.WriteFile("first/empty.txt", []);
        fixture.WriteFile("second/photo.jpg", [255, 216, 42, 0, 128]);
        fixture.WriteFile("second/excluded.docx", [9, 8, 7]);
        string firstRoot = Path.Combine(fixture.Root, "first");
        string secondRoot = Path.Combine(fixture.Root, "second");
        Dictionary<string, FileSnapshot> firstBefore = FileFixture.Snapshot(firstRoot);
        Dictionary<string, FileSnapshot> secondBefore = FileFixture.Snapshot(secondRoot);
        PackagePlan plan = await PackagePlanner.ScanAsync([firstRoot, secondRoot]);
        plan = PackagePlanner.Validate(plan with
        {
            Items = [.. plan.Items.Select(item => item.SourcePath.EndsWith("excluded.docx", StringComparison.Ordinal)
                ? item with { Included = false, ExclusionReason = "Not part of this handover" }
                : item)]
        });
        string output = Path.Combine(fixture.Root, format == PackageFormat.Zip ? "handover.zip" : "handover");

        PackageResult result = await PackageWriter.WriteAsync(plan, output, format);
        VerificationResult verification = await PackageVerifier.VerifyAsync(output, format);

        Assert.IsTrue(verification.IsValid);
        Assert.AreEqual(output, result.OutputPath);
        Assert.AreEqual(3, result.FileCount);
        Assert.AreEqual(9L, result.PayloadBytes);
        Assert.IsTrue(result.OutputBytes > result.PayloadBytes);
        using JsonDocument manifest = JsonDocument.Parse(ReadEntry(output, format, PackageWriter.ManifestFileName));
        Assert.AreEqual(1, manifest.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.AreEqual(3, manifest.RootElement.GetProperty("files").GetArrayLength());
        Assert.AreEqual(1, manifest.RootElement.GetProperty("exclusions").GetArrayLength());
        foreach (JsonElement entry in manifest.RootElement.GetProperty("files").EnumerateArray())
        {
            string archivePath = entry.GetProperty("archivePath").GetString()!;
            PackageItem item = plan.Items.Single(candidate => candidate.Included && candidate.ArchivePath == archivePath);
            byte[] actual = ReadEntry(output, format, archivePath);
            CollectionAssert.AreEqual(File.ReadAllBytes(item.SourcePath), actual);
            Assert.AreEqual(actual.LongLength, entry.GetProperty("length").GetInt64());
            Assert.AreEqual(Convert.ToHexString(SHA256.HashData(actual)).ToLowerInvariant(), entry.GetProperty("sha256").GetString());
            Assert.IsFalse(Path.IsPathRooted(entry.GetProperty("sourceRelativePath").GetString()!));
        }

        AssertSnapshotsEqual(firstBefore, FileFixture.Snapshot(firstRoot));
        AssertSnapshotsEqual(secondBefore, FileFixture.Snapshot(secondRoot));
    }

    [TestMethod]
    [DataRow(PackageFormat.Folder)]
    [DataRow(PackageFormat.Zip)]
    public async Task ExistingOutputIsNeverOverwritten(PackageFormat format)
    {
        using FileFixture fixture = new();
        string source = fixture.WriteFile("source/file.txt", [1, 2, 3]);
        string output = Path.Combine(fixture.Root, format == PackageFormat.Zip ? "existing.zip" : "existing");
        string sentinel = format == PackageFormat.Zip
            ? fixture.WriteFile("existing.zip", [8, 9, 10])
            : fixture.WriteFile("existing/keep.bin", [8, 9, 10]);
        PackagePlan plan = await PackagePlanner.ScanAsync([source]);

        await Assert.ThrowsAsync<IOException>(() => PackageWriter.WriteAsync(plan, output, format));

        CollectionAssert.AreEqual(new byte[] { 8, 9, 10 }, File.ReadAllBytes(sentinel));
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, File.ReadAllBytes(source));
    }

    [TestMethod]
    [DataRow(PackageFormat.Folder)]
    [DataRow(PackageFormat.Zip)]
    public async Task OutputInsideSourceIsRejectedBeforeWriting(PackageFormat format)
    {
        using FileFixture fixture = new();
        fixture.WriteFile("source/file.txt", [1]);
        string sourceRoot = Path.Combine(fixture.Root, "source");
        PackagePlan plan = await PackagePlanner.ScanAsync([sourceRoot]);
        string output = Path.Combine(sourceRoot, format == PackageFormat.Zip ? "nested.zip" : "nested");

        await Assert.ThrowsAsync<IOException>(() => PackageWriter.WriteAsync(plan, output, format));

        Assert.IsFalse(File.Exists(output));
        Assert.IsFalse(Directory.Exists(output));
        Assert.AreEqual(1, Directory.GetFileSystemEntries(sourceRoot).Length);
    }

    [TestMethod]
    public async Task SimilarSourcePrefixDoesNotBlockSeparateSiblingOutput()
    {
        using FileFixture fixture = new();
        fixture.WriteFile("source/file.txt", [1]);
        PackagePlan plan = await PackagePlanner.ScanAsync([Path.Combine(fixture.Root, "source")]);
        string output = Path.Combine(fixture.Root, "source-complete");

        PackageResult result = await PackageWriter.WriteAsync(plan, output, PackageFormat.Folder);

        Assert.AreEqual(output, result.OutputPath);
        Assert.IsTrue((await PackageVerifier.VerifyAsync(output, PackageFormat.Folder)).IsValid);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task SourceChangedAfterScanCannotPublish(bool sameLength)
    {
        using FileFixture fixture = new();
        string source = fixture.WriteFile("source/file.txt", [1, 2, 3]);
        PackagePlan plan = await PackagePlanner.ScanAsync([source]);
        File.WriteAllBytes(source, sameLength ? [9, 8, 7] : [9, 8, 7, 6]);
        File.SetLastWriteTimeUtc(source, plan.Items[0].LastWriteTimeUtc.AddMinutes(1));
        string output = Path.Combine(fixture.Root, "handover");

        await Assert.ThrowsAsync<IOException>(() => PackageWriter.WriteAsync(plan, output, PackageFormat.Folder));

        Assert.IsFalse(Directory.Exists(output));
    }

    [TestMethod]
    public async Task SourceLockedAfterScanCannotPublish()
    {
        using FileFixture fixture = new();
        string source = fixture.WriteFile("source/file.txt", [1, 2, 3]);
        PackagePlan plan = await PackagePlanner.ScanAsync([source]);
        string output = Path.Combine(fixture.Root, "handover.zip");
        using FileStream locked = new(source, FileMode.Open, FileAccess.Read, FileShare.None);

        await Assert.ThrowsAsync<IOException>(() => PackageWriter.WriteAsync(plan, output, PackageFormat.Zip));

        Assert.IsFalse(File.Exists(output));
    }

    [TestMethod]
    [DataRow(PackageFormat.Folder, PackagePhase.Copying)]
    [DataRow(PackageFormat.Zip, PackagePhase.Copying)]
    [DataRow(PackageFormat.Folder, PackagePhase.Verifying)]
    [DataRow(PackageFormat.Zip, PackagePhase.Verifying)]
    public async Task CancellationDuringRealIoKeepsOnlyUnfinishedOutput(PackageFormat format, PackagePhase phase)
    {
        using FileFixture fixture = new();
        byte[] contents = new byte[1_048_576];
        new Random(19).NextBytes(contents);
        string source = fixture.WriteFile("source/large.bin", contents);
        PackagePlan plan = await PackagePlanner.ScanAsync([source]);
        string output = Path.Combine(fixture.Root, format == PackageFormat.Zip ? "handover.zip" : "handover");
        using CancellationTokenSource cancellation = new();
        bool observedInsideFile = false;
        InlineProgress progress = new(update =>
        {
            if (update.Phase == phase && update.BytesCompleted > 0 && update.BytesCompleted < contents.LongLength)
            {
                observedInsideFile = true;
                cancellation.Cancel();
            }
        });

        PackageCanceledException error = await Assert.ThrowsExactlyAsync<PackageCanceledException>(() =>
            PackageWriter.WriteAsync(plan, output, format, progress, cancellation.Token));

        Assert.IsTrue(observedInsideFile);
        Assert.IsFalse(File.Exists(output));
        Assert.IsFalse(Directory.Exists(output));
        Assert.IsFalse(string.IsNullOrWhiteSpace(error.PartialPath));
        Assert.IsTrue(File.Exists(error.PartialPath) || Directory.Exists(error.PartialPath));
        CollectionAssert.AreEqual(contents, File.ReadAllBytes(source));
        string retryOutput = Path.Combine(fixture.Root, format == PackageFormat.Zip ? "retry.zip" : "retry");
        await PackageWriter.WriteAsync(plan, retryOutput, format);
        Assert.IsTrue((await PackageVerifier.VerifyAsync(retryOutput, format)).IsValid);
    }

    [TestMethod]
    [DataRow(PackageFormat.Folder)]
    [DataRow(PackageFormat.Zip)]
    public async Task OutputLimitIncludesManifestAndReadableInventory(PackageFormat format)
    {
        using FileFixture fixture = new();
        string source = fixture.WriteFile("source/empty.txt", []);
        PackagePlan plan = await PackagePlanner.ScanAsync([source], new PackageRules(MaxOutputBytes: 1));
        string output = Path.Combine(fixture.Root, format == PackageFormat.Zip ? "tiny.zip" : "tiny");

        await Assert.ThrowsAsync<IOException>(() => PackageWriter.WriteAsync(plan, output, format));

        Assert.IsFalse(File.Exists(output));
        Assert.IsFalse(Directory.Exists(output));
        Assert.AreEqual(0L, new FileInfo(source).Length);
    }

    [TestMethod]
    public async Task TenThousandRealFilesRoundtripWithoutLosingEntries()
    {
        using FileFixture fixture = new();
        const int count = 10_000;
        for (int index = 0; index < count; index++)
            fixture.WriteFile($"source/{index / 100:D3}/{index:D5}.bin", BitConverter.GetBytes(index));
        string source = Path.Combine(fixture.Root, "source");
        Dictionary<string, FileSnapshot> before = FileFixture.Snapshot(source);
        PackagePlan plan = await PackagePlanner.ScanAsync([source]);
        Assert.IsTrue(plan.CanBuild);
        Assert.AreEqual(count, plan.Items.Length);
        string package = Path.Combine(fixture.Root, "ten-thousand.zip");

        PackageResult result = await PackageWriter.WriteAsync(plan, package, PackageFormat.Zip);
        VerificationResult verification = await PackageVerifier.VerifyAsync(package, PackageFormat.Zip);

        Assert.AreEqual(count, result.FileCount);
        Assert.AreEqual(4L * count, result.PayloadBytes);
        Assert.IsTrue(verification.IsValid);
        Assert.AreEqual(count, verification.FileCount);
        using ZipArchive archive = ZipFile.OpenRead(package);
        Assert.AreEqual(count + 2, archive.Entries.Count);
        foreach (PackageItem item in plan.Items)
        {
            using Stream stream = archive.GetEntry(item.ArchivePath)!.Open();
            byte[] actual = new byte[sizeof(int)];
            stream.ReadExactly(actual);
            int expected = int.Parse(Path.GetFileNameWithoutExtension(item.SourcePath), System.Globalization.CultureInfo.InvariantCulture);
            Assert.AreEqual(expected, BitConverter.ToInt32(actual));
            Assert.AreEqual(-1, stream.ReadByte());
        }
        AssertSnapshotsEqual(before, FileFixture.Snapshot(source));
    }

    internal static byte[] ReadEntry(string packagePath, PackageFormat format, string archivePath)
    {
        if (format == PackageFormat.Folder)
        {
            return File.ReadAllBytes(Path.Combine(packagePath, archivePath.Replace('/', Path.DirectorySeparatorChar)));
        }

        using ZipArchive archive = ZipFile.OpenRead(packagePath);
        using Stream stream = archive.GetEntry(archivePath)!.Open();
        using MemoryStream contents = new();
        stream.CopyTo(contents);
        return contents.ToArray();
    }

    private static void AssertSnapshotsEqual(Dictionary<string, FileSnapshot> expected, Dictionary<string, FileSnapshot> actual)
    {
        Assert.AreEqual(expected.Count, actual.Count);
        foreach ((string path, FileSnapshot snapshot) in expected)
        {
            Assert.IsTrue(actual.TryGetValue(path, out FileSnapshot? actualSnapshot), path);
            Assert.AreEqual(snapshot, actualSnapshot, path);
        }
    }
}

internal sealed class InlineProgress(Action<PackageProgress> report) : IProgress<PackageProgress>
{
    public void Report(PackageProgress value) => report(value);
}
