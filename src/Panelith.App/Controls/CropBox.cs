using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Panelith.Media;

namespace Panelith.App.Controls;

/// <summary>
/// Shows a picture with a panel-shaped crop over it. Drag the crop to move it,
/// drag its corner or use the mouse wheel to resize it; its shape never changes.
/// </summary>
public sealed class CropBox : Control
{
    /// <summary>The picture to crop.</summary>
    public static readonly StyledProperty<Bitmap?> SourceProperty =
        AvaloniaProperty.Register<CropBox, Bitmap?>(nameof(Source));

    /// <summary>The crop, as fractions of the picture.</summary>
    public static readonly StyledProperty<CropRect> CropProperty =
        AvaloniaProperty.Register<CropBox, CropRect>(nameof(Crop), CropRect.Full, defaultBindingMode: BindingMode.TwoWay);

    /// <summary>The panel's width over height, which the crop keeps.</summary>
    public static readonly StyledProperty<double> TargetAspectProperty =
        AvaloniaProperty.Register<CropBox, double>(nameof(TargetAspect), 4d);

    private const double Handle = 14d;
    private static readonly IBrush Shade = new SolidColorBrush(Color.FromArgb(170, 0, 0, 0));
    private static readonly IPen Edge = new Pen(new SolidColorBrush(Color.Parse("#36c5f0")), 2d);
    private static readonly IBrush HandleFill = new SolidColorBrush(Color.Parse("#36c5f0"));

    private Point _grab;
    private CropRect _grabCrop;
    private bool _moving;
    private bool _resizing;

    static CropBox()
    {
        AffectsRender<CropBox>(SourceProperty, CropProperty);
        AffectsMeasure<CropBox>(SourceProperty);
        FocusableProperty.OverrideDefaultValue<CropBox>(true);
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

    /// <inheritdoc cref="TargetAspectProperty"/>
    public double TargetAspect
    {
        get => GetValue(TargetAspectProperty);
        set => SetValue(TargetAspectProperty, value);
    }

    /// <summary>
    /// <paramref name="crop"/> resized to <paramref name="width"/> (a fraction
    /// of the picture's width) about its centre, keeping the panel's shape and
    /// staying inside the picture.
    /// </summary>
    public static CropRect Resize(CropRect crop, double width, int sourceWidth, int sourceHeight, double targetAspect)
    {
        var heightPerWidth = sourceWidth / (sourceHeight * targetAspect);
        var widest = Math.Min(1d, 1d / heightPerWidth);
        width = Math.Clamp(width, Math.Min(widest, 0.05d), widest);
        var height = width * heightPerWidth;
        var centreX = crop.X + (crop.Width / 2d);
        var centreY = crop.Y + (crop.Height / 2d);
        return Clamp(new CropRect(centreX - (width / 2d), centreY - (height / 2d), width, height));
    }

    /// <summary>Moves <paramref name="crop"/> back inside the picture without resizing it.</summary>
    public static CropRect Clamp(CropRect crop) => crop with
    {
        X = Math.Clamp(crop.X, 0d, Math.Max(0d, 1d - crop.Width)),
        Y = Math.Clamp(crop.Y, 0d, Math.Max(0d, 1d - crop.Height)),
    };

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        var picture = PictureRect();
        if (Source is not { } bitmap || picture.Width <= 0d)
        {
            context.FillRectangle(new SolidColorBrush(Color.FromRgb(10, 13, 18)), new Rect(Bounds.Size));
            return;
        }

        context.DrawImage(bitmap, new Rect(bitmap.Size), picture);
        var crop = CropRectOn(picture);
        context.FillRectangle(Shade, new Rect(picture.Left, picture.Top, picture.Width, crop.Top - picture.Top));
        context.FillRectangle(Shade, new Rect(picture.Left, crop.Bottom, picture.Width, picture.Bottom - crop.Bottom));
        context.FillRectangle(Shade, new Rect(picture.Left, crop.Top, crop.Left - picture.Left, crop.Height));
        context.FillRectangle(Shade, new Rect(crop.Right, crop.Top, picture.Right - crop.Right, crop.Height));
        context.DrawRectangle(null, Edge, crop);
        context.FillRectangle(HandleFill, new Rect(crop.Right - (Handle / 2d), crop.Bottom - (Handle / 2d), Handle, Handle));
    }

