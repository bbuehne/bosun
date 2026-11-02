#if DEBUG
using System.Globalization;

namespace Bosun.Diagnostics.FaultInjection;

/// <summary>
/// The faults a Debug build can be told to inject (bs-uzk). Flags, because several can be active
/// at once (<c>BOSUN_FAULT=supervisor-start,ui-exception</c>).
/// </summary>
[Flags]
public enum DebugFault
{
    None = 0,

    /// <summary>The mount supervisor cannot be built or started (docs/OPERATIONS.md T12; bs-4js).</summary>
    SupervisorStart = 1,

    /// <summary>One unhandled exception on the UI dispatcher (T13; bs-twz).</summary>
    UiException = 2,

    /// <summary>After a resume, rclone's rcd stops answering for a bounded period (T9 part E; ADR-022).</summary>
    RcdUnavailableAtResume = 4,
}

/// <summary>
/// Which faults are active, parsed from the <c>BOSUN_FAULT</c> environment variable (bs-uzk,
/// maintainer decision 2026-11-02: option A). Exists only in Debug builds.
/// </summary>
/// <remarks>
/// <para>
/// <b>This whole file is inside <c>#if DEBUG</c>, deliberately.</b> A Release build contains no
/// <c>BOSUN_FAULT</c> string, no parser and no hook type, so there is nothing for a stray
/// environment variable to switch on. The few call sites in <c>App</c>, <c>BosunHostFactory</c>
/// and <c>BosunHostOptions</c> are wrapped in <c>#if DEBUG</c> as well. The Release check is
/// mechanical: build <c>-c Release</c> and search the assembly for the variable's name (bs-uzk
/// report; docs/OPERATIONS.md T12).
/// </para>
/// <para>
/// <b>Read once, in <c>App</c> only.</b> The settings travel in
/// <see cref="Bosun.Hosting.BosunHostOptions.DebugFaults"/>, which defaults to
/// <see cref="None"/>. Nothing under test reads the environment, so a maintainer who has the
/// variable set in the shell where he runs <c>dotnet test</c> does not change what the tests do.
/// </para>
/// </remarks>
public sealed class DebugFaultSettings
{
    /// <summary>The environment variable that selects faults.</summary>
    public const string VariableName = "BOSUN_FAULT";

    /// <summary>The environment variable that sets how long <see cref="DebugFault.RcdUnavailableAtResume"/>
    /// lasts after each resume, in seconds.</summary>
    public const string SecondsVariableName = "BOSUN_FAULT_SECONDS";

    public const string SupervisorStartName = "supervisor-start";
    public const string UiExceptionName = "ui-exception";
    public const string RcdUnavailableAtResumeName = "rcd-unavailable-at-resume";

    /// <summary>120 seconds: shorter than the 10-minute <c>RemountRecordWaitLimit</c>, so by default the
    /// remount record is still alive when rcd comes back and the drive remounts. To see the record
    /// expire instead, set <see cref="SecondsVariableName"/> above 600 (docs/OPERATIONS.md T9 part E).</summary>
    public static readonly TimeSpan DefaultRcdUnavailableDuration = TimeSpan.FromSeconds(120);

    private DebugFaultSettings(DebugFault faults, TimeSpan rcdUnavailableDuration, IReadOnlyList<string> problems)
    {
        Faults = faults;
        RcdUnavailableDuration = rcdUnavailableDuration;
        Problems = problems;
    }

    /// <summary>No fault active. What every test, and every Debug run without the variable, gets.</summary>
    public static DebugFaultSettings None { get; } = new(DebugFault.None, DefaultRcdUnavailableDuration, []);

    public DebugFault Faults { get; }

    /// <summary>How long rcd reports "not answering" after each resume while
    /// <see cref="DebugFault.RcdUnavailableAtResume"/> is active.</summary>
    public TimeSpan RcdUnavailableDuration { get; }

    /// <summary>Values that were not understood (an unknown fault name, a bad seconds value). Each is
    /// ignored, and each is logged as a Warning so a typo is visible rather than silently inert.</summary>
    public IReadOnlyList<string> Problems { get; }

    public bool Any => Faults != DebugFault.None;

    public bool Has(DebugFault fault) => fault != DebugFault.None && (Faults & fault) == fault;

