using System;
using Avalonia.Metadata;

namespace Avalonia.Animation
{
    public interface IClock : IObservable<TimeSpan>
    {
        PlayState PlayState { get; set; }
    }
}
