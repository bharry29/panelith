using System.Collections;
using System.Collections.Specialized;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Panelith.App.Controls;

/// <summary>Which part of a <see cref="Filmstrip"/> selection moved.</summary>
public enum FilmstripEdge
{
    /// <summary>The start handle.</summary>
    Start,

    /// <summary>The end handle.</summary>
    End,

    /// <summary>The whole selection, keeping its length.</summary>
    Both,
}

/// <summary>
/// Pictures from across a video with a selection on top. Drag a handle to
/// move that end, drag the middle to move the part, or click elsewhere to put
/// the part there. Left and Right nudge the last handle used by a tenth of a
/// second, a second with Shift.
/// </summary>
public sealed class Filmstrip : Control
{
    /// <summary>The shortest part that can be chosen, in seconds.</summary>
    public const double MinLength = 1d;

    /// <summary>The pictures, evenly spaced over the video; null where one is not ready yet.</summary>
    public static readonly StyledProperty<IList?> ThumbnailsProperty =
        AvaloniaProperty.Register<Filmstrip, IList?>(nameof(Thumbnails));

    /// <summary>The video's length in seconds.</summary>
    public static readonly StyledProperty<double> DurationProperty =
        AvaloniaProperty.Register<Filmstrip, double>(nameof(Duration));

    /// <summary>Where the part starts, in seconds.</summary>
    public static readonly StyledProperty<double> StartProperty =
        AvaloniaProperty.Register<Filmstrip, double>(nameof(Start), defaultBindingMode: BindingMode.TwoWay);

    /// <summary>Where the part ends, in seconds.</summary>
    public static readonly StyledProperty<double> EndProperty =
        AvaloniaProperty.Register<Filmstrip, double>(nameof(End), defaultBindingMode: BindingMode.TwoWay);

    /// <summary>The time being shown by the player, in seconds, or below 0 for none.</summary>
    public static readonly StyledProperty<double> PlayheadProperty =
        AvaloniaProperty.Register<Filmstrip, double>(nameof(Playhead), -1d);

    /// <summary>The longest part that can be chosen, in seconds.</summary>
    public static readonly StyledProperty<double> MaxLengthProperty =
        AvaloniaProperty.Register<Filmstrip, double>(nameof(MaxLength), double.MaxValue);

    private const double StripHeight = 72d;
    private const double LabelHeight = 22d;
    private const double HandleWidth = 12d;
    private const double Radius = 8d;

    private static readonly IBrush Empty = new SolidColorBrush(Color.FromRgb(22, 27, 34));
    private static readonly IBrush Shade = new SolidColorBrush(Color.FromArgb(175, 5, 7, 10));
    private static readonly Color AccentColor = Color.Parse("#3EE0C5");
    private static readonly IBrush Accent = new SolidColorBrush(AccentColor);
    private static readonly IPen Edge = new Pen(Accent, 2d);
    private static readonly IPen Grip = new Pen(new SolidColorBrush(Color.FromArgb(200, 5, 7, 10)), 1.5d);
    private static readonly IPen PlayheadPen = new Pen(Brushes.White, 2d);
    private static readonly IBrush LabelBrush = new SolidColorBrush(Color.FromRgb(200, 208, 218));

    private FilmstripEdge _dragging;
    private bool _isDragging;
    private double _grabSeconds;
    private double _grabStart;
    private double _grabEnd;
    private FilmstripEdge _lastHandle = FilmstripEdge.Start;
    private INotifyCollectionChanged? _watched;

    static Filmstrip()
    {
        AffectsRender<Filmstrip>(ThumbnailsProperty, DurationProperty, StartProperty, EndProperty, PlayheadProperty);
        FocusableProperty.OverrideDefaultValue<Filmstrip>(true);
    }

    /// <inheritdoc cref="ThumbnailsProperty"/>
    public IList? Thumbnails
    {
        get => GetValue(ThumbnailsProperty);
        set => SetValue(ThumbnailsProperty, value);
    }

    /// <inheritdoc cref="DurationProperty"/>
    public double Duration
    {
        get => GetValue(DurationProperty);
        set => SetValue(DurationProperty, value);
    }

    /// <inheritdoc cref="StartProperty"/>
    public double Start
    {
        get => GetValue(StartProperty);
        set => SetValue(StartProperty, value);
    }

