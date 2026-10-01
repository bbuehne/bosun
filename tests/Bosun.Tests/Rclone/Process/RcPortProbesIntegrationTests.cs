using System.Net;
using System.Net.Sockets;
using System.Security.Principal;
using Bosun.Rclone.Process;
using Bosun.SessionMonitor.Interop;
using Diag = System.Diagnostics;

namespace Bosun.Tests.Rclone.Process;

/// <summary>
/// Opt-in tests of the real port-owner / process-inspector / terminator behind
/// <see cref="RcPortGuard"/> (run with <c>--settings tests/Bosun.Tests/integration.runsettings</c>).
/// They only act on a loopback listener the test opened and on <c>ping.exe</c> children the test
/// started, and read (never modify) the current process.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
public sealed class RcPortProbesIntegrationTests
{
    [Fact]
    public void The_port_resolver_finds_the_process_listening_on_a_loopback_port()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;

            var owner = new TcpPortOwnerResolver(new Win32TcpConnectionReader()).GetListeningProcessId(port);

            Assert.Equal(Environment.ProcessId, owner);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public void The_inspector_reports_owner_in_the_same_form_as_the_current_identity()
    {
        // The kill rule compares the owner string against WindowsIdentity.GetCurrent().Name, so
        // the two forms (DOMAIN\user) must agree on a real machine.
        var description = new CimProcessInspector().Describe(Environment.ProcessId);

        Assert.NotNull(description);
        Assert.Equal(WindowsIdentity.GetCurrent().Name, description.Owner, ignoreCase: true);
        Assert.False(string.IsNullOrEmpty(description.Name));
        Assert.False(string.IsNullOrEmpty(description.ImagePath));
        Assert.False(string.IsNullOrEmpty(description.CommandLine));
        Assert.NotNull(description.StartTime);
    }

    [Fact]
    public void The_terminator_kills_only_the_instance_it_was_told_about()
    {
        var terminator = new SystemProcessTerminator();
        using var child = Diag.Process.Start(new Diag.ProcessStartInfo("ping.exe", "-n 20 127.0.0.1")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        })!;
        try
        {
            var start = new DateTimeOffset(child.StartTime);

            // Wrong start time (as if the PID had been reused): refused, child untouched.
            Assert.False(terminator.TryKill(child.Id, start.AddHours(-3)));
            Assert.False(child.HasExited);
            Assert.False(terminator.HasExited(child.Id, start));

            // Right start time: killed.
            Assert.True(terminator.TryKill(child.Id, start));
            Assert.True(child.WaitForExit(10_000));
            Assert.True(terminator.HasExited(child.Id, start));
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill();
            }
        }
    }
}
