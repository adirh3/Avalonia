using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Logging;
using Avalonia.Media;
using Avalonia.MicroCom;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Angle;
using Avalonia.OpenGL.Egl;
using Avalonia.Rendering;
using Avalonia.Win32.Interop;

namespace Avalonia.Win32.WinRT.Composition
{
    class WinUICompositorConnection : IRenderTimer
    {
        public static readonly Version MinHostBackdropVersion = new Version(10, 0, 22000);
        private readonly EglContext _syncContext;
        private ICompositor _compositor;
        private ICompositor2 _compositor2;
        private ICompositor5 _compositor5;
        private ICompositorInterop _compositorInterop;
        private AngleWin32EglDisplay _angle;
        private ICompositionGraphicsDevice _device;
        private EglPlatformOpenGlInterface _gl;
        private ICompositorDesktopInterop _compositorDesktopInterop;
        private ICompositionBrush _blurBrush;
        private object _pumpLock = new object();
        private readonly float _backdropCornerRadius;
        private readonly ICompositionBrush _micaBrushDark;
        private readonly ICompositionBrush _micaBrushLight;

        public WinUICompositorConnection(EglPlatformOpenGlInterface gl, object pumpLock, float backdropCornerRadius)
        {
            _gl = gl;
            _pumpLock = pumpLock;
            _backdropCornerRadius = backdropCornerRadius;
            _syncContext = _gl.PrimaryEglContext;
            _angle = (AngleWin32EglDisplay)_gl.Display;
            _compositor = NativeWinRTMethods.CreateInstance<ICompositor>("Windows.UI.Composition.Compositor");
            _compositor2 = _compositor.QueryInterface<ICompositor2>();
            _compositor5 = _compositor.QueryInterface<ICompositor5>();
            _compositorInterop = _compositor.QueryInterface<ICompositorInterop>();
            _compositorDesktopInterop = _compositor.QueryInterface<ICompositorDesktopInterop>();
            using var device = MicroComRuntime.CreateProxyFor<IUnknown>(_angle.GetDirect3DDevice(), true);

            _device = _compositorInterop.CreateGraphicsDevice(device);
            _blurBrush = CreateAcrylicBlurBackdropBrush();
            try
            {
                _micaBrushDark = CreateMicaBackdropBrush(32, 0.8f);
                _micaBrushLight = CreateMicaBackdropBrush(242, 0.6f);
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
            }
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
                _parent.Tick?.Invoke(_st.Elapsed);
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
            {
                var st = Stopwatch.StartNew();
                using (var act = _compositor5.RequestCommitAsync())
                    act.SetCompleted(new RunLoopHandler(this));
                while (true)
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

            using var drawingSurface = _device.CreateDrawingSurface(new UnmanagedMethods.SIZE(),
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
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

            using var blur = CreateBlurVisual(_blurBrush);
            IVisual micaDark = null;
            if (_micaBrushDark != null)
            {
                micaDark = CreateBlurVisual(_micaBrushDark);
                containerChildren.InsertAtTop(micaDark);
            }

            IVisual micaLight = null;
            if (_micaBrushLight != null)
            {
                micaLight = CreateBlurVisual(_micaBrushLight);
                containerChildren.InsertAtTop(micaLight);
            }

            var compositionRoundedRectangleGeometry = ClipVisual(blur, micaDark, micaLight);

            containerChildren.InsertAtTop(blur);
            containerChildren.InsertAtTop(visual);

            return new WinUICompositedWindow(_syncContext, _compositor, _pumpLock, target, surfaceInterop, visual,
                blur, micaDark, micaLight, compositionRoundedRectangleGeometry, _backdropCornerRadius);
        }

        private ICompositionBrush CreateMicaBackdropBrush(float color, float opacity)
        {
            if (Win32Platform.WindowsVersion.Build < 22000)
                return null;

            using var backDropParameterFactory =
                NativeWinRTMethods.CreateActivationFactory<ICompositionEffectSourceParameterFactory>(
                    "Windows.UI.Composition.CompositionEffectSourceParameter");


            var tint = new[] { color / 255f, color / 255f, color / 255f, 255f / 255f };

            using var tintColorEffect = new ColorSourceEffect(tint);


            using var tintOpacityEffect = new OpacityEffect(1.0f, tintColorEffect);
            using var tintOpacityEffectFactory = _compositor.CreateEffectFactory(tintOpacityEffect);
            using var tintOpacityEffectBrushEffect = tintOpacityEffectFactory.CreateBrush();
            using var tintOpacityEffectBrush = tintOpacityEffectBrushEffect.QueryInterface<ICompositionBrush>();

            using var luminosityColorEffect = new ColorSourceEffect(tint);

            using var luminosityOpacityEffect = new OpacityEffect(opacity, luminosityColorEffect);
            using var luminosityOpacityEffectFactory = _compositor.CreateEffectFactory(luminosityOpacityEffect);
            using var luminosityOpacityEffectBrushEffect = luminosityOpacityEffectFactory.CreateBrush();
            using var luminosityOpacityEffectBrush =
                luminosityOpacityEffectBrushEffect.QueryInterface<ICompositionBrush>();


            // using var backDropParameterAsSource = GetParameterSource("BlurredWallpaperBackdrop", backDropParameterFactory, out var backdropHandle);
            // using var backdropCompositionBrsuh = backDropParameterAsSource.QueryInterface<ICompositionBrush>();
            using var compositorWithBlurredWallpaperBackdropBrush =
                _compositor.QueryInterface<ICompositorWithBlurredWallpaperBackdropBrush>();
            using var blurredWallpaperBackdropBrush =
                compositorWithBlurredWallpaperBackdropBrush?.TryCreateBlurredWallpaperBackdropBrush();
            using var micaBackdropBrush = blurredWallpaperBackdropBrush?.QueryInterface<ICompositionBrush>();


            using var backgroundParameterAsSource =
                GetParameterSource("Background", backDropParameterFactory, out var backgroundHandle);
            using var foregroundParameterAsSource =
                GetParameterSource("Foreground", backDropParameterFactory, out var foregroundHandle);

            using var luminosityBlendEffect =
                new BlendEffect(23, backgroundParameterAsSource, foregroundParameterAsSource);
            using var luminosityBlendEffectFactory = _compositor.CreateEffectFactory(luminosityBlendEffect);
            using var luminosityBlendEffectBrush = luminosityBlendEffectFactory.CreateBrush();
            using var luminosityBlendEffectBrush1 = luminosityBlendEffectBrush.QueryInterface<ICompositionBrush>();
            luminosityBlendEffectBrush.SetSourceParameter(backgroundHandle, micaBackdropBrush);
            luminosityBlendEffectBrush.SetSourceParameter(foregroundHandle, luminosityOpacityEffectBrush);


            using var backgroundParameterAsSource1 =
                GetParameterSource("Background", backDropParameterFactory, out var backgroundHandle1);
            using var foregroundParameterAsSource1 =
                GetParameterSource("Foreground", backDropParameterFactory, out var foregroundHandle1);

            using var colorBlendEffect =
                new BlendEffect(22, backgroundParameterAsSource1, foregroundParameterAsSource1);
            using var colorBlendEffectFactory = _compositor.CreateEffectFactory(colorBlendEffect);
            using var colorBlendEffectBrush = colorBlendEffectFactory.CreateBrush();
            colorBlendEffectBrush.SetSourceParameter(backgroundHandle1, luminosityBlendEffectBrush1);
            colorBlendEffectBrush.SetSourceParameter(foregroundHandle1, tintOpacityEffectBrush);


            // colorBlendEffectBrush.SetSourceParameter(backgroundHandle, micaBackdropBrush);

            using var micaBackdropBrush1 = colorBlendEffectBrush.QueryInterface<ICompositionBrush>();
            return micaBackdropBrush1.CloneReference();
        }

        private static IGraphicsEffectSource GetParameterSource(string name,
            ICompositionEffectSourceParameterFactory backDropParameterFactory, out IntPtr handle)
        {
            var backdropString = new HStringInterop(name);
            var backDropParameter =
                backDropParameterFactory.Create(backdropString.Handle);
            var backDropParameterAsSource = backDropParameter.QueryInterface<IGraphicsEffectSource>();
            handle = backdropString.Handle;
            return backDropParameterAsSource;
        }

        private unsafe ICompositionBrush CreateAcrylicBlurBackdropBrush()
        {
            using var backDropParameterFactory =
                NativeWinRTMethods.CreateActivationFactory<ICompositionEffectSourceParameterFactory>(
                    "Windows.UI.Composition.CompositionEffectSourceParameter");
            using var backdropString = new HStringInterop("backdrop");
            using var backDropParameter =
                backDropParameterFactory.Create(backdropString.Handle);
            using var backDropParameterAsSource = backDropParameter.QueryInterface<IGraphicsEffectSource>();
            var blurEffect = new WinUIGaussianBlurEffect(backDropParameterAsSource);
            using var blurEffectFactory = _compositor.CreateEffectFactory(blurEffect);
            using var compositionEffectBrush = blurEffectFactory.CreateBrush();
            using var backdrop = CreateBackdropBrush();
            using var backdropBrush = backdrop.QueryInterface<ICompositionBrush>();

            var saturateEffect = new SaturationEffect(blurEffect);
            using var satEffectFactory = _compositor.CreateEffectFactory(saturateEffect);
            using var sat = satEffectFactory.CreateBrush();
            compositionEffectBrush.SetSourceParameter(backdropString.Handle, backdropBrush);
            return compositionEffectBrush.QueryInterface<ICompositionBrush>();
        }

        private ICompositionRoundedRectangleGeometry ClipVisual(params IVisual[] containerVisuals)
        {
            if (_backdropCornerRadius == 0)
                return null;
            using var roundedRectangleGeometry = _compositor5.CreateRoundedRectangleGeometry();
            roundedRectangleGeometry.SetCornerRadius(new Vector2(_backdropCornerRadius, _backdropCornerRadius));

            using var compositor6 = _compositor.QueryInterface<ICompositor6>();
            using var compositionGeometry = roundedRectangleGeometry
                .QueryInterface<ICompositionGeometry>();

            using var geometricClipWithGeometry =
                compositor6.CreateGeometricClipWithGeometry(compositionGeometry);
            foreach (var visual in containerVisuals)
            {
                visual?.SetClip(geometricClipWithGeometry.QueryInterface<ICompositionClip>());
            }

            return roundedRectangleGeometry.CloneReference();
        }

        private unsafe IVisual CreateBlurVisual(ICompositionBrush compositionBrush)
        {
            using var spriteVisual = _compositor.CreateSpriteVisual();
            using var visual = spriteVisual.QueryInterface<IVisual>();
            using var visual2 = spriteVisual.QueryInterface<IVisual2>();


            spriteVisual.SetBrush(compositionBrush);
            visual.SetIsVisible(0);
            visual2.SetRelativeSizeAdjustment(new Vector2(1.0f, 1.0f));

            return visual.CloneReference();
        }
        
        private ICompositionBrush CreateBackdropBrush()
        {
            ICompositionBackdropBrush brush = null;
            try
            {
                if (Win32Platform.WindowsVersion >= MinHostBackdropVersion)
                {
                    using var compositor3 = _compositor.QueryInterface<ICompositor3>();
                    brush = compositor3.CreateHostBackdropBrush();
                }
                else
                {
                    using var compositor2 = _compositor.QueryInterface<ICompositor2>();
                    brush = compositor2.CreateBackdropBrush();
                }

                return brush.QueryInterface<ICompositionBrush>();
            }
            finally
            {
                brush?.Dispose();
            }
        }


        public event Action<TimeSpan> Tick;
        public bool RunsInBackground => true;
    }
}
