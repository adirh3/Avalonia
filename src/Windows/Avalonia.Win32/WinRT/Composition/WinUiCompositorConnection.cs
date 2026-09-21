using System;
using System.ComponentModel;
using System.Diagnostics;
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

internal class WinUiCompositorConnection : IRenderTimer, Win32.IWindowsSurfaceFactory, IDisposable
{
    private const uint ShutdownMessage = (uint)UnmanagedMethods.WindowsMessage.WM_APP + 1;
    private readonly IDispatcherQueueController _dispatcherQueueController;
    private readonly WinUiCompositionShared _shared;
    private readonly AutoResetEvent _wakeEvent = new(false);
    private readonly object _lifecycleLock = new();
    private volatile bool _stopped = true;
    private volatile bool _shutdownRequested;
    private volatile Action<TimeSpan>? _tick;
    private IntPtr _messageWindow;
    private bool _disposed;

    public bool RunsInBackground => true;

    public Action<TimeSpan>? Tick
    {
        get => _tick;
        set
        {
            lock (_lifecycleLock)
            {
                if (_disposed || _shutdownRequested)
                    return;

                _tick = value;
                _stopped = value == null;
                if (value != null)
                    _wakeEvent.Set();
            }
        }
    }

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
                connect.RunLoop(() =>
                {
                    AvaloniaLocator.CurrentMutable.Bind<IWindowsSurfaceFactory>().ToConstant(connect);
                    AvaloniaLocator.CurrentMutable.Bind<IRenderTimer>().ToConstant(connect);
                    AvaloniaLocator.CurrentMutable.Bind<IRenderLoop>().ToConstant(RenderLoop.FromTimer(connect));
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

    private sealed class RunLoopHandler : CallbackBase, IAsyncActionCompletedHandler
    {
        private readonly WinUiCompositorConnection _parent;
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
        private TimeSpan? _commitDueAt;
        private IAsyncAction? _currentCommit;
        private bool _commitCompleted;

        public RunLoopHandler(WinUiCompositorConnection parent)
        {
            _parent = parent;
        }

        public void Invoke(IAsyncAction? asyncInfo, AsyncStatus asyncStatus)
        {
            lock (_parent._shared.SyncRoot)
            {
                if (_parent._shutdownRequested || _currentCommit == null ||
                    _currentCommit.GetNativeIntPtr() != asyncInfo.GetNativeIntPtr())
                    return;

                OnCommitCompleted();
            }
        }

        private void OnCommitCompleted()
        {
            Debug.Assert(Monitor.IsEntered(_parent._shared.SyncRoot), "Lock should be held");

            _currentCommit?.Dispose();
            _currentCommit = null;
            _parent._tick?.Invoke(_stopwatch.Elapsed);
            // Commit the final frame even when rendering has just become idle.
            ScheduleNextCommit();
            _commitCompleted = true;
        }

        public void OnAfterMessageWithoutLock()
        {
            Debug.Assert(!Monitor.IsEntered(_parent._shared.SyncRoot), "Lock should NOT be held");

            if (!_commitCompleted)
                return;

            _commitCompleted = false;
            if (_parent._stopped && !_parent._shutdownRequested)
            {
                _parent._wakeEvent.WaitOne();
                _commitDueAt = _stopwatch.Elapsed + TimeSpan.FromSeconds(1);
            }
        }

        private void ScheduleNextCommit()
        {
            Debug.Assert(Monitor.IsEntered(_parent._shared.SyncRoot), "Lock should be held");
            if (_parent._shutdownRequested)
                return;

            _commitDueAt = _stopwatch.Elapsed + TimeSpan.FromSeconds(1);
            _currentCommit = _parent._shared.Compositor5.RequestCommitAsync();
            _currentCommit.SetCompleted(this);
        }

        public void WatchDog()
        {
            lock (_parent._shared.SyncRoot)
            {
                // Upstream's recovery for a commit callback lost after graphics-device loss.
                if (!_parent._shutdownRequested && _stopwatch.Elapsed > _commitDueAt &&
                    _currentCommit != null)
                {
                    Logger.TryGet(LogEventLevel.Error, LogArea.Visual)?.Log(this,
                        "windows::UI::Composition::ICompositor5.RequestCommitAsync timed out, force-triggering next tick");
                    try
                    {
                        _currentCommit.GetResults();
                    }
                    catch (Exception e)
                    {
                        Logger.TryGet(LogEventLevel.Error, LogArea.Visual)?.Log(this,
                            "ICompositor5::RequestCommitAsync failed: {HR}, {ERR}", e.HResult, e.ToString());
                    }

                    OnCommitCompleted();
                }
            }
        }

        public void Start()
        {
            lock (_parent._shared.SyncRoot)
                ScheduleNextCommit();
        }

        public void Stop()
        {
            lock (_parent._shared.SyncRoot)
            {
                _currentCommit?.Dispose();
                _currentCommit = null;
            }
        }
    }

    private void RunLoop(Action ready)
    {
        const uint watchDogIntervalInMs = 1000;
        using var handler = new RunLoopHandler(this);
        var watchDogTimer = IntPtr.Zero;
        using var dw = new SimpleWindow((hwnd, msg, w, l) =>
        {
            if (msg == (uint)UnmanagedMethods.WindowsMessage.WM_TIMER && w == watchDogTimer)
            {
                handler.WatchDog();
                UnmanagedMethods.SetTimer(hwnd, watchDogTimer, watchDogIntervalInMs, null);
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
        EventHandler processExitHandler = (_, _) => RequestShutdown();
        AppDomain.CurrentDomain.ProcessExit += processExitHandler;

        try
        {
            watchDogTimer = UnmanagedMethods.SetTimer(dw.Handle, IntPtr.Zero, watchDogIntervalInMs, null);
            if (watchDogTimer == IntPtr.Zero)
                throw new Win32Exception("Unable to create the WinUI composition watchdog timer.");

            handler.Start();
            ready();

            // Commit callbacks can run inside GetMessage on Windows 11. Never hold SyncRoot here.
            var result = 0;
            while (!_shutdownRequested &&
                   (result = UnmanagedMethods.GetMessage(out var msg, IntPtr.Zero, 0, 0)) > 0)
            {
                UnmanagedMethods.DispatchMessage(ref msg);
                handler.OnAfterMessageWithoutLock();
            }

            if (result < 0)
            {
                Logger.TryGet(LogEventLevel.Error, "WinUIComposition")
                    ?.Log(this, "Unmanaged error in {0}. Error Code: {1}", nameof(RunLoop), Marshal.GetLastWin32Error());
            }
        }
        finally
        {
            AppDomain.CurrentDomain.ProcessExit -= processExitHandler;
            _shutdownRequested = true;
            handler.Stop();
            if (watchDogTimer != IntPtr.Zero)
                UnmanagedMethods.KillTimer(dw.Handle, watchDogTimer);
            Volatile.Write(ref _messageWindow, IntPtr.Zero);
        }
    }

    private void RequestShutdown()
    {
        lock (_lifecycleLock)
        {
            if (_disposed || _shutdownRequested)
                return;

            _shutdownRequested = true;
            _wakeEvent.Set();
        }

        IntPtr window = Volatile.Read(ref _messageWindow);
        if (window != IntPtr.Zero &&
            !UnmanagedMethods.PostMessage(window, ShutdownMessage, IntPtr.Zero, IntPtr.Zero))
        {
            int error = Marshal.GetLastWin32Error();
            Logger.TryGet(LogEventLevel.Warning, "WinUIComposition")
                ?.Log(this, "Unable to signal WinUI composition shutdown. Error Code: {0}", error);
        }
    }

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int exitCode);

    public void Dispose()
    {
        lock (_lifecycleLock)
        {
            if (_disposed)
                return;

            _disposed = true;
            _shutdownRequested = true;
            _stopped = true;
            _tick = null;
            _wakeEvent.Set();
        }
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
                _wakeEvent.Dispose();
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
