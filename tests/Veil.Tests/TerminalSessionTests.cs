using System.Text;
using Veil.Services.Terminal;

namespace Veil.Tests;

[TestClass]
public sealed class TerminalSessionTests
{
    private static readonly string CmdPath = Path.Combine(Environment.SystemDirectory, "cmd.exe");

    [TestMethod]
    public async Task TerminalSession_starts_cmd_and_processes_input()
    {
        Assert.IsTrue(File.Exists(CmdPath), "cmd.exe must be available for terminal runtime tests.");

        using var session = new TerminalSession(
            new TerminalProfile("cmd", "Command Prompt", CmdPath, "/d /q", null, null, true),
            cols: 120,
            rows: 30);

        Assert.IsTrue(session.IsAlive, "The interactive cmd session should still be alive after startup.");

        var outputSeen = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.OutputReceived += data =>
        {
            string text = Encoding.UTF8.GetString(data);
            if (text.Contains("VEIL_TERMINAL_TEST_OK", StringComparison.Ordinal))
            {
                outputSeen.TrySetResult(true);
            }
        };

        session.Write(Encoding.UTF8.GetBytes("echo VEIL_TERMINAL_TEST_OK\r"));

        Task completed = await Task.WhenAny(outputSeen.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.AreSame(outputSeen.Task, completed, "The terminal session did not echo the probe command.");
        Assert.IsTrue(session.IsAlive, "The cmd session should remain alive after processing input.");
    }

    [TestMethod]
    public void TerminalSession_failed_startup_does_not_poison_next_session()
    {
        Assert.IsTrue(File.Exists(CmdPath), "cmd.exe must be available for terminal runtime tests.");

        var failingProfile = new TerminalProfile("cmd-fail", "Command Prompt", CmdPath, "/d /c exit 42", null, null, true);
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => _ = new TerminalSession(failingProfile, 80, 24));
        StringAssert.Contains(ex.Message, "0x0000002A");

        using var session = new TerminalSession(
            new TerminalProfile("cmd", "Command Prompt", CmdPath, "/d /q", null, null, true),
            cols: 120,
            rows: 30);

        Assert.IsTrue(session.IsAlive, "A later tab launch should still succeed after one failed startup.");
    }
}
