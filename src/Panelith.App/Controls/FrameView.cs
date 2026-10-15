using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Panelith.App.Controls;

/// <summary>Draws a <see cref="LiveFrame"/> scaled to fit, with rounded corners.</summary>
public sealed class FrameView : Control
{
    /// <summary>The frame to show.</summary>
    public static readonly StyledProperty<LiveFrame?> FrameProperty =
        AvaloniaProperty.Register<FrameView, LiveFrame?>(nameof(Frame));

    /// <summary>Corner radius of the picture.</summary>
    public static readonly StyledProperty<double> RadiusProperty =
        AvaloniaProperty.Register<FrameView, double>(nameof(Radius), 10d);

    /// <summary>Width over height reserved before the first frame arrives.</summary>
    public static readonly StyledProperty<double> AspectRatioProperty =
        AvaloniaProperty.Register<FrameView, double>(nameof(AspectRatio), 4d);

    static FrameView()
    {
        AffectsRender<FrameView>(RadiusProperty);
        AffectsMeasure<FrameView>(AspectRatioProperty);
    }

    /// <inheritdoc cref="FrameProperty"/>
    public LiveFrame? Frame
    {
        get => GetValue(FrameProperty);
        set => SetValue(FrameProperty, value);
    }

    /// <inheritdoc cref="RadiusProperty"/>
    public double Radius
    {
        get => GetValue(RadiusProperty);
        set => SetValue(RadiusProperty, value);
    }

    /// <inheritdoc cref="AspectRatioProperty"/>
    public double AspectRatio
    {
        get => GetValue(AspectRatioProperty);
        set => SetValue(AspectRatioProperty, value);
    }

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        var bitmap = Frame?.Bitmap;
        var aspect = bitmap is null ? AspectRatio : (double)bitmap.PixelSize.Width / bitmap.PixelSize.Height;

        var width = bounds.Width;
        var height = width / aspect;
        if (height > bounds.Height)
        {
            height = bounds.Height;
            width = height * aspect;
        }

        var target = new Rect((bounds.Width - width) / 2, (bounds.Height - height) / 2, width, height);
        using (context.PushClip(new RoundedRect(target, Radius)))
        {
            if (bitmap is null)
            {
                context.FillRectangle(new SolidColorBrush(Color.FromRgb(10, 13, 18)), target);
                return;
            }

            context.DrawImage(bitmap, new Rect(bitmap.Size), target);
        }
    }

    /// <inheritdoc />
    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? 480 : availableSize.Width;
        var height = width / Math.Max(0.1, AspectRatio);
        if (!double.IsInfinity(availableSize.Height) && height > availableSize.Height)
        {
            height = availableSize.Height;
            width = height * AspectRatio;
        }

        return new Size(width, height);
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == FrameProperty)
        {
            if (change.OldValue is LiveFrame previous)
            {
                previous.Changed -= OnFrameChanged;
            }

            if (change.NewValue is LiveFrame next)
            {
                next.Changed += OnFrameChanged;
            }

            InvalidateVisual();
        }
    }

    private void OnFrameChanged(object? sender, EventArgs e) => InvalidateVisual();
}
