using System.Windows;
using System.Windows.Media;
using Wpf.Ui.Appearance;

namespace PaceWheel.App;

public partial class App : Application
{
    /// <summary>Racing orange, used as the Fluent accent color.</summary>
    public static readonly Color AccentColor = Color.FromRgb(0xFF, 0x6A, 0x00);

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ApplicationAccentColorManager.Apply(AccentColor, ApplicationTheme.Dark);
    }
}
