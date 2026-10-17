using System.Globalization;
using System.Windows.Controls;

namespace PaceWheel.App.Controls;

/// <summary>Rotating wheel gauge driven by the DirectInput X axis (0..65535, center 32767).</summary>
public partial class SteeringWheelView : UserControl
{
    private int _rangeDegrees = 900;

    public SteeringWheelView()
    {
        InitializeComponent();
    }

    /// <summary>Total rotation range of the wheel (lock to lock), from the wheel's settings.</summary>
    public int RangeDegrees
    {
        get => _rangeDegrees;
        set
        {
            _rangeDegrees = Math.Clamp(value, 90, 1080);
            RangeText.Text = $"{_rangeDegrees}° aralık";
        }
    }

    public void SetPosition(int axisValue)
    {
        double normalized = Math.Clamp((axisValue - 32767) / 32767.0, -1, 1);
        double degrees = normalized * _rangeDegrees / 2;
        Rotation.Angle = degrees;
        AngleText.Text = $"{degrees.ToString("0", CultureInfo.InvariantCulture)}°";
    }
}
