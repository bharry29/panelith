namespace OmniPanel.Sensors;

/// <summary>
/// The physical quantity a sensor measures. Determines the default unit, the
/// default formatting and the sensible range used by gauge-style widgets.
/// </summary>
public enum SensorKind
{
    /// <summary>Quantity is not known or does not fit any other category.</summary>
    Unknown = 0,

    /// <summary>Temperature, in degrees Celsius.</summary>
    Temperature,

    /// <summary>Utilisation or other 0-100 percentage.</summary>
    Load,

    /// <summary>Clock frequency, in megahertz.</summary>
    Clock,

    /// <summary>Electrical power, in watts.</summary>
    Power,

    /// <summary>Voltage, in volts.</summary>
    Voltage,

    /// <summary>Electrical current, in amperes.</summary>
    Current,

    /// <summary>Rotational speed, in revolutions per minute.</summary>
    Fan,

    /// <summary>Fan duty cycle or pump duty, in percent.</summary>
    Control,

    /// <summary>A quantity of bytes, such as memory used or disk capacity.</summary>
    Data,

    /// <summary>A byte rate, such as network or disk throughput, in bytes per second.</summary>
    Throughput,

    /// <summary>A count of discrete things, such as processes or frames.</summary>
    Count,

    /// <summary>A duration, in seconds.</summary>
    Duration,

    /// <summary>Frames per second.</summary>
    FrameRate,

    /// <summary>A free-form text value, such as a GPU name or the current time.</summary>
    Text,
}
