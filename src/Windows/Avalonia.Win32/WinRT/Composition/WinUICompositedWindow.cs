using System;
using System.Numerics;
using System.Reactive.Disposables;
using System.Threading;
using Avalonia.Controls;
using Avalonia.OpenGL.Egl;
using Avalonia.Win32.Interop;
using MicroCom.Runtime;

namespace Avalonia.Win32.WinRT.Composition
{
    public class WinUICompositedWindow : IDisposable
    {
        private EglContext _syncContext;
        private readonly object _pumpLock;
        private readonly ICompositionRoundedRectangleGeometry _roundedRectangleGeometry;
        private readonly IVisualCollection _containerChildren;
        private readonly float _backdropCornerRadius;
        private ICompositionTarget _compositionTarget;
        private IVisual _contentVisual;
        private ICompositionDrawingSurfaceInterop _surfaceInterop;
        private PixelSize _size;

        private static Guid IID_ID3D11Texture2D = Guid.Parse("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
        private ICompositor _compositor;
        private Vector3 _scale = Vector3.One;
        private Vector3 _centerPoint = Vector3.Zero;
        private float _opacity = 1f;
        private Vector3 _offset;
        private IVisual _currentVisual;
        private BlurEffect _currentBlurEffect;

        internal WinUICompositedWindow(EglContext syncContext,
            ICompositor compositor,
            object pumpLock,
            ICompositionTarget compositionTarget,
            ICompositionDrawingSurfaceInterop surfaceInterop,
            IVisual contentVisual,
            ICompositionRoundedRectangleGeometry roundedRectangleGeometry, IVisualCollection containerChildren,
            float backdropCornerRadius)
        {
            _compositor = compositor.CloneReference();
            _syncContext = syncContext;
            _pumpLock = pumpLock;
            _roundedRectangleGeometry = roundedRectangleGeometry;
            _containerChildren = containerChildren.CloneReference();
            _backdropCornerRadius = backdropCornerRadius;
            _compositionTarget = compositionTarget.CloneReference();
            _contentVisual = contentVisual.CloneReference();
            _surfaceInterop = surfaceInterop.CloneReference();
        }


        public void ResizeIfNeeded(PixelSize size, double infoScaling, WindowState infoWindowState,
            float infoCompositionPadding, Vector3 scaleTransform, Vector3 centerPoint, float opacity,
            Vector3 infoOffset)
        {
            using (_syncContext.EnsureLocked())
            {
                centerPoint *= new Vector3((float)infoScaling);
                // ReSharper disable once CompareOfFloatsByEqualityOperator
                if (_size != size || _scale != scaleTransform || _centerPoint != centerPoint || _opacity != opacity ||
                    infoOffset != _offset)
                {
                    _surfaceInterop?.Resize(new UnmanagedMethods.POINT { X = size.Width, Y = size.Height });
                    _contentVisual?.SetSize(new Vector2(size.Width, size.Height));

                    float backdropPadding = infoWindowState == WindowState.Maximized ? 0 : infoCompositionPadding;
                    var offset = (float)Math.Ceiling(backdropPadding * infoScaling);
                    var sizeReduction = 2 * offset;

                    float sizeWidth = size.Width - sizeReduction;
                    float sizeHeight = size.Height - sizeReduction;
                    if (sizeHeight > 0 && sizeWidth > 0)
                    {
                        _roundedRectangleGeometry?.SetSize(new Vector2(sizeWidth, sizeHeight));
                        _roundedRectangleGeometry?.SetOffset(new Vector2(offset, offset));
                    }
                    else
                    {
                        _roundedRectangleGeometry?.SetSize(new Vector2(size.Width, size.Height));
                        _roundedRectangleGeometry?.SetOffset(new Vector2(0, 0));
                    }

                    _currentVisual?.SetOffset(infoOffset);
                    _currentVisual?.SetScale(scaleTransform);
                    _currentVisual?.SetCenterPoint(centerPoint);
                    _currentVisual?.SetOpacity(opacity);

                    _roundedRectangleGeometry?.SetCornerRadius(infoWindowState == WindowState.Maximized ?
                        Vector2.Zero :
                        new Vector2((float)Math.Ceiling(_backdropCornerRadius * infoScaling)));
                    _size = size;
                    _scale = scaleTransform;
                    _centerPoint = centerPoint;
                    _opacity = opacity;
                    _offset = infoOffset;
                }
            }
        }

        public unsafe IUnknown BeginDrawToTexture(out PixelPoint offset)
        {
            if (!_syncContext.IsCurrent)
                throw new InvalidOperationException();

            var iid = IID_ID3D11Texture2D;
            void* pTexture;
            var off = _surfaceInterop.BeginDraw(null, &iid, &pTexture);
            offset = new PixelPoint(off.X, off.Y);
            return MicroComRuntime.CreateProxyFor<IUnknown>(pTexture, true);
        }

        public void EndDraw()
        {
            if (!_syncContext.IsCurrent)
                throw new InvalidOperationException();
            _surfaceInterop.EndDraw();
        }

        public void SetBlur(BlurEffect blurEffect)
        {
            if (_currentBlurEffect == blurEffect)
                return;
            using (_syncContext.EnsureLocked())
            {
                _currentBlurEffect = blurEffect;
                if (_currentVisual != null)
                {
                    _containerChildren.Remove(_currentVisual);
                    _currentVisual.Dispose();
                }

                _currentVisual = blurEffect switch
                {
                    BlurEffect.None => null,
                    BlurEffect.Acrylic => CreateAcrylicVisual(),
                    BlurEffect.MicaDark => CreateMicaDarkVisual(),
                    BlurEffect.MicaLight => CreateMicaLightVisual(),
                    _ => throw new ArgumentOutOfRangeException(nameof(blurEffect), blurEffect, null)
                };

                if (_currentVisual != null)
                {
                    if (_roundedRectangleGeometry != null)
                    {
                        using var compositionGeometry =
                            _roundedRectangleGeometry.QueryInterface<ICompositionGeometry>();
                        using var compositor6 = _compositor.QueryInterface<ICompositor6>();
                        using var geometricClipWithGeometry =
                            compositor6.CreateGeometricClipWithGeometry(compositionGeometry);
                        _currentVisual.SetClip(geometricClipWithGeometry.QueryInterface<ICompositionClip>());
                    }

                    _currentVisual.SetIsVisible(1);
                    _containerChildren.InsertAtBottom(_currentVisual);
                }
            }
        }

        public IDisposable BeginTransaction()
        {
            Monitor.Enter(_pumpLock);
            return Disposable.Create(() => Monitor.Exit(_pumpLock));
        }

        public void Dispose()
        {
            if (_syncContext == null)
            {
                _compositor.Dispose();
                _currentVisual?.Dispose();
                _containerChildren?.Dispose();
                _contentVisual.Dispose();
                _surfaceInterop.Dispose();
                _compositionTarget.Dispose();
            }
        }

        private IVisual CreateMicaLightVisual()
        {
            IVisual micaLight = null;
            var micaBrushLight = CreateMicaBackdropBrush(242, 0.6f);
            if (micaBrushLight != null)
            {
                micaLight = CreateBlurVisual(micaBrushLight);
            }

            return micaLight;
        }

        private IVisual CreateAcrylicVisual()
        {
            var acrylicBlurBackdropBrush = CreateAcrylicBlurBackdropBrush();
            if (acrylicBlurBackdropBrush != null)
            {
                return CreateBlurVisual(CreateAcrylicBlurBackdropBrush());
            }

            return null;
        }

        private IVisual CreateMicaDarkVisual()
        {
            IVisual micaDark = null;
            var micaBrushDark = CreateMicaBackdropBrush(32, 0.8f);

            if (micaBrushDark != null)
            {
                micaDark = CreateBlurVisual(micaBrushDark);
            }

            return micaDark;
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


        private ICompositionBrush CreateAcrylicBlurBackdropBrush()
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


        private IVisual CreateBlurVisual(ICompositionBrush compositionBrush)
        {
            using var spriteVisual = _compositor.CreateSpriteVisual();
            using var visual = spriteVisual.QueryInterface<IVisual>();
            using var visual2 = spriteVisual.QueryInterface<IVisual2>();


            spriteVisual.SetBrush(compositionBrush);
            visual2.SetRelativeSizeAdjustment(new Vector2(1.0f, 1.0f));

            return visual.CloneReference();
        }

        private ICompositionBrush CreateBackdropBrush()
        {
            ICompositionBackdropBrush brush = null;
            try
            {
                if (Win32Platform.WindowsVersion >= WinUICompositorConnection.MinHostBackdropVersion)
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
    }
}
