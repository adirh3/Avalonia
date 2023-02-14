namespace Avalonia.Win32.WinRT
{
    internal enum BlurEffect
    {
        None,
        Acrylic,
        MicaDark,
        MicaLight
    }
    
    public interface IBlurHost
    {
        void SetBlur(BlurEffect enable);
    }
}
