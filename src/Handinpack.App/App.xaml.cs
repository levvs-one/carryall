using System.ComponentModel;
using System.Windows;

namespace Handinpack.App;

public partial class App : Application
{
    private ResourceDictionary? highContrastPalette;

    protected override void OnStartup(StartupEventArgs e)
    {
        ApplyHighContrast();
        SystemParameters.StaticPropertyChanged += SystemParametersChanged;
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SystemParameters.StaticPropertyChanged -= SystemParametersChanged;
        base.OnExit(e);
    }

    private void SystemParametersChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemParameters.HighContrast))
        {
            Dispatcher.InvokeAsync(ApplyHighContrast);
        }
    }

    private void ApplyHighContrast()
    {
        if (highContrastPalette is not null)
        {
            Resources.MergedDictionaries.Remove(highContrastPalette);
            highContrastPalette = null;
        }

        if (!SystemParameters.HighContrast)
        {
            return;
        }

        highContrastPalette = new ResourceDictionary
        {
            ["CanvasBrush"] = SystemColors.WindowBrush,
            ["SurfaceBrush"] = SystemColors.WindowBrush,
            ["InkBrush"] = SystemColors.WindowTextBrush,
            ["MutedBrush"] = SystemColors.GrayTextBrush,
            ["RuleBrush"] = SystemColors.WindowTextBrush,
            ["ControlBorderBrush"] = SystemColors.WindowTextBrush,
            ["AccentBrush"] = SystemColors.HighlightBrush,
            ["SelectionBrush"] = SystemColors.HighlightBrush,
            ["SelectionInkBrush"] = SystemColors.HighlightTextBrush,
            ["OnAccentBrush"] = SystemColors.HighlightTextBrush,
            ["ErrorBrush"] = SystemColors.WindowTextBrush,
            ["WarningBrush"] = SystemColors.WindowTextBrush
        };
        Resources.MergedDictionaries.Add(highContrastPalette);
    }
}
