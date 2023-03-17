using System;
using System.Numerics;
using System.Threading;
using Avalonia.Controls;
using Avalonia.OpenGL.Egl;
using Avalonia.Reactive;
using Avalonia.Win32.Interop;
using MicroCom.Runtime;

namespace Avalonia.Win32.WinRT.Composition;

internal class WinUiCompositedWindow : IDisposable
{
    public EglGlPlatformSurface.IEglWindowGlPlatformSurfaceInfo WindowInfo { get; }
    private readonly WinUiCompositionShared _shared;
    private readonly float? _backdropCornerRadius;
    private readonly ICompositionRoundedRectangleGeometry? _compositionRoundedRectangleGeometry;
    private readonly IVisualCollection _containerChildren;
    private readonly IVisual _visual;
    private IVisual? _currentVisual;
    private Vector3 _scale = Vector3.One;
    private Vector3 _centerPoint = Vector3.Zero;
    private float _opacity = 1f;
    private Vector3 _offset;
    private PixelSize _size;
    private readonly ICompositionSurfaceBrush _surfaceBrush;
    private readonly ICompositionTarget _target;
    private BlurEffect _currentBlurEffect;
    private bool _disposed;

    public void Dispose()
    {
        lock (_shared.SyncRoot)
        {
            _disposed = true;
            _compositionRoundedRectangleGeometry?.Dispose();
            _currentVisual?.Dispose();
            _containerChildren.Dispose();
            _visual.Dispose();
            _surfaceBrush.Dispose();
            _target.Dispose();
        }
    }

    public WinUiCompositedWindow(EglGlPlatformSurface.IEglWindowGlPlatformSurfaceInfo info,
        WinUiCompositionShared shared, float? backdropCornerRadius)
    {
        WindowInfo = info;
        _shared = shared;
        _backdropCornerRadius = backdropCornerRadius;
        using var desktopTarget = shared.DesktopInterop.CreateDesktopWindowTarget(WindowInfo.Handle, 0);
        _target = desktopTarget.QueryInterface<ICompositionTarget>();


        using var container = shared.Compositor.CreateContainerVisual();
        using var containerVisual = container.QueryInterface<IVisual>();
        using var containerVisual2 = container.QueryInterface<IVisual2>();
        containerVisual2.SetRelativeSizeAdjustment(new Vector2(1, 1));
        _containerChildren = container.Children;

        _target.SetRoot(containerVisual);

        _compositionRoundedRectangleGeometry =
            WinUiCompositionUtils.GetRoundedRectangle(shared.Compositor, backdropCornerRadius);

        using var spriteVisual = shared.Compositor.CreateSpriteVisual();
        _visual = spriteVisual.QueryInterface<IVisual>();
        _containerChildren.InsertAtTop(_visual);

        _surfaceBrush = shared.Compositor.CreateSurfaceBrush();
        using var compositionBrush = _surfaceBrush.QueryInterface<ICompositionBrush>();
        spriteVisual.SetBrush(compositionBrush);
        _target.SetRoot(containerVisual);
    }


    public void SetSurface(ICompositionSurface surface)
    {
        if (!_disposed)
            _surfaceBrush.SetSurface(surface);
    }

