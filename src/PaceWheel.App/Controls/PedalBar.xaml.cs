using System.Windows.Controls;
using System.Windows.Media;

namespace PaceWheel.App.Controls;

/// <summary>Vertical pedal gauge for a DirectInput axis (0..65535, 0 = released).</summary>
public partial class PedalBar : UserControl
{
    public PedalBar()
    {
        InitializeComponent();
    }

    public string Label
    {
        get => LabelText.Text;
        set => LabelText.Text = value;
    }

    public Brush BarBrush
    {
        get => Fill.Background;
        set => Fill.Background = value;
    }

    public void SetValue(int axisValue)
    {
        double fraction = Math.Clamp(axisValue / 65535.0, 0, 1);
        Fill.Height = fraction * Track.Height;
        PercentText.Text = $"{Math.Round(fraction * 100)}%";
    }
}