    /// <inheritdoc cref="EndProperty"/>
    public double End
    {
        get => GetValue(EndProperty);
        set => SetValue(EndProperty, value);
    }

    /// <inheritdoc cref="PlayheadProperty"/>
    public double Playhead
    {
        get => GetValue(PlayheadProperty);
        set => SetValue(PlayheadProperty, value);
    }

    /// <inheritdoc cref="MaxLengthProperty"/>
    public double MaxLength
    {
        get => GetValue(MaxLengthProperty);
        set => SetValue(MaxLengthProperty, value);
    }

    /// <summary>
    /// Keeps a part inside a <paramref name="duration"/>-second video and between
    /// <see cref="MinLength"/> and <paramref name="maxLength"/> long, changing
    /// only the side that <paramref name="moved"/>.
    /// </summary>
    public static (double Start, double End) Limit(double start, double end, double duration, double maxLength, FilmstripEdge moved)
    {
        if (duration <= 0d)
        {
            return (0d, 0d);
        }

        var shortest = Math.Min(MinLength, duration);
        var longest = Math.Clamp(maxLength, shortest, duration);
        switch (moved)
        {
            case FilmstripEdge.Start:
                end = Math.Clamp(end, shortest, duration);
                start = Math.Clamp(start, Math.Max(0d, end - longest), end - shortest);
                break;
            case FilmstripEdge.End:
                start = Math.Clamp(start, 0d, duration - shortest);
                end = Math.Clamp(end, start + shortest, Math.Min(duration, start + longest));
                break;
            default:
                var length = Math.Clamp(end - start, shortest, longest);
                start = Math.Clamp(start, 0d, duration - length);
                end = start + length;
                break;
        }

        return (start, end);
    }

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        var strip = new Rect(0d, 0d, Bounds.Width, StripHeight);
        using (context.PushClip(new RoundedRect(strip, Radius)))
        {
            context.FillRectangle(Empty, strip);
            DrawThumbnails(context, strip);
            if (Duration > 0d)
            {
                var left = X(Start);
                var right = X(End);
                context.FillRectangle(Shade, new Rect(0d, 0d, Math.Max(0d, left), StripHeight));
                context.FillRectangle(Shade, new Rect(right, 0d, Math.Max(0d, Bounds.Width - right), StripHeight));
                if (Playhead >= Start && Playhead <= End)
                {
                    var x = X(Playhead);
                    context.DrawLine(PlayheadPen, new Point(x, 4d), new Point(x, StripHeight - 4d));
                }
            }
        }

        if (Duration <= 0d)
        {
            return;
        }

