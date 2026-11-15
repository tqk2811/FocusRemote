using FocusRemote.Host.Core.Input;
using FocusRemote.Host.Core.Models;
using FocusRemote.Host.Tests.Helpers;
using FocusRemote.Protocol.Enums;
using FocusRemote.Protocol.Messages.Models.Input;

namespace FocusRemote.Host.Tests;

public sealed class LoggingInputInjectorTests
{
    private static readonly PixelRect _target = new(100, 50, 1281, 721);

    [Fact]
    public void Handle_LogsMappedPointsInsteadOfInjecting()
    {
        var log = new MemoryLog();
        var injector = new LoggingInputInjector(log);

        injector.Handle(new MouseMoveInput { X = 0, Y = 65535 }, _target, IntPtr.Zero);
        injector.Handle(new MouseButtonInput { Button = MouseButton.Left, Down = true, X = 32768, Y = 32768 }, _target, IntPtr.Zero);
        injector.Handle(new KeyInput { Down = false, Code = "KeyA" }, _target, IntPtr.Zero);
        injector.ReleaseAll();

        Assert.Equal(
            [
                "INFO INPUT MouseMove x=100 y=770",
                "INFO INPUT MouseButton Left down=True x=740 y=410",
                "INFO INPUT Key KeyA down=False",
                "INFO INPUT ReleaseAll",
            ],
            log.Lines.ToArray());
    }
}
