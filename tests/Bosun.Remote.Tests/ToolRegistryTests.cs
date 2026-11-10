using System.Text.Json;
using Bosun.Remote.Tools;

namespace Bosun.Remote.Tests;

public sealed class ToolRegistryTests
{
    private static readonly JsonElement ObjectSchema = JsonDocument.Parse("""{"type":"object","properties":{}}""").RootElement.Clone();

    private sealed class FakeTool(string name, string description = "does a thing", JsonElement? schema = null) : IRemoteTool
    {
        public string Name { get; } = name;
        public string Description { get; } = description;
        public JsonElement InputSchema { get; } = schema ?? ObjectSchema;

        public int Calls { get; private set; }
        public JsonElement LastArguments { get; private set; }
        public CancellationToken LastToken { get; private set; }

        public Task<ToolResult> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken)
        {
            Calls++;
            LastArguments = arguments;
            LastToken = cancellationToken;
            return Task.FromResult(ToolResult.Success("ran " + Name));
        }
    }

    [Fact]
    public void Registered_tools_are_listed_in_registration_order()
    {
        var a = new FakeTool("zeta");
        var b = new FakeTool("alpha");

        var registry = new ToolRegistry([a, b]);

        Assert.Equal([a, b], registry.Tools);
    }

    [Fact]
    public void An_empty_registration_list_is_valid()
    {
        var registry = new ToolRegistry([]);

        Assert.Empty(registry.Tools);
        Assert.False(registry.TryGet("anything", out _));
    }

    [Fact]
    public void Lookup_by_name_finds_the_tool()
    {
        var tool = new FakeTool("list_projects");
        var registry = new ToolRegistry([new FakeTool("other"), tool]);

        Assert.True(registry.TryGet("list_projects", out var found));
        Assert.Same(tool, found);
    }

    [Fact]
    public void Lookup_ignores_case()
    {
        var tool = new FakeTool("List_Projects");
        var registry = new ToolRegistry([tool]);

        Assert.True(registry.TryGet("list_projects", out var found));
        Assert.Same(tool, found);
    }

    [Theory]
    [InlineData("nope")]
    [InlineData("")]
    [InlineData(null)]
    public void Lookup_of_an_unknown_name_fails(string? name)
    {
        var registry = new ToolRegistry([new FakeTool("list_projects")]);

        Assert.False(registry.TryGet(name, out _));
    }

    [Fact]
    public void Duplicate_names_are_rejected()
    {
        var ex = Assert.Throws<ArgumentException>(() => new ToolRegistry([new FakeTool("same"), new FakeTool("same")]));

        Assert.Contains("same", ex.Message);
    }

    [Fact]
    public void Duplicate_names_differing_only_in_case_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => new ToolRegistry([new FakeTool("Same"), new FakeTool("sAME")]));
    }

    [Fact]
    public void A_null_entry_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => new ToolRegistry([new FakeTool("a"), null!]));
    }

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("dot.name")]
    [InlineData("slash/name")]
    [InlineData("café")]
    [InlineData("emoji\U0001F600")]
    public void Invalid_names_are_rejected(string name)
    {
        Assert.Throws<ArgumentException>(() => new ToolRegistry([new FakeTool(name)]));
    }

    [Fact]
    public void A_name_over_the_length_limit_is_rejected_and_one_at_the_limit_is_accepted()
    {
        Assert.Throws<ArgumentException>(() => new ToolRegistry([new FakeTool(new string('a', ToolRegistry.MaxNameLength + 1))]));
        _ = new ToolRegistry([new FakeTool(new string('a', ToolRegistry.MaxNameLength))]);
    }

    [Theory]
    [InlineData("open-project")]
    [InlineData("open_project_2")]
    [InlineData("A")]
    public void Valid_names_are_accepted(string name)
    {
        var registry = new ToolRegistry([new FakeTool(name)]);

        Assert.True(registry.TryGet(name, out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_tool_without_a_description_is_rejected(string description)
    {
        Assert.Throws<ArgumentException>(() => new ToolRegistry([new FakeTool("a", description)]));
    }

    [Fact]
    public void A_schema_that_is_not_a_json_object_is_rejected()
    {
        var array = JsonDocument.Parse("[]").RootElement.Clone();

        Assert.Throws<ArgumentException>(() => new ToolRegistry([new FakeTool("a", schema: array)]));
        Assert.Throws<ArgumentException>(() => new ToolRegistry([new FakeTool("a", schema: default(JsonElement))]));
    }

    [Fact]
    public async Task Invoke_passes_arguments_and_cancellation_token_to_the_tool_and_returns_its_result()
    {
        var tool = new FakeTool("echo");
        var registry = new ToolRegistry([tool]);
        var args = JsonDocument.Parse("""{"project":"demo"}""").RootElement.Clone();
        using var cts = new CancellationTokenSource();

        var result = await registry.InvokeAsync("echo", args, cts.Token);

        Assert.False(result.IsError);
        Assert.Equal("ran echo", result.Text);
        Assert.Equal(1, tool.Calls);
        Assert.Equal("demo", tool.LastArguments.GetProperty("project").GetString());
        Assert.Equal(cts.Token, tool.LastToken);
    }

    [Fact]
    public async Task Invoking_an_unknown_tool_returns_an_error_result_and_runs_nothing()
    {
        var tool = new FakeTool("echo");
        var registry = new ToolRegistry([tool]);

        var result = await registry.InvokeAsync("missing", ObjectSchema, CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("missing", result.Text);
        Assert.Equal(0, tool.Calls);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("\"text\"")]
    [InlineData("null")]
    public async Task Arguments_that_are_not_an_object_are_refused_before_the_tool_runs(string json)
    {
        var tool = new FakeTool("echo");
        var registry = new ToolRegistry([tool]);
        var args = JsonDocument.Parse(json).RootElement.Clone();

        var result = await registry.InvokeAsync("echo", args, CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal(0, tool.Calls);
    }

    [Fact]
    public void Tool_result_factories_set_the_error_flag()
    {
        Assert.False(ToolResult.Success("ok").IsError);
        Assert.True(ToolResult.Error("bad").IsError);
        Assert.Null(ToolResult.Error("bad").StructuredContent);
    }

    [Fact]
    public void The_catalog_builds_a_registry_for_a_guard()
    {
        using var area = new TempArea();

        var registry = RemoteToolCatalog.Create(area.Guard());

        // Round 1 tools (bs-4xw.11) are not registered yet; when they are, update this to name them.
        Assert.Empty(registry.Tools);
    }

    [Fact]
    public void The_catalog_requires_a_guard()
    {
        Assert.Throws<ArgumentNullException>(() => RemoteToolCatalog.Create(null!));
    }
}
