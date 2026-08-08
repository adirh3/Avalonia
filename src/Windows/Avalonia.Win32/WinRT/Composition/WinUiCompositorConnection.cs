using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Platform.Surfaces;
using Avalonia.Logging;
using Avalonia.MicroCom;
using Avalonia.OpenGL.Egl;
using Avalonia.Rendering;
using Avalonia.Win32.Interop;
using MicroCom.Runtime;

namespace Avalonia.Win32.WinRT.Composition;

internal class WinUiCompositorConnection : Win32.IWindowsSurfaceFactory, IDisposable
{
    private const uint ShutdownMessage = (uint)UnmanagedMethods.WindowsMessage.WM_APP + 1;
    private const uint RenderTickMessage = (uint)UnmanagedMethods.WindowsMessage.WM_APP + 2;
    private readonly IDispatcherQueueController _dispatcherQueueController;
    private readonly WinUiCompositionShared _shared;
    private Action? _pendingRenderTick;
    private IntPtr _messageWindow;
    private int _renderTickMessagePosted;
    private bool _disposed;

    private WinUiCompositorConnection(IDispatcherQueueController dispatcherQueueController)
    {
        _dispatcherQueueController = dispatcherQueueController;
        using var compositor = NativeWinRTMethods.CreateInstance<ICompositor>("Windows.UI.Composition.Compositor");
        /*
        var levels = new[] { D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_11_1 };
        DirectXUnmanagedMethods.D3D11CreateDevice(IntPtr.Zero, D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_HARDWARE,
            IntPtr.Zero, 0, levels, (uint)levels.Length, 7, out var pD3dDevice, out var level, null);

        var d3dDevice = MicroComRuntime.CreateProxyFor<IUnknown>(pD3dDevice, true);

        var compositionDevice = compositor.QueryInterface<ICompositorInterop>().CreateGraphicsDevice(d3dDevice);
        var surf = compositionDevice.CreateDrawingSurface(new UnmanagedMethods.SIZE_F { X = 100, Y = 100 },
            DirectXPixelFormat.B8G8R8A8UIntNormalized, DirectXAlphaMode.Premultiplied);
        var surfInterop = surf.QueryInterface<ICompositionDrawingSurfaceInterop>();
        var IID_ID3D11Texture2D = Guid.Parse("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
        void* texture = null;
        surfInterop.BeginDraw(null, &IID_ID3D11Texture2D, &texture);
        */

        _shared = new WinUiCompositionShared(compositor);
    }

