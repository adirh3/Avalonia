namespace Avalonia.Win32.WinRT
{
    public enum BlurEffect
    {
        None,
        Acrylic,
        MicaLight,
        MicaDark
    }
    
    public interface IBlurHost
    {
        void SetBlur(BlurEffect enable);
    }
}