        var (l, r) = (X(Start), X(End));
        context.DrawRectangle(null, Edge, new Rect(l, 1d, Math.Max(0d, r - l), StripHeight - 2d), 4d, 4d);
        DrawHandle(context, l - (HandleWidth / 2d));
        DrawHandle(context, r - (HandleWidth / 2d));
        var start = Label(Clock(Start));
        var end = Label(Clock(End));
        if (l + start.Width + 12d > r - end.Width)
        {
            var both = Label($"{Clock(Start)} – {Clock(End)}");
            DrawLabel(context, both, ((l + r) / 2d) - (both.Width / 2d));
        }
        else
        {
            DrawLabel(context, start, l);
            DrawLabel(context, end, r - end.Width);
        }
    }

    /// <inheritdoc />
    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 720d : availableSize.Width, StripHeight + LabelHeight);

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ThumbnailsProperty)
        {
            if (_watched is not null)
            {
                _watched.CollectionChanged -= OnThumbnailsChanged;
            }

            _watched = change.NewValue as INotifyCollectionChanged;
            if (_watched is not null)
            {
                _watched.CollectionChanged += OnThumbnailsChanged;
            }
        }
    }

    /// <inheritdoc />
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (Duration <= 0d || Bounds.Width <= 0d)
        {
            return;
        }

        var x = e.GetPosition(this).X;
        var (left, right) = (X(Start), X(End));
        if (Math.Abs(x - left) <= HandleWidth && Math.Abs(x - left) <= Math.Abs(x - right))
        {
            _dragging = FilmstripEdge.Start;
        }
        else if (Math.Abs(x - right) <= HandleWidth)
        {
            _dragging = FilmstripEdge.End;
        }
        else
        {
            _dragging = FilmstripEdge.Both;
            if (x < left || x > right)
            {
                var length = End - Start;
                Apply(Seconds(x) - (length / 2d), Seconds(x) + (length / 2d), FilmstripEdge.Both);
            }
        }

        if (_dragging != FilmstripEdge.Both)
        {
            _lastHandle = _dragging;
        }

        _isDragging = true;
        _grabSeconds = Seconds(x);
        (_grabStart, _grabEnd) = (Start, End);
        Focus();
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    /// <inheritdoc />
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_isDragging)
        {
            return;
        }

        var delta = Seconds(e.GetPosition(this).X) - _grabSeconds;
        switch (_dragging)
        {
            case FilmstripEdge.Start:
                Apply(_grabStart + delta, _grabEnd, FilmstripEdge.Start);
                break;
            case FilmstripEdge.End:
                Apply(_grabStart, _grabEnd + delta, FilmstripEdge.End);
                break;
            default:
                Apply(_grabStart + delta, _grabEnd + delta, FilmstripEdge.Both);
                break;
        }
    }

    /// <inheritdoc />
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _isDragging = false;
        e.Pointer.Capture(null);
    }

    /// <inheritdoc />
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key is not (Key.Left or Key.Right) || Duration <= 0d)
        {
            return;
        }

        var step = (e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 1d : 0.1d) * (e.Key == Key.Left ? -1d : 1d);
        if (_lastHandle == FilmstripEdge.Start)
        {
            Apply(Start + step, End, FilmstripEdge.Start);
        }
        else
        {
            Apply(Start, End + step, FilmstripEdge.End);
        }

        e.Handled = true;
    }

    private static string Clock(double seconds)
    {
        var time = TimeSpan.FromSeconds(Math.Max(0d, seconds));
        return time.TotalHours >= 1d ? time.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture) : time.ToString(@"m\:ss\.f", CultureInfo.InvariantCulture);
    }

    private static void DrawHandle(DrawingContext context, double left)
    {
        var handle = new Rect(left, 0d, HandleWidth, StripHeight);
        context.DrawRectangle(Accent, null, handle, 4d, 4d);
        var centre = handle.Center;
        context.DrawLine(Grip, new Point(centre.X - 2d, centre.Y - 8d), new Point(centre.X - 2d, centre.Y + 8d));
        context.DrawLine(Grip, new Point(centre.X + 2d, centre.Y - 8d), new Point(centre.X + 2d, centre.Y + 8d));
    }

    private void DrawThumbnails(DrawingContext context, Rect strip)
    {
        if (Thumbnails is not { Count: > 0 } thumbnails)
        {
            return;
        }

        var slot = strip.Width / thumbnails.Count;
        for (var i = 0; i < thumbnails.Count; i++)
        {
            if (thumbnails[i] is not Bitmap bitmap)
            {
                continue;
            }

            var target = new Rect(i * slot, 0d, slot + 0.5d, strip.Height);
            var size = bitmap.Size;
            var scale = Math.Max(target.Width / size.Width, target.Height / size.Height);
            var (w, h) = (target.Width / scale, target.Height / scale);
            context.DrawImage(bitmap, new Rect((size.Width - w) / 2d, (size.Height - h) / 2d, w, h), target);
        }
    }

    private static FormattedText Label(string text) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, 12d, LabelBrush);

    private void DrawLabel(DrawingContext context, FormattedText label, double left)
    {
        left = Math.Clamp(left, 0d, Math.Max(0d, Bounds.Width - label.Width));
        context.DrawText(label, new Point(left, StripHeight + 4d));
    }

    private void Apply(double start, double end, FilmstripEdge moved)
    {
        var (s, e) = Limit(start, end, Duration, MaxLength, moved);
        if (moved == FilmstripEdge.End)
        {
            End = e;
            Start = s;
        }
        else
        {
            Start = s;
            End = e;
        }
    }

    private double X(double seconds) => Duration <= 0d ? 0d : Math.Clamp(seconds / Duration, 0d, 1d) * Bounds.Width;

    private double Seconds(double x) => Bounds.Width <= 0d ? 0d : Math.Clamp(x / Bounds.Width, 0d, 1d) * Duration;

    private void OnThumbnailsChanged(object? sender, NotifyCollectionChangedEventArgs e) => InvalidateVisual();
}