    private static bool TryCreateAndRegisterCore()
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var th = new Thread(() =>
        {
            WinUiCompositorConnection? connect = null;
            WinUiRenderTimer? renderTimer = null;
            IDispatcherQueueController? dispatcherQueueController = null;
            try
            {
                var controllerPointer = NativeWinRTMethods.CreateDispatcherQueueController(
                    new NativeWinRTMethods.DispatcherQueueOptions
                    {
                        apartmentType = NativeWinRTMethods.DISPATCHERQUEUE_THREAD_APARTMENTTYPE.DQTAT_COM_NONE,
                        dwSize = Marshal.SizeOf<NativeWinRTMethods.DispatcherQueueOptions>(),
                        threadType = NativeWinRTMethods.DISPATCHERQUEUE_THREAD_TYPE.DQTYPE_THREAD_CURRENT
                    });
                dispatcherQueueController =
                    MicroComRuntime.CreateProxyFor<IDispatcherQueueController>(controllerPointer, true);
                connect = new WinUiCompositorConnection(dispatcherQueueController);
                dispatcherQueueController = null;
                renderTimer = new WinUiRenderTimer(60, connect.QueueRenderTick);

                connect.RunLoop(() =>
                {
                    AvaloniaLocator.CurrentMutable.Bind<IWindowsSurfaceFactory>().ToConstant(connect);
                    AvaloniaLocator.CurrentMutable.Bind<IRenderTimer>().ToConstant(renderTimer);
                    AvaloniaLocator.CurrentMutable.Bind<IRenderLoop>().ToConstant(RenderLoop.FromTimer(renderTimer));
                    tcs.SetResult(true);
                });
            }
            catch (Exception e)
            {
                if (!tcs.TrySetException(e))
                {
                    Logger.TryGet(LogEventLevel.Error, "WinUIComposition")
                        ?.Log(null, "WinUI composition loop failed: {0}", e);
                }
            }
            finally
            {
                renderTimer?.Dispose();
                connect?.Dispose();
                if (dispatcherQueueController != null)
                {
                    try
                    {
                        ShutdownDispatcherQueue(dispatcherQueueController);
                    }
                    catch (Exception e)
                    {
                        Logger.TryGet(LogEventLevel.Error, "WinUIComposition")
                            ?.Log(null, "Unable to shut down the unowned WinUI DispatcherQueue cleanly: {0}", e);
                    }
                    finally
                    {
                        dispatcherQueueController.Dispose();
                    }
                }
            }
        })
        {
            IsBackground = true,
            Name = "WinUICompositionLoop"
        };
        th.SetApartmentState(ApartmentState.STA);
        th.Start();
        return tcs.Task.Result;
    }

    private sealed class DispatcherQueueShutdownHandler : CallbackBase, IAsyncActionCompletedHandler
    {
        private readonly EventWaitHandle _completed;

        public DispatcherQueueShutdownHandler(EventWaitHandle completed)
        {
            _completed = completed;
        }

        public void Invoke(IAsyncAction? asyncInfo, AsyncStatus asyncStatus) => _completed.Set();
    }

    private void RunLoop(Action ready)
    {
        using var dw = new SimpleWindow((hwnd, msg, w, l) =>
        {
            if (msg == RenderTickMessage)
            {
                DispatchRenderTick();
                return IntPtr.Zero;
            }

            if (msg == ShutdownMessage)
            {
                PostQuitMessage(0);
                return IntPtr.Zero;
            }

            return UnmanagedMethods.DefWindowProc(hwnd, msg, w, l);
        });
        Volatile.Write(ref _messageWindow, dw.Handle);
        EventHandler processExitHandler = (_, _) =>
            UnmanagedMethods.PostMessage(dw.Handle, ShutdownMessage, IntPtr.Zero, IntPtr.Zero);
        AppDomain.CurrentDomain.ProcessExit += processExitHandler;

        try
        {
            ready();

            var result = 0;
            while ((result = UnmanagedMethods.GetMessage(out var msg, IntPtr.Zero, 0, 0)) > 0)
                UnmanagedMethods.DispatchMessage(ref msg);

            if (result < 0)
            {
                Logger.TryGet(LogEventLevel.Error, "WinUIComposition")
                    ?.Log(this, "Unmanaged error in {0}. Error Code: {1}", nameof(RunLoop), Marshal.GetLastWin32Error());
            }
        }
        finally
        {
            AppDomain.CurrentDomain.ProcessExit -= processExitHandler;
            Volatile.Write(ref _messageWindow, IntPtr.Zero);
            Interlocked.Exchange(ref _pendingRenderTick, null);
            Interlocked.Exchange(ref _renderTickMessagePosted, 0);
        }
    }

    private void QueueRenderTick(Action renderTick)
    {
        Volatile.Write(ref _pendingRenderTick, renderTick);
        PostRenderTickMessage();
    }

    private void PostRenderTickMessage()
    {
        IntPtr window = Volatile.Read(ref _messageWindow);
        if (window == IntPtr.Zero ||
            Interlocked.CompareExchange(ref _renderTickMessagePosted, 1, 0) != 0)
        {
            return;
        }

        if (!UnmanagedMethods.PostMessage(window, RenderTickMessage, IntPtr.Zero, IntPtr.Zero))
            Interlocked.Exchange(ref _renderTickMessagePosted, 0);
    }

    private void DispatchRenderTick()
    {
        Action? renderTick = Interlocked.Exchange(ref _pendingRenderTick, null);
        renderTick?.Invoke();

        Interlocked.Exchange(ref _renderTickMessagePosted, 0);
        if (Volatile.Read(ref _pendingRenderTick) != null)
            PostRenderTickMessage();
    }

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int exitCode);

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        try
        {
            _shared.Dispose();
        }
        finally
        {
            try
            {
                ShutdownDispatcherQueue(_dispatcherQueueController);
            }
            catch (Exception e)
            {
                Logger.TryGet(LogEventLevel.Error, "WinUIComposition")
                    ?.Log(this, "Unable to shut down the WinUI DispatcherQueue cleanly: {0}", e);
            }
            finally
            {
                _dispatcherQueueController.Dispose();
            }
        }
    }

    private static void ShutdownDispatcherQueue(IDispatcherQueueController dispatcherQueueController)
    {
        const uint removeMessage = 0x0001;
        using var completed = new ManualResetEvent(false);
        using var completionHandler = new DispatcherQueueShutdownHandler(completed);
        using var operation = dispatcherQueueController.ShutdownQueueAsync();
        operation.SetCompleted(completionHandler);

        var completedSafeHandle = completed.SafeWaitHandle;
        bool completedHandleRef = false;
        try
        {
            completedSafeHandle.DangerousAddRef(ref completedHandleRef);
            IntPtr[] waitHandles = [completedSafeHandle.DangerousGetHandle()];
            while (!completed.WaitOne(0))
            {
                int waitResult = UnmanagedMethods.MsgWaitForMultipleObjectsEx(
                    waitHandles.Length,
                    waitHandles,
                    Timeout.Infinite,
                    UnmanagedMethods.QueueStatusFlags.QS_ALLINPUT,
                    UnmanagedMethods.MsgWaitForMultipleObjectsFlags.MWMO_INPUTAVAILABLE);
                if (waitResult == 0)
                    break;

                while (UnmanagedMethods.PeekMessage(
                           out var message,
                           IntPtr.Zero,
                           0,
                           0,
                           removeMessage))
                {
                    UnmanagedMethods.DispatchMessage(ref message);
                }
            }

            operation.GetResults();
        }
        finally
        {
            if (completedHandleRef)
                completedSafeHandle.DangerousRelease();
        }
    }

    public static bool IsSupported()
    {
        return Win32Platform.WindowsVersion >= WinUiCompositionShared.MinWinCompositionVersion;
    }

    public static bool TryCreateAndRegister()
    {
        if (IsSupported())
        {
            try
            {
                TryCreateAndRegisterCore();
                return true;
            }
            catch (Exception e)
            {
                Logger.TryGet(LogEventLevel.Error, "WinUIComposition")
                    ?.Log(null, "Unable to initialize WinUI compositor: {0}", e);
            }
        }
        else
        {
            var osVersionNotice =
                $"Windows {WinUiCompositionShared.MinWinCompositionVersion} is required. Your machine has Windows {Win32Platform.WindowsVersion} installed.";

            Logger.TryGet(LogEventLevel.Warning, "WinUIComposition")?.Log(null,
                $"Unable to initialize WinUI compositor: {osVersionNotice}");
        }

        return false;
    }

    public bool RequiresNoRedirectionBitmap => true;

    public IPlatformRenderSurface CreateSurface(EglGlPlatformSurface.IEglWindowGlPlatformSurfaceInfo info)
    {
        if (info is not IWinUiCompositionWindowInfo compositionInfo)
            throw new InvalidOperationException($"{nameof(WinUiCompositedWindowSurface)} requires {nameof(IWinUiCompositionWindowInfo)}.");

        return new WinUiCompositedWindowSurface(_shared, info, compositionInfo);
    }
}
