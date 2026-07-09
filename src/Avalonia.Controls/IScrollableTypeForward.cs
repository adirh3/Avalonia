using System.Runtime.CompilerServices;

// Binary-compatibility shim for the Avalonia 12 upgrade.
//
// In Avalonia 11 the interface Avalonia.Controls.Primitives.IScrollable lived in the
// Avalonia.Controls assembly. In Avalonia 12 it was moved to Avalonia.Base (the namespace
// was kept the same). Pre-built third-party binaries that were compiled against Avalonia 11
// (for example the vendored Avalonia.Controls.PanAndZoom build, whose ZoomBorder implements
// IScrollable, and AvaloniaEdit) still reference the interface as living in Avalonia.Controls.
//
// Without a type forward, loading any of those types throws:
//   System.TypeLoadException: Could not load type 'Avalonia.Controls.Primitives.IScrollable'
//   from assembly 'Avalonia.Controls'.
//
// Re-exporting the type from Avalonia.Controls keeps those binaries working without recompiling
// them.
[assembly: TypeForwardedTo(typeof(Avalonia.Controls.Primitives.IScrollable))]
