using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace PaceWheel.App.Controls;

/// <summary>Labeled slider for one wheel setting; marks the value when it differs from the wheel's.</summary>
public partial class SettingSlider : UserControl
{
    private bool _initialized;
    private int? _original;

    public SettingSlider()
    {
        InitializeComponent();
        _initialized = true;
    }

    public event EventHandler? ValueChanged;

    public string Title { get => TitleText.Text; set => TitleText.Text = value; }

    public string Unit { get => UnitText.Text; set => UnitText.Text = value; }

    public double Minimum
    {
        get => ValueSlider.Minimum;
        set { ValueSlider.Minimum = value; MinText.Text = Format(value); }
    }

    public double Maximum
    {
        get => ValueSlider.Maximum;
        set { ValueSlider.Maximum = value; MaxText.Text = Format(value); }
    }

    public double Step
    {
        get => ValueSlider.TickFrequency;
        set { ValueSlider.TickFrequency = value; ValueSlider.SmallChange = value; ValueSlider.LargeChange = value; }
    }

    public int Value => (int)Math.Round(ValueSlider.Value);

    public bool IsChanged => _original is int o && o != Value;

    /// <summary>Shows the value read from the wheel and enables the slider.</summary>
    public void Load(int wheelValue)
    {
        _original = wheelValue;
        ValueSlider.IsEnabled = true;
        ValueSlider.Value = Math.Clamp(wheelValue, ValueSlider.Minimum, ValueSlider.Maximum);
        OriginalText.Text = $"direksiyonda: {wheelValue}{Unit}";
        Refresh();
    }

    private void OnSliderValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // The slider raises this during InitializeComponent, before the other elements exist.
        if (!_initialized)
        {
            return;
        }
        Refresh();
        ValueChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Refresh()
    {
        ValueText.Text = _original is null ? "-" : Format(Value);
        ChangedBadge.Visibility = IsChanged ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string Format(double value) => value.ToString("0", CultureInfo.InvariantCulture);
}
