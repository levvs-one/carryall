using Handinpack.Core;

namespace Handinpack.Tests;

[TestClass]
public sealed class PlannerTests
{
    [TestMethod]
    public async Task ScanPreservesNestedNamesAndIncludesEmptyFiles()
    {
        using FileFixture fixture = new();
        fixture.WriteFile("source/чертежи/этаж.pdf", [0, 1, 2, 255]);
        fixture.WriteFile("source/пусто.txt", []);

        PackagePlan plan = await PackagePlanner.ScanAsync([Path.Combine(fixture.Root, "source")]);

        Assert.IsTrue(plan.CanBuild);
        Assert.AreEqual(2, plan.Items.Length);
        Assert.AreEqual(4L, plan.TotalBytes);
        Assert.IsTrue(plan.Items.Any(item => item.SourceRelativePath.Replace('\\', '/').EndsWith("чертежи/этаж.pdf", StringComparison.Ordinal)));
        Assert.IsTrue(plan.Items.Any(item => item.Length == 0));
        Assert.IsTrue(plan.Items.All(item => !Path.IsPathRooted(item.ArchivePath)));
    }

    [TestMethod]
    public async Task ScanRepeatedSourceDoesNotSilentlyCreateDuplicateRows()
    {
        using FileFixture fixture = new();
        string source = fixture.WriteFile("source/file.txt", [1, 2, 3]);

        PackagePlan plan = await PackagePlanner.ScanAsync([source, source]);

        Assert.AreEqual(1, plan.Items.Count(item => item.Included));
    }

    [TestMethod]
    public async Task EmptyFolderCannotBecomeCompletedPackage()
    {
        using FileFixture fixture = new();
        string source = Path.Combine(fixture.Root, "empty");
        Directory.CreateDirectory(source);

        PackagePlan plan = await PackagePlanner.ScanAsync([source]);

        Assert.IsFalse(plan.CanBuild);
        Assert.IsTrue(plan.Issues.Any(issue => issue.Code == "EmptyPackage"));
    }

    [TestMethod]
    public async Task MissingSourceProducesExplicitIssue()
    {
        using FileFixture fixture = new();
        string missing = Path.Combine(fixture.Root, "missing");

        PackagePlan plan = await PackagePlanner.ScanAsync([missing]);

        Assert.IsFalse(plan.CanBuild);
        Assert.IsTrue(plan.ScanIssues.Any(issue => issue.Code == "SourceUnavailable"));
    }

