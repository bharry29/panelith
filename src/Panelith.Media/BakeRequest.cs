namespace Panelith.Media;

/// <summary>
/// The part of a source picture to keep, as fractions of its width and height,
/// so the same crop works whatever resolution the source decodes at.
/// </summary>
/// <param name="X">Left edge, 0 to 1.</param>
/// <param name="Y">Top edge, 0 to 1.</param>
/// <param name="Width">Width, above 0 up to 1.</param>
/// <param name="Height">Height, above 0 up to 1.</param>
public readonly record struct CropRect(double X, double Y, double Width, double Height)
{
    /// <summary>The whole picture.</summary>
    public static CropRect Full { get; } = new(0d, 0d, 1d, 1d);

    /// <summary>
    /// The largest centred crop of a <paramref name="sourceWidth"/> x <paramref name="sourceHeight"/>
    /// picture with the panel's aspect ratio: what "cover" would show.
    /// </summary>
    public static CropRect Cover(int sourceWidth, int sourceHeight, int panelWidth, int panelHeight)
    {
        var source = sourceWidth / (double)sourceHeight;
        var panel = panelWidth / (double)panelHeight;
        if (source > panel)
        {
            var width = panel / source;
            return new CropRect((1d - width) / 2d, 0d, width, 1d);
        }

        var height = source / panel;
        return new CropRect(0d, (1d - height) / 2d, 1d, height);
    }

    /// <summary>Whether the crop lies inside the picture and has an area.</summary>
    public bool IsValid =>
        double.IsFinite(X) && double.IsFinite(Y) && double.IsFinite(Width) && double.IsFinite(Height)
        && X >= 0d && Y >= 0d && Width > 0d && Height > 0d
        && X + Width <= 1.000001d && Y + Height <= 1.000001d;

    /// <summary>The crop in pixels of a <paramref name="sourceWidth"/> x <paramref name="sourceHeight"/> picture.</summary>
    public (float Left, float Top, float Right, float Bottom) ToPixels(int sourceWidth, int sourceHeight) =>
        ((float)(X * sourceWidth), (float)(Y * sourceHeight),
         (float)(Math.Min(1d, X + Width) * sourceWidth), (float)(Math.Min(1d, Y + Height) * sourceHeight));

    /// <summary>
    /// Whether the crop holds fewer source pixels than the panel shows, so the
    /// result will look soft. The import screen warns about this.
    /// </summary>
    public bool IsSmallerThan(int sourceWidth, int sourceHeight, int panelWidth, int panelHeight) =>
        Width * sourceWidth < panelWidth - 0.5d || Height * sourceHeight < panelHeight - 0.5d;
}

/// <summary>What to bake and how.</summary>
/// <param name="Source">The video file to read.</param>
/// <param name="Output">Where to write the <c>.ptclip</c>.</param>
/// <param name="PanelWidth">Width of the panel the clip is for.</param>
/// <param name="PanelHeight">Height of the panel the clip is for.</param>
/// <param name="Crop">The part of the video to keep; null keeps the centred panel-shaped part.</param>
/// <param name="Start">Where in the video the loop starts.</param>
/// <param name="End">Where it ends; null for the end of the video.</param>
/// <param name="Fps">Frames per second in the clip.</param>
/// <param name="LoopCrossfade">Whether to blend the end into the start so the loop has no visible jump.</param>
/// <param name="Quality">JPEG quality, 1 to 100.</param>
public sealed record BakeRequest(
    string Source,
    string Output,
    int PanelWidth,
    int PanelHeight,
    CropRect? Crop = null,
    TimeSpan Start = default,
    TimeSpan? End = null,
    int Fps = BakeRequest.DefaultFps,
    bool LoopCrossfade = true,
    int Quality = BakeRequest.DefaultQuality)
{
    /// <summary>The panel frame rate clips are baked at.</summary>
    public const int DefaultFps = 15;

    /// <summary>A JPEG quality that keeps 1920x480 frames around 100 KB.</summary>
    public const int DefaultQuality = 85;

    /// <summary>How long the loop crossfade lasts.</summary>
    public static TimeSpan CrossfadeLength { get; } = TimeSpan.FromSeconds(0.5d);

    /// <summary>Throws when the request cannot produce a valid clip.</summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Source) || string.IsNullOrWhiteSpace(Output))
        {
            throw new ArgumentException("A bake needs a source and an output file.");
        }

        ClipLimits.Check(PanelWidth, PanelHeight, Fps, 1);
        if (Crop is { IsValid: false } crop)
        {
            throw new ArgumentException($"The crop {crop} is not inside the picture.");
        }

        if (Start < TimeSpan.Zero || (End is { } end && end <= Start))
        {
            throw new ArgumentException("The loop must start at or after 0 and end after it starts.");
        }

        if (Quality is < 1 or > 100)
        {
            throw new ArgumentException("JPEG quality must be between 1 and 100.");
        }
    }

    /// <summary>The longest loop the clip format can hold at this frame rate.</summary>
    public TimeSpan MaxLength => TimeSpan.FromSeconds(ClipLimits.MaxFrames / (double)Fps);
}

/// <summary>How far a bake has got.</summary>
/// <param name="Fraction">0 to 1, or null while the length is unknown.</param>
/// <param name="Frames">Clip frames produced so far.</param>
public readonly record struct BakeProgress(double? Fraction, int Frames);
