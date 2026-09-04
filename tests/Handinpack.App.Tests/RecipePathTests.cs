using System.Diagnostics;
using System.IO;
using System.Reflection;
using Handinpack.App;
using Handinpack.Tests;

namespace Handinpack.App.Tests;

[TestClass]
public sealed class RecipePathTests
{
    private static readonly MethodInfo IsLocalRecipePath = typeof(MainWindow)
        .GetMethod("IsLocalRecipePath", BindingFlags.NonPublic | BindingFlags.Static)!;

    [TestMethod]
    [DataRow("")]
    [DataRow("relative/file.txt")]
    [DataRow(@"C:file.txt")]
    [DataRow(@"\root-relative.txt")]
    [DataRow(@"\\handinpack-test.invalid\share\file.txt")]
    [DataRow("//handinpack-test.invalid/share/file.txt")]
    [DataRow(@"\\?\UNC\handinpack-test.invalid\share\file.txt")]
    [DataRow(@"\\?\C:\file.txt")]
    [DataRow(@"\\.\pipe\handinpack-test")]
    [DataRow("https://handinpack-test.invalid/file.txt")]
    [DataRow("file://handinpack-test.invalid/share/file.txt")]
    public void NonLocalRecipeGrammarIsRejectedBeforeFileAccess(string path)
    {
        Assert.IsFalse((bool)IsLocalRecipePath.Invoke(null, [path])!);
    }

    [TestMethod]
    public void ExistingLocalFixtureIsAllowed()
    {
        using FileFixture fixture = new();
        string source = fixture.WriteFile("source/file.txt", [1]);

        Assert.IsTrue((bool)IsLocalRecipePath.Invoke(null, [source])!);
        CollectionAssert.AreEqual(new byte[] { 1 }, File.ReadAllBytes(source));
    }

    [TestMethod]
    public async Task JunctionAncestorCannotBecomeAnAutomaticRecipeRead()
    {
        string fixtureBase = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Temp", "handinpack-reparse-tests");
        using FileFixture fixture = new(fixtureBase);
        string source = fixture.WriteFile("outside/file.txt", [1, 2, 3]);
        string target = Path.GetDirectoryName(source)!;
        string link = Path.Combine(fixture.Root, "recipe-link");
        ProcessStartInfo start = new("cmd.exe")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (string argument in new[] { "/d", "/c", "mklink", "/J", link, target })
            start.ArgumentList.Add(argument);
        using Process process = Process.Start(start)!;
        string output = await process.StandardOutput.ReadToEndAsync();
        string error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.AreEqual(0, process.ExitCode, output + error);
        fixture.DirectoryLinks.Add(link);

        Assert.IsFalse((bool)IsLocalRecipePath.Invoke(null, [Path.Combine(link, "file.txt")])!);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, File.ReadAllBytes(source));
    }
}