    [TestMethod]
    public async Task ScanCancellationDoesNotReturnPartialSuccessfulPlan()
    {
        using FileFixture fixture = new();
        string source = fixture.WriteFile("source/file.txt", [1]);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            PackagePlanner.ScanAsync([source], cancellationToken: cancellation.Token));
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("../outside.txt")]
    [DataRow("/absolute.txt")]
    [DataRow("C:/absolute.txt")]
    [DataRow("//server/share/file.txt")]
    [DataRow("a//b.txt")]
    [DataRow("a/./b.txt")]
    [DataRow("a/../b.txt")]
    [DataRow("report.txt:secret")]
    [DataRow("CON")]
    [DataRow("con.txt")]
    [DataRow("NUL.pdf")]
    [DataRow("AUX")]
    [DataRow("COM1.txt")]
    [DataRow("LPT9")]
    [DataRow("COM¹.txt")]
    [DataRow("LPT²")]
    [DataRow("CON .txt")]
    [DataRow("COM1 .txt")]
    [DataRow("LPT1 .txt")]
    [DataRow("folder./file.txt")]
    [DataRow("folder /file.txt")]
    [DataRow("file.txt.")]
    [DataRow("file.txt ")]
    [DataRow("a?b.txt")]
    [DataRow("a\u0001b.txt")]
    [DataRow("handinpack.json")]
    [DataRow("CONTENTS.TXT")]
    public async Task UnsafePackagePathsAreRejectedWithoutRenaming(string archivePath)
    {
        using FileFixture fixture = new();
        string source = fixture.WriteFile("file.bin", [1]);
        PackagePlan plan = await PackagePlanner.ScanAsync([source]);
        plan = plan with { Items = [plan.Items[0] with { ArchivePath = archivePath }] };

        PackagePlan validated = PackagePlanner.Validate(plan);

        Assert.IsFalse(validated.CanBuild);
        Assert.IsTrue(validated.Issues.Any(issue => issue.Code is "InvalidPath" or "Collision"));
        Assert.AreEqual(archivePath, validated.Items[0].ArchivePath);
        CollectionAssert.AreEqual(new byte[] { 1 }, File.ReadAllBytes(source));
    }

    [TestMethod]
    [DataRow("report.pdf", "REPORT.PDF")]
    [DataRow("café.txt", "cafe\u0301.txt")]
    [DataRow("part", "part/file.txt")]
    [DataRow("A/file.txt", "a")]
    [DataRow("café/a.txt", "cafe\u0301/b.txt")]
    [DataRow("Folder/a.txt", "folder/b.txt")]
    public async Task CaseUnicodeAndFileDirectoryCollisionsBlockPlan(string firstPath, string secondPath)
    {
        using FileFixture fixture = new();
        string first = fixture.WriteFile("first.bin", [1]);
        string second = fixture.WriteFile("second.bin", [2]);
        PackagePlan plan = await PackagePlanner.ScanAsync([first, second]);
        plan = plan with
        {
            Items = [plan.Items[0] with { ArchivePath = firstPath }, plan.Items[1] with { ArchivePath = secondPath }]
        };

        PackagePlan validated = PackagePlanner.Validate(plan);

        Assert.IsFalse(validated.CanBuild);
        Assert.IsTrue(validated.Issues.Any(issue => issue.Code == "Collision"));
        Assert.AreEqual(firstPath, validated.Items[0].ArchivePath);
        Assert.AreEqual(secondPath, validated.Items[1].ArchivePath);
    }

    [TestMethod]
    public async Task ExtensionFileAndPayloadLimitsAreEnforced()
    {
        using FileFixture fixture = new();
        string first = fixture.WriteFile("first.pdf", [1, 2, 3]);
        string second = fixture.WriteFile("second.txt", [4, 5, 6]);
        PackageRules rules = new(AllowedExtensions: [".pdf"], MaxFileBytes: 2, MaxTotalBytes: 5);

        PackagePlan plan = await PackagePlanner.ScanAsync([first, second], rules);

        Assert.IsFalse(plan.CanBuild);
        Assert.IsTrue(plan.Issues.Any(issue => issue.Code == "ExtensionNotAllowed"));
        Assert.IsTrue(plan.Issues.Any(issue => issue.Code == "FileTooLarge"));
        Assert.IsTrue(plan.Issues.Any(issue => issue.Code == "TotalTooLarge"));
    }

    [TestMethod]
    public async Task ExcludingRequiredFileKeepsRequirementBlocking()
    {
        using FileFixture fixture = new();
        string source = fixture.WriteFile("required.pdf", [1]);
        PackagePlan plan = await PackagePlanner.ScanAsync([source]);
        string archivePath = plan.Items[0].ArchivePath;
        plan = plan with
        {
            Rules = new PackageRules(RequiredPaths: [archivePath]),
            Items = [plan.Items[0] with { Included = false, ExclusionReason = "Not approved for handover" }]
        };

        PackagePlan validated = PackagePlanner.Validate(plan);

        Assert.IsFalse(validated.CanBuild);
        Assert.IsTrue(validated.Issues.Any(issue => issue.Code == "MissingRequired"));
        Assert.AreEqual("Not approved for handover", validated.Items[0].ExclusionReason);
    }

    [TestMethod]
    public async Task NegativeLimitIsInvalidRule()
    {
        using FileFixture fixture = new();
        string source = fixture.WriteFile("file.txt", [1]);

        PackagePlan plan = await PackagePlanner.ScanAsync([source], new PackageRules(MaxFileBytes: -1));

        Assert.IsFalse(plan.CanBuild);
        Assert.IsTrue(plan.Issues.Any(issue => issue.Code == "InvalidRule"));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task NullRuleArrayElementProducesInvalidRuleInsteadOfCrashing(bool requiredPath)
    {
        using FileFixture fixture = new();
        string source = fixture.WriteFile("file.txt", [1]);
        PackageRules rules = requiredPath
            ? new PackageRules(RequiredPaths: [null!])
            : new PackageRules(AllowedExtensions: [null!]);

        PackagePlan plan = await PackagePlanner.ScanAsync([source], rules);

        Assert.IsFalse(plan.CanBuild);
        Assert.IsTrue(plan.Issues.Any(issue => issue.Code == "InvalidRule"));
    }
}
