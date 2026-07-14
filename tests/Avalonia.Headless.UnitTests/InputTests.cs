using System;
using System.Reactive.Disposables;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Threading;

namespace Avalonia.Headless.UnitTests;

public class InputTests
#if XUNIT
    : IDisposable
#endif
{
    private Window _window;
    private Application _setupApp;

#if NUNIT
    [SetUp]
    public void SetUp()
#elif XUNIT
    public InputTests()
#endif
    {
        _setupApp = Application.Current;
        Dispatcher.UIThread.VerifyAccess();
        _window = new Window
        {
            Width = 100,
            Height = 100
        };
    }
    
#if NUNIT
    [AvaloniaTest]
#elif XUNIT
    [AvaloniaFact]
#endif
    public void Should_Click_Button_On_Window()
    {
        AssertHelper.Same(_setupApp, Application.Current);
        var buttonClicked = false;
        var button = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };

        button.Click += (_, _) => buttonClicked = true;
        
        _window.Content = button;
        _window.Show();

        _window.MouseDown(new Point(50, 50), MouseButton.Left);
        _window.MouseUp(new Point(50, 50), MouseButton.Left);

        AssertHelper.True(buttonClicked);
    }
    
#if NUNIT
    [AvaloniaTest]
#elif XUNIT
    [AvaloniaFact]
#endif
    public void Change_Window_Position()
    {
        var newWindowPosition = new PixelPoint(100, 150);
        _window.Position = newWindowPosition;
        _window.Show();
        AssertHelper.Equal(newWindowPosition, _window.Position);
    }

#if NUNIT
    [AvaloniaTest]
#elif XUNIT
    [AvaloniaFact]
#endif
    public void Should_Click_Button_After_Explicit_RunJobs()
    {
        // Regression test for https://github.com/AvaloniaUI/Avalonia/issues/20309
        // Ensure that calling Dispatcher.UIThread.RunJobs() before MouseDown does not throw
        var button = new Button { Content = "Test content" };
        _window.Content = button;
        _window.Show();

        Dispatcher.UIThread.RunJobs();

        var clickCount = 0;
        button.Click += (_, _) => clickCount++;

        var point = new Point(button.Bounds.Width / 2, button.Bounds.Height / 2);
        var translatePoint = button.TranslatePoint(point, _window);

        // Move
        _window.MouseMove(translatePoint!.Value, RawInputModifiers.None);

        // Click
        _window.MouseDown(translatePoint.Value, MouseButton.Left, RawInputModifiers.None);
        _window.MouseUp(translatePoint.Value, MouseButton.Left, RawInputModifiers.None);

        AssertHelper.Equal(1, clickCount);
    }

#if NUNIT
    [AvaloniaTest]
#elif XUNIT
    [AvaloniaFact]
#endif
    public void Legacy_Key_Helpers_Use_Empty_Physical_Key_Metadata()
    {
        KeyEventArgs? keyDown = null;
        KeyEventArgs? keyUp = null;
        var textBox = new TextBox();
        textBox.AddHandler(InputElement.KeyDownEvent, (_, e) => keyDown = e, handledEventsToo: true);
        textBox.AddHandler(InputElement.KeyUpEvent, (_, e) => keyUp = e, handledEventsToo: true);

        _window.Content = textBox;
        _window.Show();
        AssertHelper.True(textBox.Focus());

        _window.KeyPress(Key.A, RawInputModifiers.Control);
        _window.KeyRelease(Key.A, RawInputModifiers.Control);

        AssertHelper.NotNull(keyDown);
        AssertHelper.NotNull(keyUp);
        AssertHelper.Equal(Key.A, keyDown!.Key);
        AssertHelper.Equal(Key.A, keyUp!.Key);
        AssertHelper.Equal(PhysicalKey.None, keyDown.PhysicalKey);
        AssertHelper.Equal(PhysicalKey.None, keyUp.PhysicalKey);
    }

#if NUNIT
    [TearDown]
    public void TearDown()
#elif XUNIT
    public void Dispose()
#endif
    {
        AssertHelper.Same(_setupApp, Application.Current);

        Dispatcher.UIThread.VerifyAccess();
        _window.Close();
    }
}
