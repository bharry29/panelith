using System.Collections.Frozen;

namespace OmniPanel.Devices.Protocols.Thermalright;

/// <summary>What a Thermalright panel reported about its own screen.</summary>
/// <param name="Width">Native width in pixels.</param>
/// <param name="Height">Native height in pixels.</param>
/// <param name="UsesJpeg">Whether frames are sent as JPEG rather than raw pixels.</param>
/// <param name="BigEndian">For raw frames, whether RGB565 words are big-endian.</param>
/// <param name="PanelModel">The raw PM byte, kept for diagnostics.</param>
/// <param name="SubModel">The raw SUB byte, kept for diagnostics.</param>
/// <param name="ScreenCode">The resolved FBL screen code, kept for diagnostics.</param>
public readonly record struct TrccScreen(
    int Width,
    int Height,
    bool UsesJpeg,
    bool BigEndian,
    int PanelModel,
    int SubModel,
    int ScreenCode);

/// <summary>
/// Maps the identification bytes a Thermalright panel returns to its screen
/// geometry and pixel format.
/// </summary>
/// <remarks>
/// <para>
/// Thermalright ships dozens of coolers whose screens differ in size but share
/// a USB product id, so the product id alone cannot tell OmniPanel what
/// resolution to render at. Instead the panel is asked: every wire family
/// answers its init packet with a "PM" panel-model byte and often a "SUB"
/// sub-model byte, which resolve through these tables to a screen code the
/// vendor firmware calls "FBL", and from there to a resolution.
/// </para>
/// <para>
/// Asking the hardware is what lets OmniPanel support Thermalright models
/// nobody has tested yet, including ones released after this build: an unknown
/// cooler carrying a known screen code works immediately.
/// </para>
/// </remarks>
public static class TrccScreenTable
{
    /// <summary>Screen code to geometry and pixel format.</summary>
    private static readonly FrozenDictionary<int, (int Width, int Height, bool Jpeg, bool BigEndian)> Screens =
        new Dictionary<int, (int, int, bool, bool)>
        {
            [36] = (240, 240, false, false),
            [37] = (240, 240, false, false),
            [50] = (320, 240, false, false),
            [51] = (320, 240, false, false),
            [52] = (320, 240, false, false),
            [53] = (320, 240, false, false),
            [54] = (360, 360, true, false),
            [58] = (320, 240, false, false),
            [64] = (640, 480, false, false),
            [72] = (480, 480, false, false),
            [100] = (320, 320, false, true),
            [101] = (320, 320, false, true),
            [102] = (320, 320, false, true),
            [114] = (1600, 720, true, false),
            [128] = (1280, 480, true, false),
            [129] = (480, 480, false, false),
            [192] = (1920, 462, true, false),
            [224] = (854, 480, true, false),
        }.ToFrozenDictionary();

    /// <summary>
    /// Panel models whose screen code differs from the model byte. Every other
    /// model uses its own byte as the screen code.
    /// </summary>
    private static readonly FrozenDictionary<int, int> ScreenCodeOverrides =
        new Dictionary<int, int>
        {
            [5] = 50,
            [7] = 64,
            [9] = 224,
            [10] = 224,
            [11] = 224,
            [12] = 224,
            [13] = 224,
            [14] = 64,
            [15] = 224,
            [16] = 224,
            [17] = 224,
            [32] = 100,
            [50] = 50,
            [63] = 114,
            [64] = 114,
            [65] = 192,
            [66] = 192,
            [68] = 192,
            [69] = 192,
        }.ToFrozenDictionary();

    /// <summary>
    /// Screen code 224 is shared by several resolutions; the panel model byte
    /// picks between them.
    /// </summary>
    private static readonly FrozenDictionary<int, (int Width, int Height)> Screen224ByModel =
        new Dictionary<int, (int, int)>
        {
            [10] = (960, 540),
            [12] = (800, 480),
            [13] = (960, 320),
            [15] = (640, 172),
            [16] = (960, 540),
            [17] = (960, 320),
        }.ToFrozenDictionary();

    /// <summary>Screen code 192 is likewise shared.</summary>
    private static readonly FrozenDictionary<int, (int Width, int Height)> Screen192ByModel =
        new Dictionary<int, (int, int)>
        {
            [68] = (1280, 480),
            [69] = (1920, 440),
        }.ToFrozenDictionary();

