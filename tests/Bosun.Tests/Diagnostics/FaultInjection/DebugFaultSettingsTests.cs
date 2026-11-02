#if DEBUG
using Bosun.Diagnostics.FaultInjection;
using Bosun.Supervisor;
using Serilog.Core;
using Serilog.Events;

namespace Bosun.Tests.Diagnostics.FaultInjection;

/// <summary>
/// bs-uzk: the parser for <c>BOSUN_FAULT</c> / <c>BOSUN_FAULT_SECONDS</c>. This file is inside
/// <c>#if DEBUG</c> because the type under test does not exist in a Release build; that absence is
/// checked separately by building Release and searching the assembly (see the bs-uzk report).
/// </summary>
public sealed class DebugFaultSettingsTests
{
    [Fact]
    public void VariableNames_AreTheDocumentedOnes()
    {
        // docs/OPERATIONS.md T9/T12/T13 tell the maintainer to set exactly these.
        Assert.Equal("BOSUN_FAULT", DebugFaultSettings.VariableName);
        Assert.Equal("BOSUN_FAULT_SECONDS", DebugFaultSettings.SecondsVariableName);
    }

    [Theory]
    [InlineData("supervisor-start", DebugFault.SupervisorStart)]
    [InlineData("ui-exception", DebugFault.UiException)]
    [InlineData("rcd-unavailable-at-resume", DebugFault.RcdUnavailableAtResume)]
    public void Parse_MapsEachValueToItsFault(string value, DebugFault expected)
    {
        var settings = DebugFaultSettings.Parse(value, seconds: null);

        Assert.Equal(expected, settings.Faults);
        Assert.True(settings.Any);
        Assert.True(settings.Has(expected));
        Assert.Empty(settings.Problems);
    }

    [Fact]
    public void Parse_CombinesCommaSeparatedValues_IgnoringCaseAndWhitespaceAndEmptyEntries()
    {
        var settings = DebugFaultSettings.Parse(" Supervisor-Start , ,UI-EXCEPTION,,rcd-unavailable-at-resume ", seconds: null);

        Assert.Equal(DebugFault.SupervisorStart | DebugFault.UiException | DebugFault.RcdUnavailableAtResume, settings.Faults);
        Assert.Empty(settings.Problems);
    }

    [Fact]
    public void Parse_ACombinationDoesNotActivateWhatWasNotNamed()
    {
        var settings = DebugFaultSettings.Parse("ui-exception,rcd-unavailable-at-resume", seconds: null);

        Assert.True(settings.Has(DebugFault.UiException));
        Assert.True(settings.Has(DebugFault.RcdUnavailableAtResume));
        Assert.False(settings.Has(DebugFault.SupervisorStart));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(",")]
    public void Parse_NothingNamed_ActivatesNothingAndReportsNoProblem(string? value)
    {
        var settings = DebugFaultSettings.Parse(value, seconds: null);

        Assert.Equal(DebugFault.None, settings.Faults);
        Assert.False(settings.Any);
        Assert.False(settings.Has(DebugFault.None));
        Assert.Empty(settings.Problems);
        Assert.Empty(settings.Describe());
    }

