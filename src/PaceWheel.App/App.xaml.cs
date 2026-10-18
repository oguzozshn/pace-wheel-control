using System.Windows;
using System.Windows.Media;
using Wpf.Ui.Appearance;

namespace PaceWheel.App;

public partial class App : Application
{
    /// <summary>Cyan accent, used as the Fluent accent color (matches Theme/Racing.xaml).</summary>
    public static readonly Color AccentColor = Color.FromRgb(0x00, 0xB4, 0xFF);

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ApplicationAccentColorManager.Apply(AccentColor, ApplicationTheme.Dark);
    }
}