    /// <summary>Model and sub-model pairs that together select a screen code.</summary>
    private static readonly FrozenDictionary<(int Model, int Sub), int> ScreenCodeByModelAndSub =
        new Dictionary<(int, int), int>
        {
            [(1, 48)] = 114,
            [(1, 49)] = 192,
        }.ToFrozenDictionary();

    /// <summary>Resolves a panel model byte to a screen code.</summary>
    public static int ScreenCodeFor(int panelModel, int subModel = 0)
    {
        if (ScreenCodeByModelAndSub.TryGetValue((panelModel, subModel), out var bySub))
        {
            return bySub;
        }

        return ScreenCodeOverrides.TryGetValue(panelModel, out var code) ? code : panelModel;
    }

    /// <summary>
    /// Resolves a screen code to geometry, or <see langword="null"/> when the
    /// code is not in the table.
    /// </summary>
    public static TrccScreen? Resolve(int screenCode, int panelModel = 0, int subModel = 0)
    {
        if (!Screens.TryGetValue(screenCode, out var screen))
        {
            return null;
        }

        var (width, height, jpeg, bigEndian) = screen;

        if (screenCode == 224)
        {
            (width, height) = Screen224ByModel.TryGetValue(panelModel, out var size) ? size : (854, 480);
        }
        else if (screenCode == 192)
        {
            (width, height) = Screen192ByModel.TryGetValue(panelModel, out var size) ? size : (1920, 462);
        }

        return new TrccScreen(width, height, jpeg, bigEndian, panelModel, subModel, screenCode);
    }

    /// <summary>Resolves a panel model byte straight through to geometry.</summary>
    public static TrccScreen? ResolveModel(int panelModel, int subModel = 0) =>
        Resolve(ScreenCodeFor(panelModel, subModel), panelModel, subModel);

    /// <summary>Every screen code the table knows, for diagnostics and documentation.</summary>
    public static IReadOnlyCollection<int> KnownScreenCodes => Screens.Keys;

    /// <summary>
    /// Reads the panel and sub-model bytes out of an LY handshake reply.
    /// </summary>
    /// <remarks>
    /// The two firmware variants put the identification in different places, and
    /// the original one biases its value: panel models up to 3 all mean "the
    /// first screen in the family", so they collapse to 1 before the base of 64
    /// is added.
    /// </remarks>
    public static (int PanelModel, int SubModel) ReadLyIdentity(ReadOnlySpan<byte> response, int productId)
    {
        if (response.Length < 37)
        {
            throw new ArgumentException(
                $"An LY handshake reply is at least 37 bytes; got {response.Length}.",
                nameof(response));
        }

        if (productId == LyProtocol.ProductIdLy)
        {
            var raw = response[20];
            if (raw <= 3)
            {
                raw = 1;
            }

            return (64 + raw, response[22] + 1);
        }

        return (50 + response[36], response[22]);
    }

    /// <summary>Reads the identity bytes out of a raw bulk handshake reply.</summary>
    public static (int PanelModel, int SubModel) ReadBulkIdentity(ReadOnlySpan<byte> response)
    {
        if (response.Length < 41)
        {
            throw new ArgumentException(
                $"A bulk handshake reply is at least 41 bytes; got {response.Length}.",
                nameof(response));
        }

        return (response[24], response[36]);
    }

    /// <summary>
    /// Panel models the raw-bulk firmware recognises. Anything else falls back
    /// to the 480x480 base screen rather than failing, matching the vendor tool.
    /// </summary>
    private static readonly FrozenSet<int> KnownBulkModels =
        new HashSet<int> { 5, 7, 9, 10, 11, 12, 13, 14, 15, 16, 17, 32, 50, 63, 64, 65, 66, 68, 69 }.ToFrozenSet();

    /// <summary>The screen code a raw-bulk panel's identity resolves to.</summary>
    public static int BulkScreenCodeFor(int panelModel, int subModel) =>
        KnownBulkModels.Contains(panelModel) || (panelModel == 1 && subModel is 48 or 49)
            ? ScreenCodeFor(panelModel, subModel)
            : 72;
}