    public void SetBlur(BlurEffect blurEffect)
    {
        if (_currentBlurEffect == blurEffect || _disposed)
            return;
        lock (_shared.SyncRoot)
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
                if (_compositionRoundedRectangleGeometry != null)
                {
                    using var compositionGeometry =
                        _compositionRoundedRectangleGeometry.QueryInterface<ICompositionGeometry>();
                    using var compositor6 = _shared.Compositor.QueryInterface<ICompositor6>();
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
        Monitor.Enter(_shared.SyncRoot);
        return Disposable.Create(() => Monitor.Exit(_shared.SyncRoot));
    }

    public void ResizeIfNeeded(PixelSize size, double infoScaling, WindowState infoWindowState,
        float infoCompositionPadding, Vector3 scaleTransform, Vector3 centerPoint, float opacity,
        Vector3 infoOffset)
    {
        if (_disposed)
            return;
        lock (_shared.SyncRoot)
        {
            if (_disposed)
                return;
            centerPoint *= new Vector3((float)infoScaling);
            // ReSharper disable once CompareOfFloatsByEqualityOperator
            if (_size != size || _scale != scaleTransform || _centerPoint != centerPoint || _opacity != opacity ||
                infoOffset != _offset)
            {
                _visual.SetSize(new Vector2(size.Width, size.Height));

                float backdropPadding = infoWindowState == WindowState.Maximized ? 0 : infoCompositionPadding;
                var offset = (float)Math.Ceiling(backdropPadding * infoScaling);
                var sizeReduction = 2 * offset;

                float sizeWidth = size.Width - sizeReduction;
                float sizeHeight = size.Height - sizeReduction;
                if (sizeHeight > 0 && sizeWidth > 0)
                {
                    _compositionRoundedRectangleGeometry?.SetSize(new Vector2(sizeWidth, sizeHeight));
                    _compositionRoundedRectangleGeometry?.SetOffset(new Vector2(offset, offset));
                }
                else
                {
                    _compositionRoundedRectangleGeometry?.SetSize(new Vector2(size.Width, size.Height));
                    _compositionRoundedRectangleGeometry?.SetOffset(new Vector2(0, 0));
                }

                _currentVisual?.SetOffset(infoOffset);
                _currentVisual?.SetScale(scaleTransform);
                _currentVisual?.SetCenterPoint(centerPoint);
                _currentVisual?.SetOpacity(opacity);

                _compositionRoundedRectangleGeometry?.SetCornerRadius(
                    infoWindowState == WindowState.Maximized || !_backdropCornerRadius.HasValue ?
                        Vector2.Zero :
                        new Vector2((float)Math.Ceiling(_backdropCornerRadius.Value * infoScaling)));
                _size = size;
                _scale = scaleTransform;
                _centerPoint = centerPoint;
                _opacity = opacity;
                _offset = infoOffset;
            }
        }
    }

    private IVisual? CreateMicaLightVisual()
    {
        IVisual? micaLight = null;
        var micaBrushLight = CreateMicaBackdropBrush(242, 0.6f);
        if (micaBrushLight != null)
        {
            micaLight = CreateBlurVisual(micaBrushLight);
        }

        return micaLight;
    }

    private IVisual? CreateAcrylicVisual()
    {
        var acrylicBlurBackdropBrush = CreateAcrylicBlurBackdropBrush();
        if (acrylicBlurBackdropBrush != null)
        {
            return CreateBlurVisual(acrylicBlurBackdropBrush);
        }

        return null;
    }

    private IVisual? CreateMicaDarkVisual()
    {
        IVisual? micaDark = null;
        var micaBrushDark = CreateMicaBackdropBrush(32, 0.8f);

        if (micaBrushDark != null)
        {
            micaDark = CreateBlurVisual(micaBrushDark);
        }

        return micaDark;
    }


    private ICompositionBrush? CreateMicaBackdropBrush(float color, float opacity)
    {
        if (Win32Platform.WindowsVersion.Build < 22000)
            return null;

        using var backDropParameterFactory =
            NativeWinRTMethods.CreateActivationFactory<ICompositionEffectSourceParameterFactory>(
                "Windows.UI.Composition.CompositionEffectSourceParameter");


        var tint = new[] { color / 255f, color / 255f, color / 255f, 255f / 255f };

        using var tintColorEffect = new ColorSourceEffect(tint);


        using var tintOpacityEffect = new OpacityEffect(1.0f, tintColorEffect);
        using var tintOpacityEffectFactory = _shared.Compositor.CreateEffectFactory(tintOpacityEffect);
        using var tintOpacityEffectBrushEffect = tintOpacityEffectFactory.CreateBrush();
        using var tintOpacityEffectBrush = tintOpacityEffectBrushEffect.QueryInterface<ICompositionBrush>();

        using var luminosityColorEffect = new ColorSourceEffect(tint);

        using var luminosityOpacityEffect = new OpacityEffect(opacity, luminosityColorEffect);
        using var luminosityOpacityEffectFactory = _shared.Compositor.CreateEffectFactory(luminosityOpacityEffect);
        using var luminosityOpacityEffectBrushEffect = luminosityOpacityEffectFactory.CreateBrush();
        using var luminosityOpacityEffectBrush =
            luminosityOpacityEffectBrushEffect.QueryInterface<ICompositionBrush>();


        // using var backDropParameterAsSource = GetParameterSource("BlurredWallpaperBackdrop", backDropParameterFactory, out var backdropHandle);
        // using var backdropCompositionBrsuh = backDropParameterAsSource.QueryInterface<ICompositionBrush>();
        using var compositorWithBlurredWallpaperBackdropBrush =
            _shared.Compositor.QueryInterface<ICompositorWithBlurredWallpaperBackdropBrush>();
        using var blurredWallpaperBackdropBrush =
            compositorWithBlurredWallpaperBackdropBrush?.TryCreateBlurredWallpaperBackdropBrush();
        using var micaBackdropBrush = blurredWallpaperBackdropBrush?.QueryInterface<ICompositionBrush>();


        using var backgroundParameterAsSource =
            GetParameterSource("Background", backDropParameterFactory, out var backgroundHandle);
        using var foregroundParameterAsSource =
            GetParameterSource("Foreground", backDropParameterFactory, out var foregroundHandle);

        using var luminosityBlendEffect =
            new BlendEffect(23, backgroundParameterAsSource, foregroundParameterAsSource);
        using var luminosityBlendEffectFactory = _shared.Compositor.CreateEffectFactory(luminosityBlendEffect);
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
        using var colorBlendEffectFactory = _shared.Compositor.CreateEffectFactory(colorBlendEffect);
        using var colorBlendEffectBrush = colorBlendEffectFactory.CreateBrush();
        colorBlendEffectBrush.SetSourceParameter(backgroundHandle1, luminosityBlendEffectBrush1);
        colorBlendEffectBrush.SetSourceParameter(foregroundHandle1, tintOpacityEffectBrush);


        // colorBlendEffectBrush.SetSourceParameter(backgroundHandle, micaBackdropBrush);

        using var micaBackdropBrush1 = colorBlendEffectBrush.QueryInterface<ICompositionBrush>();
        return micaBackdropBrush1.CloneReference();
    }

    ICompositionBrush CreateAcrylicBrushCompositionEffectFactory(
        bool shouldBrushBeOpaque,
        bool useWindowAcrylic,
        bool useCrossFadeEffect,
        float initialTintColor,
        float initialLuminosityColor,
        float initialFallbackColor)
    {
        // The part of the effect graph below the noise layer. This is either a semi-transparent tint (common) or an opaque tint (uncommon).
        // Opaque tint may be used by apps wishing add the complexity of noise to their brand color, for example.
        ICompositionEffectBrush tintOutput;

        // Tint Color - either used directly or in a Color blend over a blurred backdrop
        var tint = new[] { initialTintColor / 255f, initialTintColor / 255f, initialTintColor / 255f, 255f / 255f };
        using var tintColorEffect = new ColorSourceEffect(tint);

        using var effectSourceParameterFactory =
            NativeWinRTMethods.CreateActivationFactory<ICompositionEffectSourceParameterFactory>(
                "Windows.UI.Composition.CompositionEffectSourceParameter");
        if (shouldBrushBeOpaque)
        {
            var effectFactory = _shared.Compositor.CreateEffectFactory(tintColorEffect);
            tintOutput = effectFactory.CreateBrush();
        }
        else
        {
            // Load the backdrop in a brush

            using var backDropParameterFactory =
                NativeWinRTMethods.CreateActivationFactory<ICompositionEffectSourceParameterFactory>(
                    "Windows.UI.Composition.CompositionEffectSourceParameter");
            using var backdropString = new HStringInterop("backdrop");
            using var backdropEffectSourceParameter =
                backDropParameterFactory.Create(backdropString.Handle);

            // Get a blurred backdrop...
            IGraphicsEffect blurredSource;
            if (useWindowAcrylic)
            {
                // ...either the shell baked the blur into the backdrop brush, and we use it directly...
                blurredSource = backdropEffectSourceParameter.QueryInterface<IGraphicsEffect>();
            }
            else
            {
                // ...or we apply the blur ourselves
                var gaussianBlurEffect =
                    new WinUIGaussianBlurEffect(backdropEffectSourceParameter.QueryInterface<IGraphicsEffectSource>());
                blurredSource = gaussianBlurEffect;
            }

            tintOutput = //SharedHelpers::Is19H1OrHigher() ?
                CombineNoiseWithTintEffect_Luminosity(effectSourceParameterFactory,blurredSource, tintColorEffect, initialLuminosityColor);
            //CombineNoiseWithTintEffect_Legacy(blurredSource, *tintColorEffect);
        }

        // Create noise with alpha and wrap:
        // Noise image BorderEffect (infinitely tiles noise image)
   
        // using var noiseString = new HStringInterop("Noise");
        // using var noiseEffectSourceParameter =
        //     effectSourceParameterFactory.Create(noiseString.Handle);
        //
        // var noiseBorderEffect =
        //     new BorderEffect(1, 1, noiseEffectSourceParameter.QueryInterface<IGraphicsEffectSource>());
        //
        //
        // // OpacityEffect applied to wrapped noise
        // var noiseOpacityEffect = new OpacityEffect(0.02f, noiseBorderEffect);
        // Blend noise on top of tint
        
        
        // using var destinationAsParameter =
        //     GetParameterSource("Destination", effectSourceParameterFactory, out var destinationHandle);
        // // using var sourceAsParameter =
        // //     GetParameterSource("Source", effectSourceParameterFactory, out var sourceHandle);
        // //
        // using var blendEffectOuter =
        //     new CompositeStepEffect(0, destinationAsParameter);
        // using var blendEffectFactory = _shared.Compositor.CreateEffectFactory(blendEffectOuter);
        // using var blendEffectBrush = blendEffectFactory.CreateBrush();
        // var blendEffectBrush1 = blendEffectBrush.QueryInterface<ICompositionBrush>();
        //
        //
        // blendEffectBrush.SetSourceParameter(destinationHandle, tintOutput.QueryInterface<ICompositionBrush>());
        
        // using var effectFactory = _shared.Compositor.CreateEffectFactory(noiseOpacityEffect);
        // blendEffectBrush.SetSourceParameter(sourceHandle, effectFactory.CreateBrush().QueryInterface<ICompositionBrush>());
        

        // if (useCrossFadeEffect)
        // {
        //     // Fallback color
        //     auto fallbackColorEffect = winrt::make_self<Microsoft::UI::Private::Composition::Effects::ColorSourceEffect>();
        //     fallbackColorEffect->Name(L"FallbackColor");
        //     fallbackColorEffect->Color(initialFallbackColor);
        //
        //     // CrossFade with the fallback color. Weight = 0 means full fallback, 1 means full acrylic.
        //     auto fadeInOutEffect = winrt::make_self<Microsoft::UI::Private::Composition::Effects::CrossFadeEffect>();
        //     fadeInOutEffect->Name(L"FadeInOut");
        //     fadeInOutEffect->Source1(*fallbackColorEffect);
        //     fadeInOutEffect->Source2(*blendEffectOuter);
        //     fadeInOutEffect->Weight(1.0f);
        //
        //     animatedProperties.push_back(winrt::hstring{ FallbackColorColor });
        //     animatedProperties.push_back(L"FadeInOut.Weight");
        //     effectFactory = compositor.CreateEffectFactory(*fadeInOutEffect, animatedProperties);
        // }
        // else

        // }
        return tintOutput.QueryInterface<ICompositionBrush>();
    }


    ICompositionEffectBrush CombineNoiseWithTintEffect_Luminosity(
        ICompositionEffectSourceParameterFactory compositionEffectSourceParameterFactory,
        IGraphicsEffect blurredSource,
        IGraphicsEffect tintColorEffect,
        float initialLuminosityColor)
    {
        // Apply luminosity:

        // Luminosity Color
        var tint = new[]
        {
            initialLuminosityColor / 255f, initialLuminosityColor / 255f, initialLuminosityColor / 255f, 255f / 255f
        };

        // Apply tint:
        var luminosityColorEffect = new ColorSourceEffect(tint);
        
        
         var backgroundParameterAsSource =
            GetParameterSource("Background", compositionEffectSourceParameterFactory, out var backgroundHandle);
         var foregroundParameterAsSource =
            GetParameterSource("Foreground", compositionEffectSourceParameterFactory, out var foregroundHandle);
        
         var luminosityBlendEffect =
            new BlendEffect(22, backgroundParameterAsSource, foregroundParameterAsSource);
            // new BlendEffect(22, blurredSource, luminosityColorEffect);
             var luminosityBlendEffectFactory = _shared.Compositor.CreateEffectFactory(luminosityBlendEffect);
             var luminosityBlendEffectBrush = luminosityBlendEffectFactory.CreateBrush();
             var luminosityBlendEffectBrush1 = luminosityBlendEffectBrush.QueryInterface<ICompositionBrush>();


            var compositionEffectFactory = _shared.Compositor.CreateEffectFactory(blurredSource);
            var compositionEffectBrush = compositionEffectFactory.CreateBrush().QueryInterface<ICompositionBrush>();
            
            luminosityBlendEffectBrush.SetSourceParameter(backgroundHandle, compositionEffectBrush);

            var effectFactory = _shared.Compositor.CreateEffectFactory(luminosityColorEffect);
            luminosityBlendEffectBrush.SetSourceParameter(foregroundHandle, effectFactory.CreateBrush().QueryInterface<ICompositionBrush>());
            
            

        // Color blend

         var backgroundParameterAsSource1 =
            GetParameterSource("Background", compositionEffectSourceParameterFactory, out var backgroundHandle1);
         var foregroundParameterAsSource1 =
            GetParameterSource("Foreground", compositionEffectSourceParameterFactory, out var foregroundHandle1);
        
        var colorBlendEffect =
            new BlendEffect(23, backgroundParameterAsSource1, foregroundParameterAsSource1);
            // new BlendEffect(23, luminosityBlendEffect, tintColorEffect);

             var colorBlendEffectFactory = _shared.Compositor.CreateEffectFactory(colorBlendEffect);
             var colorBlendEffectBrush = colorBlendEffectFactory.CreateBrush();
            colorBlendEffectBrush.SetSourceParameter(backgroundHandle1, luminosityBlendEffectBrush1);

            var factory = _shared.Compositor.CreateEffectFactory(tintColorEffect);
            colorBlendEffectBrush.SetSourceParameter(foregroundHandle1, factory.CreateBrush().QueryInterface<ICompositionBrush>());

        return colorBlendEffectBrush;
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


    private ICompositionBrush? CreateAcrylicBlurBackdropBrush()
    {
        using var backDropParameterFactory =
            NativeWinRTMethods.CreateActivationFactory<ICompositionEffectSourceParameterFactory>(
                "Windows.UI.Composition.CompositionEffectSourceParameter");
        using var backdropString = new HStringInterop("backdrop");
        using var backDropParameter =
            backDropParameterFactory.Create(backdropString.Handle);
        using var backDropParameterAsSource = backDropParameter.QueryInterface<IGraphicsEffectSource>();
        var blurEffect = new WinUIGaussianBlurEffect(backDropParameterAsSource);
        using var blurEffectFactory = _shared.Compositor.CreateEffectFactory(blurEffect);
        using var compositionEffectBrush = blurEffectFactory.CreateBrush();
        using var backdrop = CreateBackdropBrush();
        using var backdropBrush = backdrop.QueryInterface<ICompositionBrush>();

        var saturateEffect = new SaturationEffect(blurEffect);
        using var satEffectFactory = _shared.Compositor.CreateEffectFactory(saturateEffect);
        using var sat = satEffectFactory.CreateBrush();
        compositionEffectBrush.SetSourceParameter(backdropString.Handle, backdropBrush);
        return compositionEffectBrush.QueryInterface<ICompositionBrush>();
    }


    private IVisual? CreateBlurVisual(ICompositionBrush? compositionBrush)
    {
        using var spriteVisual = _shared.Compositor.CreateSpriteVisual();
        using var visual = spriteVisual.QueryInterface<IVisual>();
        using var visual2 = spriteVisual.QueryInterface<IVisual2>();


        spriteVisual.SetBrush(compositionBrush);
        visual2.SetRelativeSizeAdjustment(new Vector2(1.0f, 1.0f));

        return visual.CloneReference();
    }

    private ICompositionBrush? CreateBackdropBrush()
    {
        ICompositionBackdropBrush? brush = null;
        try
        {
            if (Win32Platform.WindowsVersion >= WinUiCompositionShared.MinHostBackdropVersion)
            {
                using var compositor3 = _shared.Compositor.QueryInterface<ICompositor3>();
                brush = compositor3.CreateHostBackdropBrush();
            }
            else
            {
                using var compositor2 = _shared.Compositor.QueryInterface<ICompositor2>();
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