    /// <summary>Reads <see cref="VariableName"/> and <see cref="SecondsVariableName"/>. <paramref name="getVariable"/>
    /// is for tests; production passes nothing.</summary>
    public static DebugFaultSettings FromEnvironment(Func<string, string?>? getVariable = null)
    {
        getVariable ??= Environment.GetEnvironmentVariable;
        return Parse(getVariable(VariableName), getVariable(SecondsVariableName));
    }

    /// <summary>
    /// Parses a comma-separated list of fault names (case-insensitive, whitespace ignored) and an
    /// optional seconds value. Unknown names and an unusable seconds value are recorded in
    /// <see cref="Problems"/> and otherwise ignored; this never throws.
    /// </summary>
    public static DebugFaultSettings Parse(string? faults, string? seconds)
    {
        var problems = new List<string>();
        var parsed = DebugFault.None;

        foreach (var raw in (faults ?? string.Empty).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case SupervisorStartName:
                    parsed |= DebugFault.SupervisorStart;
                    break;
                case UiExceptionName:
                    parsed |= DebugFault.UiException;
                    break;
                case RcdUnavailableAtResumeName:
                    parsed |= DebugFault.RcdUnavailableAtResume;
                    break;
                default:
                    problems.Add(
                        $"{VariableName} contains '{raw}', which is not a fault name; it is ignored. Known names: " +
                        $"{SupervisorStartName}, {UiExceptionName}, {RcdUnavailableAtResumeName}.");
                    break;
            }
        }

        var duration = DefaultRcdUnavailableDuration;
        if (!string.IsNullOrWhiteSpace(seconds))
        {
            if (double.TryParse(seconds, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                && double.IsFinite(value) && value > 0 && value <= TimeSpan.FromDays(1).TotalSeconds)
            {
                duration = TimeSpan.FromSeconds(value);
            }
            else
            {
                problems.Add(
                    $"{SecondsVariableName} is '{seconds}', which is not a number of seconds between 0 and 86400; " +
                    $"the default of {DefaultRcdUnavailableDuration.TotalSeconds:0} s is used.");
            }
        }

        return new DebugFaultSettings(parsed, duration, problems);
    }

    /// <summary>The exception <c>BosunHostFactory</c>'s <c>MountSupervisor</c> registration throws under
    /// <see cref="DebugFault.SupervisorStart"/>. Its message is what the red banner shows after
    /// "The mount supervisor failed to start: ".</summary>
    public static InvalidOperationException CreateSupervisorStartException() =>
        new($"{VariableName}={SupervisorStartName}: the mount supervisor was made to fail on purpose (injected, not a real fault)");

    /// <summary>
    /// One sentence per active fault, each beginning "FAULT INJECTION ACTIVE" so a person reading the
    /// log can search for it, and each saying the failure is not real.
    /// </summary>
    public IReadOnlyList<string> Describe()
    {
        var lines = new List<string>();
        if (Has(DebugFault.SupervisorStart))
        {
            lines.Add(
                $"FAULT INJECTION ACTIVE ({VariableName}={SupervisorStartName}): the mount supervisor will fail to " +
                "start on purpose. This is a Debug-build test hook, not a real fault.");
        }

        if (Has(DebugFault.UiException))
        {
            lines.Add(
                $"FAULT INJECTION ACTIVE ({VariableName}={UiExceptionName}): one unhandled exception will be thrown on " +
                "the UI dispatcher a few seconds after the UI is up, on purpose. This is a Debug-build test hook, " +
                "not a real fault.");
        }

        if (Has(DebugFault.RcdUnavailableAtResume))
        {
            lines.Add(
                $"FAULT INJECTION ACTIVE ({VariableName}={RcdUnavailableAtResumeName}): after each system resume, " +
                "rclone rcd will be reported as not answering core/version, and mount/mount will fail, for " +
                $"{RcdUnavailableDuration.TotalSeconds:0.##} s ({SecondsVariableName}), on purpose. rcd itself is " +
                "unaffected. This is a Debug-build test hook, not a real fault.");
        }

        return lines;
    }

    /// <summary>Writes one Warning per active fault and one per unusable value. Called once, when the
    /// host is built, before anything the faults affect can log.</summary>
    public void LogActive(Serilog.ILogger logger)
    {
        foreach (var line in Describe())
        {
            logger.Warning("{FaultInjection}", line);
        }

        foreach (var problem in Problems)
        {
            logger.Warning("{FaultInjectionProblem}", problem);
        }
    }
}
#endif
