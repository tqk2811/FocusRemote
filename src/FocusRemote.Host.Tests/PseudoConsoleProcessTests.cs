using System.Text;
using FocusRemote.Host.Core.Terminal;
using FocusRemote.Host.Core.Terminal.Interfaces;

namespace FocusRemote.Host.Tests;

/// <summary>Real ConPTY: runs cmd.exe and checks output and exit reporting.</summary>
public sealed class PseudoConsoleProcessTests
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task Cmd_EchoesInputAndReportsExit()
    {
        string cmd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
        var listener = new RecordingListener();
        using var process = new PseudoConsoleProcess("\"" + cmd + "\" /q /k", Path.GetTempPath(), 80, 25, listener);
        process.Resize(100, 30);
        process.Write(System.Text.Encoding.ASCII.GetBytes("echo FR_MARK_%COMPUTERNAME:~0,0%42\r\nexit 5\r\n"));

        int exitCode = await listener.Exited.Task.WaitAsync(_timeout);
        Assert.Equal(5, exitCode);
        Assert.Contains("FR_MARK_42", listener.Text);
    }

    [Fact]
    public async Task Dispose_KillsWithoutReportingExit()
    {
        string cmd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
        var listener = new RecordingListener();
        var process = new PseudoConsoleProcess("\"" + cmd + "\" /q /k", Path.GetTempPath(), 80, 25, listener);
        await Task.Delay(300);
        await Task.Run(process.Dispose).WaitAsync(_timeout);
        await Task.Delay(200);
        Assert.False(listener.Exited.Task.IsCompleted);
    }

    private sealed class RecordingListener : ITerminalProcessListener
    {
        private readonly MemoryStream _output = new();

        public TaskCompletionSource<int> Exited { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Text
        {
            get { lock (_output) return System.Text.Encoding.UTF8.GetString(_output.ToArray()); }
        }

        public void OnOutput(ReadOnlySpan<byte> data)
        {
            lock (_output)
                _output.Write(data);
        }

        public void OnExited(int exitCode) => Exited.TrySetResult(exitCode);
    }
}
