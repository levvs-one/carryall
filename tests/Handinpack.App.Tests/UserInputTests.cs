using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using Handinpack.App;
using Handinpack.Core;
using Handinpack.Tests;

namespace Handinpack.App.Tests;

[TestClass]
public sealed class UserInputTests
{
    private const BindingFlags PrivateStatic = BindingFlags.NonPublic | BindingFlags.Static;
    private const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;
    private const string ValidRecipe = """
        {"version":1,"packageName":"handover","destinationDirectory":"C:\\handover",
         "format":0,"rules":{},"sourcePaths":[],"edits":[]}
        """;

    [TestMethod]
    [DataRow("", null)]
    [DataRow("   ", null)]
    [DataRow("1", 1048576L)]
    [DataRow("1.5", 1572864L)]
    [DataRow("1,5", 1572864L)]
    [DataRow(" 2 ", 2097152L)]
    public void MebibytesUseActualDecimalParser(string input, long? expected)
    {
        object?[] arguments = [input, null];

        bool accepted = (bool)typeof(MainWindow).GetMethod("TryReadMebibytes", PrivateStatic)!.Invoke(null, arguments)!;

        Assert.IsTrue(accepted);
        Assert.AreEqual(expected, (long?)arguments[1]);
    }

    [TestMethod]
    [DataRow("-1")]
    [DataRow("0")]
    [DataRow("0.00000001")]
    [DataRow("NaN")]
    [DataRow("1e3")]
    [DataRow("1,2.3")]
    [DataRow("8796093022208")]
    [DataRow("9223372036854775807")]
    public void InvalidMebibytesCannotBecomeAnUnboundedRule(string input)
    {
        object?[] arguments = [input, null];

        bool accepted = (bool)typeof(MainWindow).GetMethod("TryReadMebibytes", PrivateStatic)!.Invoke(null, arguments)!;

        Assert.IsFalse(accepted);
    }

    [TestMethod]
    [DataRow("unknown")]
    [DataRow("duplicate")]
    [DataRow("missingRules")]
    [DataRow("nullRules")]
    [DataRow("nullSourcePaths")]
    [DataRow("nullEdits")]
    [DataRow("nullPackageName")]
    [DataRow("malformed")]
    public void ActualRecipeDeserializerRejectsMalformedRequiredFields(string mutation)
    {
        JsonObject recipe = JsonNode.Parse(ValidRecipe)!.AsObject();
        switch (mutation)
        {
            case "unknown": recipe["unexpected"] = true; break;
            case "missingRules": recipe.Remove("rules"); break;
            case "nullRules": recipe["rules"] = null; break;
            case "nullSourcePaths": recipe["sourcePaths"] = null; break;
            case "nullEdits": recipe["edits"] = null; break;
            case "nullPackageName": recipe["packageName"] = null; break;
        }
        string json = mutation == "malformed" ? "{" : recipe.ToJsonString();
        if (mutation == "duplicate")
            json = json.Replace("\"version\":1", "\"version\":1,\"version\":1", StringComparison.Ordinal);
        Type recipeType = typeof(MainWindow).GetNestedType("Recipe", BindingFlags.NonPublic)!;
        var options = (JsonSerializerOptions)typeof(MainWindow).GetField("RecipeJson", PrivateStatic)!.GetValue(null)!;

        Assert.ThrowsExactly<JsonException>(() => JsonSerializer.Deserialize(json, recipeType, options));
    }

    [TestMethod]
    [DataRow("requiredPaths")]
    [DataRow("allowedExtensions")]
    public void NullRecipeRuleElementCannotBecomeABuildablePlan(string ruleName)
    {
        JsonObject input = JsonNode.Parse(ValidRecipe)!.AsObject();
        input["rules"]![ruleName] = new JsonArray((JsonNode?)null);
        Type recipeType = typeof(MainWindow).GetNestedType("Recipe", BindingFlags.NonPublic)!;
        var options = (JsonSerializerOptions)typeof(MainWindow).GetField("RecipeJson", PrivateStatic)!.GetValue(null)!;
        object recipe = JsonSerializer.Deserialize(input.ToJsonString(), recipeType, options)!;
        var rules = (PackageRules)recipeType.GetProperty("Rules")!.GetValue(recipe)!;

        PackagePlan plan = PackagePlanner.Validate(new([], [], rules, [], [], 0));

        Assert.IsFalse(plan.CanBuild);
        Assert.IsTrue(plan.Issues.Any(issue => issue.Code == "InvalidRule"));
    }

