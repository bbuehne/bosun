using System.Reflection;

namespace Bosun.Remote.Tests;

/// <summary>
/// Pins ADR-024's structural rules for this project so a later change cannot break them quietly.
/// </summary>
public sealed class ProjectBoundaryTests
{
    private static readonly string[] ReferencedNames =
        typeof(DevelopmentAreaGuard).Assembly.GetReferencedAssemblies().Select(a => a.Name ?? "").ToArray();

    [Fact]
    public void The_project_does_not_reference_wpf_or_the_bosun_app()
    {
        Assert.DoesNotContain(ReferencedNames, n => n.StartsWith("Presentation", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(ReferencedNames, n => n.Equals("WindowsBase", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(ReferencedNames, n => n.Equals("Bosun", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Tripwire, not a proof. ADR-024: no tool runs a caller-supplied command. Today nothing in the
    /// project can start a process at all. The issue that adds launching (bs-4xw.8 and later) will fail
    /// this test, which is the moment to confirm the launch takes only values the tool built itself,
    /// and then to move the allowance to the one type that launches.
    /// </summary>
    [Fact]
    public void The_project_does_not_reference_the_process_api()
    {
        Assert.DoesNotContain(ReferencedNames, n => n.Equals("System.Diagnostics.Process", StringComparison.OrdinalIgnoreCase));
    }
}