    /// <inheritdoc />
    protected override Size MeasureOverride(Size availableSize)
    {
        var aspect = Source is { } bitmap ? bitmap.Size.Width / Math.Max(1d, bitmap.Size.Height) : 16d / 9d;
        var width = double.IsInfinity(availableSize.Width) ? 720d : availableSize.Width;
        var height = width / aspect;
        if (!double.IsInfinity(availableSize.Height) && height > availableSize.Height)
        {
            height = availableSize.Height;
            width = height * aspect;
        }

        return new Size(width, height);
    }

    /// <inheritdoc />
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var picture = PictureRect();
        if (Source is null || picture.Width <= 0d)
        {
            return;
        }

        var at = e.GetPosition(this);
        var crop = CropRectOn(picture);
        var corner = new Rect(crop.Right - Handle, crop.Bottom - Handle, Handle * 2d, Handle * 2d);
        _resizing = corner.Contains(at);
        _moving = !_resizing && crop.Contains(at);
        if (!_resizing && !_moving)
        {
            // Clicking outside the crop centres it there.
            Crop = Clamp(Crop with
            {
                X = ((at.X - picture.Left) / picture.Width) - (Crop.Width / 2d),
                Y = ((at.Y - picture.Top) / picture.Height) - (Crop.Height / 2d),
            });
            _moving = true;
        }

        _grab = at;
        _grabCrop = Crop;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    /// <inheritdoc />
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_moving && !_resizing)
        {
            return;
        }

        var picture = PictureRect();
        var at = e.GetPosition(this);
        var dx = (at.X - _grab.X) / picture.Width;
        var dy = (at.Y - _grab.Y) / picture.Height;
        if (_moving)
        {
            Crop = Clamp(_grabCrop with { X = _grabCrop.X + dx, Y = _grabCrop.Y + dy });
            return;
        }

        if (Source is { } bitmap)
        {
            // Resize from the top-left corner, which stays put.
            var (w, h) = (bitmap.PixelSize.Width, bitmap.PixelSize.Height);
            var heightPerWidth = w / (h * TargetAspect);
            var width = Math.Max(_grabCrop.Width + dx, (_grabCrop.Height + dy) / heightPerWidth);
            width = Math.Clamp(width, 0.05d, Math.Min(1d - _grabCrop.X, (1d - _grabCrop.Y) / heightPerWidth));
            Crop = new CropRect(_grabCrop.X, _grabCrop.Y, width, width * heightPerWidth);
        }
    }

    /// <inheritdoc />
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _moving = _resizing = false;
        e.Pointer.Capture(null);
    }

    /// <inheritdoc />
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (Source is not { } bitmap)
        {
            return;
        }

        var factor = e.Delta.Y > 0 ? 0.92d : 1d / 0.92d;
        Crop = Resize(Crop, Crop.Width * factor, bitmap.PixelSize.Width, bitmap.PixelSize.Height, TargetAspect);
        e.Handled = true;
    }

    private Rect PictureRect()
    {
        if (Source is not { } bitmap)
        {
            return default;
        }

        var bounds = Bounds.Size;
        var aspect = bitmap.Size.Width / Math.Max(1d, bitmap.Size.Height);
        var width = bounds.Width;
        var height = width / aspect;
        if (height > bounds.Height)
        {
            height = bounds.Height;
            width = height * aspect;
        }

        return new Rect((bounds.Width - width) / 2d, (bounds.Height - height) / 2d, width, height);
    }

    private Rect CropRectOn(Rect picture)
    {
        var crop = Crop;
        return new Rect(
            picture.Left + (crop.X * picture.Width),
            picture.Top + (crop.Y * picture.Height),
            crop.Width * picture.Width,
            crop.Height * picture.Height);
    }
}