    [STATestMethod(UseSTASynchronizationContext = true)]
    public async Task ActualWindowPreservesSpacedNamesAndRecoversFromFailedOperations()
    {
        using FileFixture fixture = new();
        string source = fixture.WriteFile("source/Final plan.pdf", [1, 2, 3]);
        PackagePlan scanned = await PackagePlanner.ScanAsync([source]);
        string verifiedOutput = Path.Combine(fixture.Root, "verified.zip");
        await PackageWriter.WriteAsync(scanned, verifiedOutput, PackageFormat.Zip);
        var application = new Handinpack.App.App();
        application.InitializeComponent();
        application.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var window = new MainWindow();
        try
        {
            Assert.IsFalse(window.IsVisible);
            foreach (string name in new[] { "MaxFileBox", "MaxPayloadBox", "MaxPackageBox", "ExtensionsBox" })
                ((TextBox)window.FindName(name)).Text = string.Empty;
            ((TextBox)window.FindName("RequiredPathsBox")).Text = " drawings/Final plan.pdf \r\n\r\nimages/Photo 01.jpg\n ";
            object?[] arguments = [null, null];

            bool accepted = (bool)typeof(MainWindow).GetMethod("TryReadRules", PrivateInstance)!.Invoke(window, arguments)!;

            Assert.IsTrue(accepted);
            Assert.IsNull(arguments[1]);
            var parsedRules = (PackageRules)arguments[0]!;
            CollectionAssert.AreEqual(new[] { "drawings/Final plan.pdf", "images/Photo 01.jpg" }, parsedRules.RequiredPaths.ToArray());
            ((TextBox)window.FindName("RequiredPathsBox")).Text = string.Empty;
            ((TextBox)window.FindName("DestinationBox")).Text = fixture.Root;
            ((TextBox)window.FindName("PackageNameBox")).Text = "handover";
            typeof(MainWindow).GetField("plan", PrivateInstance)!.SetValue(window, scanned);
            typeof(MainWindow).GetField("initialized", PrivateInstance)!.SetValue(window, true);
            typeof(MainWindow).GetMethod("RefreshPlan", PrivateInstance)!.Invoke(window, null);
            Assert.IsTrue(((Button)window.FindName("BuildButton")).IsEnabled);

            typeof(MainWindow).GetField("lastOutput", PrivateInstance)!.SetValue(window, verifiedOutput);
            typeof(MainWindow).GetMethod("RefreshBuildAvailability", PrivateInstance)!.Invoke(window, null);
            Assert.IsTrue(((Button)window.FindName("ShowOutputButton")).IsEnabled);
            ((TextBox)window.FindName("DestinationBox")).Text = "not-an-absolute-path";
            Assert.IsFalse(((Button)window.FindName("BuildButton")).IsEnabled);
            Assert.IsFalse(((Button)window.FindName("ShowOutputButton")).IsEnabled);
            Assert.IsFalse(string.IsNullOrWhiteSpace(((TextBlock)window.FindName("OutputHintText")).Text));

            ((TextBox)window.FindName("DestinationBox")).Text = fixture.Root;
            ((TextBox)window.FindName("MaxFileBox")).Text = "invalid";
            Assert.IsFalse(((Button)window.FindName("BuildButton")).IsEnabled);
            Assert.IsNotNull(typeof(MainWindow).GetField("ruleError", PrivateInstance)!.GetValue(window));
            ((TextBox)window.FindName("MaxFileBox")).Text = string.Empty;

            foreach (bool canceled in new[] { false, true })
            {
                object priorPlan = typeof(MainWindow).GetField("plan", PrivateInstance)!.GetValue(window)!;
                typeof(MainWindow).GetField("lastOutput", PrivateInstance)!.SetValue(window, verifiedOutput);
                Func<IProgress<PackageProgress>, CancellationToken, Task> action = (_, _) =>
                {
                    Assert.IsFalse(((Grid)window.FindName("MainEditor")).IsEnabled);
                    Assert.IsFalse(((Button)window.FindName("BuildButton")).IsEnabled);
                    Assert.IsTrue(((Button)window.FindName("CancelButton")).IsEnabled);
                    return canceled ? Task.FromCanceled(new CancellationToken(true))
                        : Task.FromException(new InvalidDataException("invalid recipe regression"));
                };

                await (Task)typeof(MainWindow).GetMethod("RunOperation", PrivateInstance)!.Invoke(window, [action])!;

                Assert.AreSame(priorPlan, typeof(MainWindow).GetField("plan", PrivateInstance)!.GetValue(window));
                Assert.IsNull(typeof(MainWindow).GetField("operation", PrivateInstance)!.GetValue(window));
                Assert.IsNull(typeof(MainWindow).GetField("lastOutput", PrivateInstance)!.GetValue(window));
                Assert.IsTrue(((Grid)window.FindName("MainEditor")).IsEnabled);
                Assert.IsTrue(((Button)window.FindName("BuildButton")).IsEnabled);
                Assert.IsFalse(((Button)window.FindName("CancelButton")).IsEnabled);
                Assert.IsFalse(((Button)window.FindName("ShowOutputButton")).IsEnabled);
                Assert.IsFalse(((ProgressBar)window.FindName("ProgressBar")).IsIndeterminate);
                string status = ((TextBlock)window.FindName("StatusText")).Text;
                Assert.IsTrue(status.Contains(canceled ? "отменена" : "invalid recipe regression", StringComparison.Ordinal));
            }
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, File.ReadAllBytes(source));
            Assert.IsFalse(window.IsVisible);
        }
        finally
        {
            window.Close();
        }
    }
}