    [Theory]
    [InlineData("supervisor_start")]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("all")]
    [InlineData("supervisor-start;ui-exception")]
    public void Parse_AnUnknownValue_ActivatesNothingAndIsReportedAsAProblem(string value)
    {
        var settings = DebugFaultSettings.Parse(value, seconds: null);

        Assert.Equal(DebugFault.None, settings.Faults);
        var problem = Assert.Single(settings.Problems);
        Assert.Contains("BOSUN_FAULT", problem);
        Assert.Contains(value, problem);
    }

    [Fact]
    public void Parse_AKnownValueBesideAnUnknownOne_KeepsTheKnownAndReportsTheUnknown()
    {
        var settings = DebugFaultSettings.Parse("ui-exception,typo", seconds: null);

        Assert.Equal(DebugFault.UiException, settings.Faults);
        Assert.Contains("typo", Assert.Single(settings.Problems));
    }

    [Fact]
    public void Seconds_DefaultsToTwoMinutes_WhichIsInsideTheTenMinuteRemountWindow()
    {
        var settings = DebugFaultSettings.Parse("rcd-unavailable-at-resume", seconds: null);

        Assert.Equal(TimeSpan.FromSeconds(120), settings.RcdUnavailableDuration);
        Assert.Equal(DebugFaultSettings.DefaultRcdUnavailableDuration, settings.RcdUnavailableDuration);

        // The point of the default: the record is still alive when rcd comes back, so T9 part E can
        // show "mounts when rclone returns at 2 minutes" without setting anything.
        Assert.True(settings.RcdUnavailableDuration < MountSupervisor.RemountRecordWaitLimit);
    }

    [Theory]
    [InlineData("600", 600)]
    [InlineData("900", 900)]
    [InlineData("0.5", 0.5)]
    [InlineData(" 45 ", 45)]
    public void Seconds_ValidValuesAreUsed(string value, double expectedSeconds)
    {
        var settings = DebugFaultSettings.Parse("rcd-unavailable-at-resume", value);

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), settings.RcdUnavailableDuration);
        Assert.Empty(settings.Problems);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("86401")]
    [InlineData("2 minutes")]
    public void Seconds_UnusableValuesFallBackToTheDefaultAndAreReported(string value)
    {
        var settings = DebugFaultSettings.Parse("rcd-unavailable-at-resume", value);

        Assert.Equal(DebugFaultSettings.DefaultRcdUnavailableDuration, settings.RcdUnavailableDuration);
        var problem = Assert.Single(settings.Problems);
        Assert.Contains("BOSUN_FAULT_SECONDS", problem);
    }

    [Fact]
    public void Seconds_UsesTheInvariantCulture_SoADecimalPointIsAlwaysADot()
    {
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");

            var settings = DebugFaultSettings.Parse("rcd-unavailable-at-resume", "1.5");

            Assert.Equal(TimeSpan.FromSeconds(1.5), settings.RcdUnavailableDuration);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void FromEnvironment_ReadsExactlyTheTwoDocumentedVariables()
    {
        var asked = new List<string>();
        string? Get(string name)
        {
            asked.Add(name);
            return name switch
            {
                "BOSUN_FAULT" => "supervisor-start",
                "BOSUN_FAULT_SECONDS" => "300",
                _ => null,
            };
        }

        var settings = DebugFaultSettings.FromEnvironment(Get);

        Assert.Equal(DebugFault.SupervisorStart, settings.Faults);
        Assert.Equal(TimeSpan.FromSeconds(300), settings.RcdUnavailableDuration);
        Assert.Equal(["BOSUN_FAULT", "BOSUN_FAULT_SECONDS"], asked);
    }

    [Fact]
    public void FromEnvironment_WithNeitherVariableSet_ActivatesNothing()
    {
        var settings = DebugFaultSettings.FromEnvironment(_ => null);

        Assert.False(settings.Any);
        Assert.Equal(DebugFault.None, settings.Faults);
        Assert.Empty(settings.Problems);
    }

    [Fact]
    public void None_IsInactive()
    {
        Assert.False(DebugFaultSettings.None.Any);
        Assert.Empty(DebugFaultSettings.None.Describe());
        Assert.Empty(DebugFaultSettings.None.Problems);
    }

    [Fact]
    public void Describe_NamesEveryActiveFault_AndSaysItIsNotReal()
    {
        var settings = DebugFaultSettings.Parse("supervisor-start,ui-exception,rcd-unavailable-at-resume", "90");

        var lines = settings.Describe();

        Assert.Equal(3, lines.Count);
        Assert.All(lines, l => Assert.StartsWith("FAULT INJECTION ACTIVE", l));
        Assert.All(lines, l => Assert.Contains("not a real fault", l));
        Assert.Single(lines, l => l.Contains("BOSUN_FAULT=supervisor-start"));
        Assert.Single(lines, l => l.Contains("BOSUN_FAULT=ui-exception"));
        Assert.Single(lines, l => l.Contains("BOSUN_FAULT=rcd-unavailable-at-resume") && l.Contains("90 s"));
    }

    [Fact]
    public void Describe_OnlyNamesTheFaultsThatAreActive()
    {
        var lines = DebugFaultSettings.Parse("ui-exception", seconds: null).Describe();

        var line = Assert.Single(lines);
        Assert.Contains("ui-exception", line);
    }

    [Fact]
    public void LogActive_WritesOneWarningPerActiveFault_AndOnePerProblem_AndNothingWhenInactive()
    {
        var sink = new CapturingSink();
        using var logger = new Serilog.LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(sink).CreateLogger();

        DebugFaultSettings.None.LogActive(logger);
        Assert.Empty(sink.Events);

        DebugFaultSettings.Parse("supervisor-start,rcd-unavailable-at-resume,bogus", "nope").LogActive(logger);

        Assert.Equal(4, sink.Events.Count);
        Assert.All(sink.Events, e => Assert.Equal(LogEventLevel.Warning, e.Level));
        var rendered = sink.Events.Select(e => e.RenderMessage()).ToList();
        Assert.Single(rendered, m => m.Contains("FAULT INJECTION ACTIVE (BOSUN_FAULT=supervisor-start)"));
        Assert.Single(rendered, m => m.Contains("FAULT INJECTION ACTIVE (BOSUN_FAULT=rcd-unavailable-at-resume)"));
        Assert.Single(rendered, m => m.Contains("'bogus'"));
        Assert.Single(rendered, m => m.Contains("'nope'"));
    }

    [Fact]
    public void SupervisorStartException_SaysWhatItIs()
    {
        var ex = DebugFaultSettings.CreateSupervisorStartException();

        Assert.Contains("BOSUN_FAULT=supervisor-start", ex.Message);
        Assert.Contains("not a real fault", ex.Message);
    }

    private sealed class CapturingSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];

        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }
}
#endif
