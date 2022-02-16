using System;
using System.Numerics;
using System.Reactive.Disposables;
using System.Threading;
using Avalonia.Controls;
using Avalonia.MicroCom;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Egl;
using Avalonia.Win32.Interop;

namespace Avalonia.Win32.WinRT.Composition
{
    public class WinUICompositedWindow : IDisposable
    {
        private EglContext _syncContext;
        private readonly object _pumpLock;
        private readonly IVisual _micaDarkVisual;
        private readonly IVisual _micaLightVisual;
        private readonly ICompositionRoundedRectangleGeometry _roundedRectangleGeometry;
        private readonly float _backdropCornerRadius;
        private readonly IVisual _blurVisual;
        private ICompositionTarget _compositionTarget;
        private IVisual _contentVisual;
        private ICompositionDrawingSurfaceInterop _surfaceInterop;
        private PixelSize _size;

        private static Guid IID_ID3D11Texture2D = Guid.Parse("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
        private ICompositor _compositor;


        internal WinUICompositedWindow(EglContext syncContext,
            ICompositor compositor,
            object pumpLock,
            ICompositionTarget compositionTarget,
            ICompositionDrawingSurfaceInterop surfaceInterop,
            IVisual contentVisual, IVisual blurVisual, IVisual micaDarkVisual, IVisual micaLightVisual,
            ICompositionRoundedRectangleGeometry roundedRectangleGeometry, float backdropCornerRadius)
        {
            _compositor = compositor.CloneReference();
            _syncContext = syncContext;
            _pumpLock = pumpLock;
            _micaDarkVisual = micaDarkVisual;
            _micaLightVisual = micaLightVisual;
            _roundedRectangleGeometry = roundedRectangleGeometry;
            _backdropCornerRadius = backdropCornerRadius;
            _blurVisual = blurVisual.CloneReference();
            _compositionTarget = compositionTarget.CloneReference();
            _contentVisual = contentVisual.CloneReference();
            _surfaceInterop = surfaceInterop.CloneReference();
        }


        public void ResizeIfNeeded(PixelSize size, double infoScaling, WindowState infoWindowState,
            float infoCompositionPadding)
        {
            using (_syncContext.EnsureLocked())
            {
                if (_size != size)
                {
                    _surfaceInterop.Resize(new UnmanagedMethods.POINT { X = size.Width, Y = size.Height });
                    _contentVisual.SetSize(new Vector2(size.Width, size.Height));
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

                    _roundedRectangleGeometry?.SetCornerRadius(infoWindowState == WindowState.Maximized ?
                        Vector2.Zero :
                        new Vector2((float)Math.Ceiling(_backdropCornerRadius * infoScaling)));
                    _size = size;
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
            using (_syncContext.EnsureLocked())
            {
                _blurVisual.SetIsVisible(blurEffect == BlurEffect.Acrylic ? 1 : 0);
                _micaDarkVisual?.SetIsVisible(blurEffect == BlurEffect.MicaDark ? 1 : 0);
                _micaLightVisual?.SetIsVisible(blurEffect == BlurEffect.MicaLight ? 1 : 0);
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
                _blurVisual.Dispose();
                _contentVisual.Dispose();
                _surfaceInterop.Dispose();
                _compositionTarget.Dispose();
            }
        }
    }
}
