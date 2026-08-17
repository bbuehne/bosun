using System.Collections;
using System.Reflection;
using Bosun.SystemEventIntegration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;

namespace Bosun.Tests.SystemEventIntegration;

/// <summary>
/// Proves <see cref="Win32SystemEventSource"/> subscribes to, and on <see cref="IDisposable.Dispose"/>
/// unsubscribes from, the REAL static <c>Microsoft.Win32.SystemEvents</c> class -- the one thing
/// <see cref="Win32SystemEventSourceTests"/> (which runs entirely through the
/// <c>FakeSystemEventRegistrar</c> seam) cannot itself demonstrate, because that seam is exactly
/// what stands between the default suite and the real static event.
/// </summary>
/// <remarks>
/// Marked <see cref="TestCategories.Integration"/> per CLAUDE.md/docs/OPERATIONS.md: this touches
/// <c>Microsoft.Win32.SystemEvents</c>'s real, process-wide, static subscriber list, which is
/// exactly the kind of real-system state the default suite must never reach.
/// <para>
/// Verification reflects into <c>SystemEvents</c>'s private <c>s_handlers</c>
/// dictionary (keyed by the private static <c>s_onPowerModeChangedEvent</c>/
/// <c>s_onSessionSwitchEvent</c> marker objects) to count subscribers before/after. That is
/// necessarily version-dependent BCL-internals reflection rather than a public API -- there is no
/// supported way to enumerate a static event's subscriber list from outside the declaring class.
/// If a future runtime renames these fields, this specific test will fail with a clear
/// <see cref="InvalidOperationException"/> naming the missing field rather than a cryptic
/// <see cref="NullReferenceException"/>; that is a "this test needs updating for a new runtime"
/// finding, not evidence of a regression in <see cref="Win32SystemEventSource"/> itself -- the
/// default-suite tests already prove the subscribe/unsubscribe discipline via the registrar spy,
/// independent of this reflection working at all.
/// </para>
/// </remarks>
[Trait(TestCategories.Category, TestCategories.Integration)]
public sealed class Win32SystemEventSourceIntegrationTests
{
    [Fact]
    public void Start_then_Dispose_leaves_the_real_SystemEvents_subscriber_count_unchanged()
    {
        var before = CountHandlers("s_onPowerModeChangedEvent");

        var source = new Win32SystemEventSource(TimeProvider.System, NullLogger<Win32SystemEventSource>.Instance);
        source.Start();

        var duringCount = CountHandlers("s_onPowerModeChangedEvent");
        Assert.Equal(before + 1, duringCount);

        source.Dispose();

        var afterCount = CountHandlers("s_onPowerModeChangedEvent");
        Assert.Equal(before, afterCount);
    }

    [Fact]
    public void Dispose_without_Start_does_not_throw_against_the_real_registrar()
    {
        var source = new Win32SystemEventSource(TimeProvider.System, NullLogger<Win32SystemEventSource>.Instance);

        source.Dispose(); // must not throw
        source.Dispose(); // idempotent -- must not throw either
    }

    /// <summary>Counts entries in <c>SystemEvents.s_handlers[SystemEvents.&lt;eventKeyFieldName&gt;]</c>
    /// via reflection -- see the class remarks for why this is the only way to observe this from
    /// outside <c>SystemEvents</c> itself.</summary>
    private static int CountHandlers(string eventKeyFieldName)
    {
        var type = typeof(SystemEvents);
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Static;

        var keyField = type.GetField(eventKeyFieldName, flags)
            ?? throw new InvalidOperationException(
                $"Microsoft.Win32.SystemEvents.{eventKeyFieldName} was not found by reflection -- the runtime's " +
                "internal layout has changed since this test was written; see the class remarks.");
        var key = keyField.GetValue(null)
            ?? throw new InvalidOperationException($"SystemEvents.{eventKeyFieldName} was null.");

        var handlersField = type.GetField("s_handlers", flags)
            ?? throw new InvalidOperationException(
                "Microsoft.Win32.SystemEvents.s_handlers was not found by reflection -- the runtime's internal " +
                "layout has changed since this test was written; see the class remarks.");

        if (handlersField.GetValue(null) is not IDictionary handlers || !handlers.Contains(key))
        {
            return 0;
        }

        return handlers[key] is ICollection list ? list.Count : 0;
    }
}
