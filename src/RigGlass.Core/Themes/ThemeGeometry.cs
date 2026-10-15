namespace RigGlass.Themes;

/// <summary>
/// How a theme designed for one canvas size is mapped onto a panel of a
/// different size or aspect ratio.
/// </summary>
/// <remarks>
/// Themes for existing panel software are written in device pixels, so a layout
/// built for a 480x320 screen is unusable on an 1280x480 one and the community
/// ends up maintaining a separate copy of every theme per resolution. RigGlass
/// treats the authored size as a design canvas and transforms it at render time,
/// so one theme file covers every panel.
/// </remarks>
public enum ThemeScaleMode
{
    /// <summary>
    /// Scale uniformly until the design canvas fits inside the panel, leaving
    /// bars on two sides when the aspect ratios differ. Nothing is cropped and
    /// nothing is distorted.
    /// </summary>
    Fit = 0,

    /// <summary>
    /// Scale uniformly until the design canvas covers the panel, cropping the
    /// overflow. Good for themes whose edges are decorative.
    /// </summary>
    Fill,

    /// <summary>
    /// Scale each axis independently so the design canvas exactly matches the
    /// panel. Fills the screen at the cost of distorting circles and text.
    /// </summary>
    Stretch,

    /// <summary>
    /// Do not scale. Widgets land on device pixels as authored, which keeps text
    /// and hairlines perfectly crisp; combine with anchoring for layouts that
    /// should adapt by moving rather than by scaling.
    /// </summary>
    None,
}

/// <summary>Which edges a widget keeps a fixed distance from when the canvas is resized.</summary>
[Flags]
public enum AnchorEdges
{
    /// <summary>Position scales with the canvas; no edge is pinned.</summary>
    None = 0,

    /// <summary>Keep the authored distance to the left edge.</summary>
    Left = 1 << 0,

    /// <summary>Keep the authored distance to the top edge.</summary>
    Top = 1 << 1,

    /// <summary>Keep the authored distance to the right edge.</summary>
    Right = 1 << 2,

    /// <summary>Keep the authored distance to the bottom edge.</summary>
    Bottom = 1 << 3,

    /// <summary>Stretch horizontally with the canvas.</summary>
    LeftRight = Left | Right,

    /// <summary>Stretch vertically with the canvas.</summary>
    TopBottom = Top | Bottom,

    /// <summary>Stretch in both directions.</summary>
    All = Left | Top | Right | Bottom,
}

/// <summary>Horizontal text alignment.</summary>
public enum HorizontalAlignment
{
    /// <summary>Align to the left edge of the widget box.</summary>
    Left = 0,

    /// <summary>Centre within the widget box.</summary>
    Center,

    /// <summary>Align to the right edge of the widget box.</summary>
    Right,
}

/// <summary>Vertical text alignment.</summary>
public enum VerticalAlignment
{
    /// <summary>Align to the top edge of the widget box.</summary>
    Top = 0,

    /// <summary>Centre within the widget box.</summary>
    Middle,

    /// <summary>Align to the bottom edge of the widget box.</summary>
    Bottom,

    /// <summary>Align the text baseline to the bottom of the widget box.</summary>
    Baseline,
}

/// <summary>How an image fills its box.</summary>
public enum ImageFit
{
    /// <summary>Scale uniformly to fit entirely inside the box.</summary>
    Contain = 0,

    /// <summary>Scale uniformly to cover the box, cropping the overflow.</summary>
    Cover,

    /// <summary>Scale each axis to match the box exactly.</summary>
    Stretch,

    /// <summary>Draw at native size, centred.</summary>
    None,

    /// <summary>Repeat to fill the box.</summary>
    Tile,
}

/// <summary>A rectangle in theme design units.</summary>
/// <param name="X">Distance from the left edge of the canvas.</param>
/// <param name="Y">Distance from the top edge of the canvas.</param>
/// <param name="Width">Width in design units.</param>
/// <param name="Height">Height in design units.</param>
public readonly record struct ThemeRect(double X, double Y, double Width, double Height)
{
    /// <summary>A zero rectangle at the origin.</summary>
    public static ThemeRect Empty { get; }

    /// <summary>Right edge.</summary>
    public double Right => X + Width;

    /// <summary>Bottom edge.</summary>
    public double Bottom => Y + Height;

    /// <summary>Horizontal centre.</summary>
    public double CenterX => X + (Width / 2d);

    /// <summary>Vertical centre.</summary>
    public double CenterY => Y + (Height / 2d);

    /// <summary>Whether the rectangle encloses any area.</summary>
    public bool IsEmpty => Width <= 0d || Height <= 0d;

    /// <summary>Returns this rectangle offset by the given amounts.</summary>
    public ThemeRect Offset(double dx, double dy) => this with { X = X + dx, Y = Y + dy };

    /// <summary>Returns this rectangle shrunk on every side by <paramref name="amount"/>.</summary>
    public ThemeRect Deflate(double amount) =>
        new(X + amount, Y + amount, Math.Max(0d, Width - (2d * amount)), Math.Max(0d, Height - (2d * amount)));
}
