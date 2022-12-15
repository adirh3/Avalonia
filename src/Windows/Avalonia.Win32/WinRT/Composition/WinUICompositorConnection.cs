using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Logging;
using Avalonia.OpenGL.Angle;
using Avalonia.OpenGL.Egl;
using Avalonia.Rendering;
using Avalonia.Win32.Interop;
using MicroCom.Runtime;

namespace Avalonia.Win32.WinRT.Composition
{
    class WinUICompositorConnection : IRenderTimer
    {
        public static readonly Version MinHostBackdropVersion = new Version(10, 0, 22000);
        private Action<TimeSpan>? _tick;
        private readonly EglContext _syncContext;
        private ICompositor _compositor;
        private ICompositor5 _compositor5;
        private ICompositorInterop _compositorInterop;
        private AngleWin32EglDisplay _angle;
        private ICompositionGraphicsDevice _device;
        private EglPlatformOpenGlInterface _gl;
        private ICompositorDesktopInterop _compositorDesktopInterop;
        private object _pumpLock = new object();
        private readonly float _backdropCornerRadius;
        private int _subscriberCount;
        private CancellationTokenSource _renderCts;
        private readonly ManualResetEvent _manualResetEvent = new(false);

        public WinUICompositorConnection(EglPlatformOpenGlInterface gl, object pumpLock, float backdropCornerRadius)
        {
            _gl = gl;
            _pumpLock = pumpLock;
            _backdropCornerRadius = backdropCornerRadius;
            _syncContext = _gl.PrimaryEglContext;
            _angle = (AngleWin32EglDisplay)_gl.Display;
            _compositor = NativeWinRTMethods.CreateInstance<ICompositor>("Windows.UI.Composition.Compositor");
            _compositor5 = _compositor.QueryInterface<ICompositor5>();
            _compositorInterop = _compositor.QueryInterface<ICompositorInterop>();
            _compositorDesktopInterop = _compositor.QueryInterface<ICompositorDesktopInterop>();
            using var device = MicroComRuntime.CreateProxyFor<IUnknown>(_angle.GetDirect3DDevice(), true);

            _device = _compositorInterop.CreateGraphicsDevice(device);
        }

        public EglPlatformOpenGlInterface Egl => _gl;

        static bool TryCreateAndRegisterCore(EglPlatformOpenGlInterface angle, float backdropCornerRadius)
        {
            var tcs = new TaskCompletionSource<bool>();
            var pumpLock = new object();
            var th = new Thread(() =>
            {
                WinUICompositorConnection connect;
                try
                {
                    NativeWinRTMethods.CreateDispatcherQueueController(new NativeWinRTMethods.DispatcherQueueOptions
                    {
                        apartmentType = NativeWinRTMethods.DISPATCHERQUEUE_THREAD_APARTMENTTYPE.DQTAT_COM_NONE,
                        dwSize = Marshal.SizeOf<NativeWinRTMethods.DispatcherQueueOptions>(),
                        threadType = NativeWinRTMethods.DISPATCHERQUEUE_THREAD_TYPE.DQTYPE_THREAD_CURRENT
                    });
                    connect = new WinUICompositorConnection(angle, pumpLock, backdropCornerRadius);
                    AvaloniaLocator.CurrentMutable.BindToSelf(connect);
                    AvaloniaLocator.CurrentMutable.Bind<IRenderTimer>().ToConstant(connect);
                    tcs.SetResult(true);
                }
                catch (Exception e)
                {
                    tcs.SetException(e);
                    return;
                }

                connect.RunLoop();
            }) { IsBackground = true };
            th.SetApartmentState(ApartmentState.STA);
            th.Start();
            return tcs.Task.Result;
        }

        class RunLoopHandler : IAsyncActionCompletedHandler, IMicroComShadowContainer
        {
            private readonly WinUICompositorConnection _parent;
            private Stopwatch _st = Stopwatch.StartNew();

            public RunLoopHandler(WinUICompositorConnection parent)
            {
                _parent = parent;
            }

            public void Dispose()
            {
            }

            public void Invoke(IAsyncAction asyncInfo, AsyncStatus asyncStatus)
            {
                _parent._tick?.Invoke(_st.Elapsed);
                using var act = _parent._compositor5.RequestCommitAsync();
                act.SetCompleted(this);
            }

            public MicroComShadow Shadow { get; set; }

            public void OnReferencedFromNative()
            {
            }

            public void OnUnreferencedFromNative()
            {
            }
        }

