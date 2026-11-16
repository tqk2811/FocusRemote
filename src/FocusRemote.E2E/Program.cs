using FocusRemote.E2E;
using FocusRemote.Host.Core;
using FocusRemote.Host.Core.Input;
using FocusRemote.Host.Core.Settings.Models;
using FocusRemote.Protocol.Crypto;

// Headless host for end-to-end tests: real capture + encoder, dry-run input (logged, never injected).
// Prints "STATUS ..." lines on every state change and "INPUT ..." lines for each input message received.
// Stops on Ctrl+C or when stdin reaches end of file.

HarnessOptions? options = HarnessOptions.Parse(args);
if (options is null)
{
    Console.Error.WriteLine("Usage: FocusRemote.E2E --id <id> --password <pwd> [--relay <url>] [--lan-port <port>] [--real-input]");
    return 2;
}

var log = new ConsoleAppLog();
byte[] rootKey = KeyDerivation.DeriveRootKey(options.Password, options.Id);
string webRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
await using var service = new HostService(log, webRoot, options.RealInput ? null : () => new LoggingInputInjector(log));
service.StatusChanged += status => log.Info(
    $"STATUS relay={status.RelayState} lan={status.LanRunning}:{status.LanPort} viewer={status.Viewer ?? "-"} " +
    $"stream={(status.Stream is null ? "-" : status.Stream.ToString())} lanError={status.LanError ?? "-"}");

var settings = new HostSettings
{
    Id = options.Id,
    RelayUrl = options.RelayUrl ?? string.Empty,
    LanEnabled = options.LanPort is not null,
    LanPort = options.LanPort ?? HostSettings.DefaultLanPort,
    LanLoopbackOnly = true,
};
await service.StartAsync(settings, rootKey);
log.Info("READY");

var stop = new TaskCompletionSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stop.TrySetResult();
};
_ = Task.Run(() =>
{
    while (Console.In.ReadLine() is not null)
    {
    }
    stop.TrySetResult();
});
await stop.Task;
log.Info("STOPPING");
await service.StopAsync();
return 0;
