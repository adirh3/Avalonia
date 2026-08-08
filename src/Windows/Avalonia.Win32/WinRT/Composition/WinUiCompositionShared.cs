using System;
using MicroCom.Runtime;

namespace Avalonia.Win32.WinRT.Composition;

internal class WinUiCompositionShared : IDisposable
{
    public ICompositor Compositor { get; }
    public ICompositorDesktopInterop DesktopInterop { get; }
    public object SyncRoot { get; } = new();

    public static readonly Version MinWinCompositionVersion = new(10, 0, 17134);
    public static readonly Version MinAcrylicVersion = new(10, 0, 15063);
    public static readonly Version MinHostBackdropVersion = new(10, 0, 22000);
    
    public WinUiCompositionShared(ICompositor compositor)
    {
        Compositor = compositor.CloneReference();
        DesktopInterop = compositor.QueryInterface<ICompositorDesktopInterop>();
    }
    
    public void Dispose()
    {
        DesktopInterop.Dispose();
        Compositor.Dispose();
    }
}
