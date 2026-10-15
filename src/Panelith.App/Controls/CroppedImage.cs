using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Panelith.Media;

namespace Panelith.App.Controls;

/// <summary>Shows the <see cref="Crop"/> part of a picture at the panel's shape: what the panel will show.</summary>
public sealed class CroppedImage : Control
{
    /// <summary>The whole picture.</summary>
    public static readonly StyledProperty<Bitmap?> SourceProperty =
        AvaloniaProperty.Register<CroppedImage, Bitmap?>(nameof(Source));

    /// <summary>The part to show, as fractions of the picture.</summary>
    public static readonly StyledProperty<CropRect> CropProperty =
        AvaloniaProperty.Register<CroppedImage, CropRect>(nameof(Crop), CropRect.Full);

    /// <summary>The panel's width over height.</summary>
    public static readonly StyledProperty<double> AspectRatioProperty =
        AvaloniaProperty.Register<CroppedImage, double>(nameof(AspectRatio), 4d);

    private static readonly IBrush Empty = new SolidColorBrush(Color.FromRgb(10, 13, 18));

    static CroppedImage()
    {
        AffectsRender<CroppedImage>(SourceProperty, CropProperty);
        AffectsMeasure<CroppedImage>(AspectRatioProperty);
    }

    /// <inheritdoc cref="SourceProperty"/>
    public Bitmap? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    /// <inheritdoc cref="CropProperty"/>
    public CropRect Crop
    {
        get => GetValue(CropProperty);
        set => SetValue(CropProperty, value);
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
        var target = new Rect(Bounds.Size);
        using (context.PushClip(new RoundedRect(target, 5d)))
        {
            if (Source is not { } bitmap)
            {
                context.FillRectangle(Empty, target);
                return;
            }

            var size = bitmap.Size;
            var crop = Crop;
            context.DrawImage(bitmap, new Rect(crop.X * size.Width, crop.Y * size.Height, crop.Width * size.Width, crop.Height * size.Height), target);
        }
    }

    /// <inheritdoc />
    protected override Size MeasureOverride(Size availableSize)
    {
        var aspect = Math.Max(0.1d, AspectRatio);
        var width = double.IsInfinity(availableSize.Width) ? 720d : availableSize.Width;
        var height = width / aspect;
        if (!double.IsInfinity(availableSize.Height) && height > availableSize.Height)
        {
            height = availableSize.Height;
            width = height * aspect;
        }

        return new Size(width, height);
    }
}
