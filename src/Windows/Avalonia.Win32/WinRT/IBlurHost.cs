namespace Avalonia.Win32.WinRT
{
    public enum BlurEffect
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