        private void RunLoop()
        {
            var cts = new CancellationTokenSource();
            AppDomain.CurrentDomain.ProcessExit += (sender, args) =>
                cts.Cancel();
            var asyncActionCompletedHandler = new RunLoopHandler(this);
            using (var act = _compositor5.RequestCommitAsync())
            {
                act.SetCompleted(asyncActionCompletedHandler);
            }

            while (!cts.IsCancellationRequested)
            {
                _manualResetEvent.WaitOne();
                _renderCts = new CancellationTokenSource();
                while (!_renderCts.IsCancellationRequested && !cts.IsCancellationRequested)
                {
                    UnmanagedMethods.GetMessage(out var msg, IntPtr.Zero, 0, 0);
                    lock (_pumpLock)
                        UnmanagedMethods.DispatchMessage(ref msg);
                }
            }
        }

        public static void TryCreateAndRegister(EglPlatformOpenGlInterface angle,
            float backdropCornerRadius)
        {
            const int majorRequired = 10;
            const int buildRequired = 17134;

            var majorInstalled = Win32Platform.WindowsVersion.Major;
            var buildInstalled = Win32Platform.WindowsVersion.Build;

            if (majorInstalled >= majorRequired &&
                buildInstalled >= buildRequired)
            {
                try
                {
                    TryCreateAndRegisterCore(angle, backdropCornerRadius);
                    return;
                }
                catch (Exception e)
                {
                    Logger.TryGet(LogEventLevel.Error, "WinUIComposition")
                        ?.Log(null, "Unable to initialize WinUI compositor: {0}", e);
                }
            }

            var osVersionNotice =
                $"Windows {majorRequired} Build {buildRequired} is required. Your machine has Windows {majorInstalled} Build {buildInstalled} installed.";

            Logger.TryGet(LogEventLevel.Warning, "WinUIComposition")?.Log(null,
                $"Unable to initialize WinUI compositor: {osVersionNotice}");
        }


        public WinUICompositedWindow CreateWindow(IntPtr hWnd)
        {
            using var sc = _syncContext.EnsureLocked();
            using var desktopTarget = _compositorDesktopInterop.CreateDesktopWindowTarget(hWnd, 0);
            using var target = desktopTarget.QueryInterface<ICompositionTarget>();
            using var device2 = _device.QueryInterface<ICompositionGraphicsDevice2>();

            using var drawingSurface = device2.CreateDrawingSurface2(new UnmanagedMethods.SIZE(), DirectXPixelFormat.B8G8R8A8UIntNormalized,
                DirectXAlphaMode.Premultiplied);
            using var surface = drawingSurface.QueryInterface<ICompositionSurface>();
            using var surfaceInterop = drawingSurface.QueryInterface<ICompositionDrawingSurfaceInterop>();

            using var surfaceBrush = _compositor.CreateSurfaceBrushWithSurface(surface);
            using var brush = surfaceBrush.QueryInterface<ICompositionBrush>();

            using var spriteVisual = _compositor.CreateSpriteVisual();
            spriteVisual.SetBrush(brush);

            using var visual = spriteVisual.QueryInterface<IVisual>();
            using var visual2 = spriteVisual.QueryInterface<IVisual2>();
            using var container = _compositor.CreateContainerVisual();
            using var containerVisual = container.QueryInterface<IVisual>();
            using var containerVisual2 = container.QueryInterface<IVisual2>();
            containerVisual2.SetRelativeSizeAdjustment(new Vector2(1, 1));
            using var containerChildren = container.Children;

            target.SetRoot(containerVisual);

            var compositionRoundedRectangleGeometry = GetRoundedRectangleGeometry();
            containerChildren.InsertAtTop(visual);

            return new WinUICompositedWindow(_syncContext, _compositor, _pumpLock, target, surfaceInterop, visual,
                compositionRoundedRectangleGeometry, containerChildren, _backdropCornerRadius);
        }

        private ICompositionRoundedRectangleGeometry GetRoundedRectangleGeometry()
        {
            if (_backdropCornerRadius == 0)
                return null;
            using var compositor5 = _compositor.QueryInterface<ICompositor5>();
            using var roundedRectangleGeometry = compositor5.CreateRoundedRectangleGeometry();
            roundedRectangleGeometry.SetCornerRadius(new Vector2(_backdropCornerRadius, _backdropCornerRadius));

            return roundedRectangleGeometry.CloneReference();
        }


        public event Action<TimeSpan> Tick
        {
            add
            {
                _tick += value;

                if (_subscriberCount++ == 0)
                {
                    Start();
                }
            }

            remove
            {
                if (--_subscriberCount == 0)
                {
                    Stop();
                }

                _tick -= value;
            }
        }
        
        private void Start()
        {
            _manualResetEvent.Set();
        }
        
        private void Stop()
        {
            _manualResetEvent.Reset();
            _renderCts.Cancel();
            _renderCts.Dispose();
        }

        public bool RunsInBackground => true;
    }
}
