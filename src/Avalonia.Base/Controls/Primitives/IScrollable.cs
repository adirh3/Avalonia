namespace Avalonia.Controls.Primitives
{
    /// <summary>
    /// Interface implemented by scrollable controls.
    /// </summary>
    public interface IScrollable
    {
        /// <summary>
        /// Gets the extent of the scrollable content, in logical units
        /// </summary>
        Size Extent { get; }

        /// <summary>
        /// Gets or sets the current scroll offset, in logical units.
        /// </summary>
        Vector Offset { get; set; }

        /// <summary>
        /// Gets the size of the viewport, in logical units.
        /// </summary>
        Size Viewport { get; }

        /// <summary>
        /// Gets a value indicating whether the content can be scrolled horizontally.
        /// </summary>
        /// <remarks>
        /// A default implementation is provided so that types compiled against earlier Avalonia
        /// versions (where this member only existed on ILogicalScrollable) remain loadable. Such
        /// controls expose their real value through the ILogicalScrollable slot, which is what the
        /// scrolling infrastructure consumes.
        /// </remarks>
        bool CanHorizontallyScroll => false;

        /// <summary>
        /// Gets a value indicating whether the content can be scrolled vertically.
        /// </summary>
        /// <remarks>
        /// A default implementation is provided so that types compiled against earlier Avalonia
        /// versions (where this member only existed on ILogicalScrollable) remain loadable. Such
        /// controls expose their real value through the ILogicalScrollable slot, which is what the
        /// scrolling infrastructure consumes.
        /// </remarks>
        bool CanVerticallyScroll => false;
    }
}
