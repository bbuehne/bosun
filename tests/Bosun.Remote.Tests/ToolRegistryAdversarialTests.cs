using System.Reflection;
using System.Text.Json;
using Bosun.Remote.Tools;

namespace Bosun.Remote.Tests;

/// <summary>
/// Independent adversarial pass on the tool registry and the project's "no caller-supplied command"
/// tripwire (bs-4xw.7, ADR-024 "modular" addendum). The registry is the one place a remote caller's
/// tool name is turned into code to run, so lookups must not alias and odd arguments must not crash.
/// </summary>
public sealed class ToolRegistryAdversarialTests
{
    private static readonly JsonElement ObjectSchema =
        JsonDocument.Parse("""{"type":"object","properties":{}}""").RootElement.Clone();

    private sealed class CountingTool(string name) : IRemoteTool
    {
        public string Name { get; } = name;
        public string Description => "counts calls";
        public JsonElement InputSchema => ObjectSchema;
        public int Calls { get; private set; }

        public Task<ToolResult> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(ToolResult.Success("ran " + Name));
        }
    }

    // ---- Unicode case folding --------------------------------------------------------------

    /// <summary>
    /// Bug caught: a switch to culture-aware or full Unicode case folding (InvariantCultureIgnoreCase,
    /// ToUpper with a Turkish culture) that lets a non-ASCII spelling reach an ASCII tool. Tool names
    /// are ASCII by contract, so no non-ASCII input can name one.
    /// </summary>
    [Theory]
    [InlineData("kill", "Kill")]          // KELVIN SIGN folds to k under full case folding
    [InlineData("shell", "ſhell")]        // LATIN SMALL LETTER LONG S uppercases to S
    [InlineData("list", "lıst")]          // dotless i uppercases to I
    [InlineData("id", "İd")]              // dotted capital I lowercases to i in tr-TR
    [InlineData("list", "LİST")]
    [InlineData("open", "ｏpen")]          // fullwidth o
    [InlineData("a-b", "a‐b")]            // hyphen vs hyphen-minus
    [InlineData("a_b", "a＿b")]            // fullwidth low line
    public async Task A_non_ascii_lookalike_never_finds_an_ascii_tool(string registered, string lookalike)
    {
        var tool = new CountingTool(registered);
        var registry = new ToolRegistry([tool]);

        Assert.False(registry.TryGet(lookalike, out _));
        var result = await registry.InvokeAsync(lookalike, ObjectSchema, CancellationToken.None);
        Assert.True(result.IsError);
        Assert.Equal(0, tool.Calls);
    }

    [Fact]
    public void A_non_ascii_lookalike_still_fails_under_a_turkish_current_culture()
    {
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("tr-TR");
            var registry = new ToolRegistry([new CountingTool("list")]);

            Assert.True(registry.TryGet("LIST", out _));
            Assert.False(registry.TryGet("LİST", out _));
            Assert.False(registry.TryGet("lıst", out _));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previous;
        }
    }

    [Theory]
    [InlineData("Kill")]
    [InlineData("İd")]
    [InlineData("lıst")]
    [InlineData("ſhell")]
    public void A_tool_whose_name_contains_a_case_folding_lookalike_cannot_be_registered(string name)
    {
        Assert.Throws<ArgumentException>(() => new ToolRegistry([new CountingTool(name)]));
    }

    /// <summary>Bug caught: lookups that trim or normalise the caller's name, creating aliases.</summary>
    [Theory]
    [InlineData("echo ")]
    [InlineData(" echo")]
    [InlineData("echo\0")]
    [InlineData("echo\n")]
    [InlineData("ec ho")]
    [InlineData("ech")]
    [InlineData("echoo")]
    public void A_lookup_must_match_the_whole_name_exactly_apart_from_ascii_case(string name)
    {
        var registry = new ToolRegistry([new CountingTool("echo")]);

        Assert.False(registry.TryGet(name, out _));
    }

    // ---- Argument edge cases ---------------------------------------------------------------

    /// <summary>
    /// Bug caught: an uninitialised <see cref="JsonElement"/> (what a deserialiser hands over when
    /// the "arguments" member is missing) throwing InvalidOperationException out of the registry, or
    /// reaching a tool that then reads properties from it.
    /// </summary>
    [Fact]
    public async Task Undefined_arguments_are_an_error_result_and_the_tool_does_not_run()
    {
        var tool = new CountingTool("echo");
        var registry = new ToolRegistry([tool]);

        var result = await registry.InvokeAsync("echo", default, CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal(0, tool.Calls);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("true")]
    [InlineData("false")]
    public async Task Scalar_arguments_are_an_error_result_and_the_tool_does_not_run(string json)
    {
        var tool = new CountingTool("echo");
        var registry = new ToolRegistry([tool]);

        var result = await registry.InvokeAsync("echo", JsonDocument.Parse(json).RootElement.Clone(), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal(0, tool.Calls);
    }

    [Fact]
    public async Task An_unknown_name_with_undefined_arguments_is_an_error_result_not_an_exception()
    {
        var registry = new ToolRegistry([new CountingTool("echo")]);

        var unknown = await registry.InvokeAsync("nope", default, CancellationToken.None);
        var nullName = await registry.InvokeAsync(null, default, CancellationToken.None);

        Assert.True(unknown.IsError);
        Assert.True(nullName.IsError);
    }

    [Fact]
    public async Task Invoking_with_a_differently_cased_name_runs_the_one_registered_tool_once()
    {
        var a = new CountingTool("Open_Project");
        var b = new CountingTool("list_projects");
        var registry = new ToolRegistry([a, b]);

        var result = await registry.InvokeAsync("OPEN_PROJECT", ObjectSchema, CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal(1, a.Calls);
        Assert.Equal(0, b.Calls);
    }

    // ---- The registration list is closed after construction ---------------------------------

    /// <summary>
    /// ToolRegistry's own documentation: "no way to add a tool after construction". Bug caught:
    /// exposing the internal list, so code holding the registry can cast <see cref="ToolRegistry.Tools"/>
    /// back to a list and append a tool. The appended tool would then be advertised (if the MCP layer
    /// lists <c>Tools</c>) while lookup, which uses a separate dictionary, does not know it.
    /// </summary>
    [Fact]
    public void The_listed_tools_cannot_be_mutated_through_a_cast()
    {
        var registry = new ToolRegistry([new CountingTool("a")]);

        Assert.False(registry.Tools is ICollection<IRemoteTool> { IsReadOnly: false },
            "Tools is a mutable collection; a caller can add or remove tools after construction.");
    }

    // ---- No caller-supplied command: a tripwire that P/Invoke cannot walk around -------------

    /// <summary>
    /// The existing tripwire (<see cref="ProjectBoundaryTests"/>) catches a reference to
    /// <c>System.Diagnostics.Process</c>. A pseudo-console launcher (bs-4xw.8) has to call
    /// <c>CreateProcessW</c> through P/Invoke instead, which that tripwire does not see. This one
    /// does, so the launch type has to be named deliberately when it arrives.
    /// </summary>
    [Fact]
    public void The_project_declares_no_native_process_launching_entry_point()
    {
        string[] launchers =
        [
            "CreateProcess", "CreateProcessW", "CreateProcessA", "CreateProcessAsUser", "CreateProcessAsUserW",
            "CreateProcessWithLogonW", "CreateProcessWithTokenW", "ShellExecute", "ShellExecuteW",
            "ShellExecuteEx", "ShellExecuteExW", "WinExec", "system", "_wsystem", "_popen", "_wpopen",
        ];
        const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static |
                                 BindingFlags.Instance | BindingFlags.DeclaredOnly;

        var found = typeof(DevelopmentAreaGuard).Assembly.GetTypes()
            .SelectMany(t => t.GetMethods(All))
            .Where(m => (m.Attributes & MethodAttributes.PinvokeImpl) != 0)
            .Select(m => (Method: m, Entry: m.GetCustomAttribute<System.Runtime.InteropServices.DllImportAttribute>()?.EntryPoint ?? m.Name))
            .Where(x => launchers.Contains(x.Entry, StringComparer.OrdinalIgnoreCase))
            .Select(x => $"{x.Method.DeclaringType?.FullName}.{x.Method.Name} -> {x.Entry}")
            .ToArray();

        Assert.Empty(found);
    }

    [Fact]
    public void The_project_does_not_reference_wmi_which_can_also_create_processes()
    {
        var referenced = typeof(DevelopmentAreaGuard).Assembly.GetReferencedAssemblies().Select(a => a.Name ?? "");

        Assert.DoesNotContain(referenced, n => n.StartsWith("System.Management", StringComparison.OrdinalIgnoreCase)
                                               || n.StartsWith("Microsoft.Management.Infrastructure", StringComparison.OrdinalIgnoreCase));
    }
}
