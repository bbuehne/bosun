using Bosun.Terminal;

namespace Bosun.Tests.Terminal;

/// <summary>bs-e28: the single <c>wt -w 0</c> argument list for the autostart tabs.</summary>
public sealed class WtAutostartArgumentBuilderTests
{
    [Fact]
    public void OneProfile_IsOneNewTab()
    {
        var args = WtAutostartArgumentBuilder.Build(["Example Jump"]);

        Assert.Equal(["-w", "0", "new-tab", "-p", "Example Jump"], args);
    }

    [Fact]
    public void ThreeProfiles_AreChainedWithBareSemicolonArguments_InOrder()
    {
        var args = WtAutostartArgumentBuilder.Build(["A", "B", "C"]);

        Assert.Equal(
            ["-w", "0", "new-tab", "-p", "A", ";", "new-tab", "-p", "B", ";", "new-tab", "-p", "C"],
            args);
    }

    [Fact]
    public void ASemicolonInAName_IsEscapedSoTerminalDoesNotSplitTheCommand()
    {
        var args = WtAutostartArgumentBuilder.Build(["prod; rm", "plain"]);

        Assert.Equal(
            ["-w", "0", "new-tab", "-p", @"prod\; rm", ";", "new-tab", "-p", "plain"],
            args);

        // Exactly one bare separator: the one between the two tabs, not the one inside the name.
        Assert.Single(args, a => a == ";");
    }

    [Fact]
    public void ABackslashBeforeASemicolon_StaysSplitSafe()
    {
        // Same rule as WtRestoreArgumentBuilder: a\;b -> a\\;b, so the ';' is still preceded by '\'.
        var args = WtAutostartArgumentBuilder.Build([@"a\;b"]);

        Assert.Equal(@"a\\;b", args[^1]);
    }

    [Fact]
    public void EmptyList_Throws()
    {
        Assert.Throws<ArgumentException>(() => WtAutostartArgumentBuilder.Build([]));
    }

    [Fact]
    public void ABlankName_Throws()
    {
        Assert.Throws<ArgumentException>(() => WtAutostartArgumentBuilder.Build(["ok", "  "]));
    }
}
