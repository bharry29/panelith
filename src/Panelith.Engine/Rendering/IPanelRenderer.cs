using Panelith.Expressions;
using Panelith.Imaging;

namespace Panelith.Engine.Rendering;

/// <summary>
/// Produces frames for one panel.
/// </summary>
/// <remarks>
/// The engine deliberately knows nothing about themes, widgets or graphics
/// libraries. That keeps the render loop - the part that has to be correct about
/// reconnection, pacing and shutdown - testable with a renderer that fills the
/// frame with a solid colour, and it leaves the graphics stack swappable.
/// </remarks>
public interface IPanelRenderer : IDisposable
{
    /// <summary>The size of frame this renderer produces.</summary>
    (int Width, int Height) Size { get; }

    /// <summary>
    /// Draws one frame into an existing buffer.
    /// </summary>
    /// <param name="target">A buffer of exactly <see cref="Size"/>.</param>
    /// <param name="context">Where bindings read their values from.</param>
    /// <remarks>
    /// Must not throw for content reasons. A theme that names a missing sensor,
    /// a missing font or a missing image is a normal situation on somebody
    /// else's machine, and the panel should keep updating.
    /// </remarks>
    void RenderInto(FrameBuffer target, IBindingContext context);
}

/// <summary>Builds renderers for panels.</summary>
public interface IPanelRendererFactory
{
    /// <summary>
    /// Creates a renderer for one panel.
    /// </summary>
    /// <param name="themeId">Which theme to draw.</param>
    /// <param name="width">Frame width.</param>
    /// <param name="height">Frame height.</param>
    /// <param name="variables">Theme variables overridden for this panel.</param>
    /// <exception cref="PanelRenderException">The theme could not be prepared.</exception>
    IPanelRenderer Create(
        string themeId,
        int width,
        int height,
        IReadOnlyDictionary<string, string>? variables = null);
}

/// <summary>A theme could not be prepared for rendering.</summary>
public sealed class PanelRenderException : Exception
{
    /// <summary>Creates the exception.</summary>
    public PanelRenderException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
