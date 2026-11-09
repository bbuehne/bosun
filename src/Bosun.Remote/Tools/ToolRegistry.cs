using System.Text.Json;

namespace Bosun.Remote.Tools;

/// <summary>
/// The set of tools the remote surface exposes. Built once from an explicit list; there is no
/// scanning, no loading from disk, and no way to add a tool after construction (ADR-024: compile-time
/// modularity, not a plugin system).
/// </summary>
public sealed class ToolRegistry
{
    public const int MaxNameLength = 64;

    private readonly Dictionary<string, IRemoteTool> _byName;

    /// <exception cref="ArgumentException">
    /// A tool has an invalid name, an empty description, or a schema that is not a JSON object, or
    /// two tools share a name (ignoring case).
    /// </exception>
    public ToolRegistry(IEnumerable<IRemoteTool> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);

        _byName = new Dictionary<string, IRemoteTool>(StringComparer.OrdinalIgnoreCase);
        var ordered = new List<IRemoteTool>();

        foreach (var tool in tools)
        {
            if (tool is null)
            {
                throw new ArgumentException("A tool in the registration list is null.", nameof(tools));
            }

            Validate(tool);

            if (!_byName.TryAdd(tool.Name, tool))
            {
                throw new ArgumentException(
                    $"Tool name '{tool.Name}' is registered more than once (names are compared ignoring case).",
                    nameof(tools));
            }

            ordered.Add(tool);
        }

        // Read-only wrapper, not the list itself: a caller must not be able to cast Tools back to a
        // List and add a tool that lookup does not know.
        Tools = ordered.AsReadOnly();
    }

    /// <summary>The tools in registration order.</summary>
    public IReadOnlyList<IRemoteTool> Tools { get; }

    /// <summary>Finds a tool by name, ignoring case. Returns false for an unknown name.</summary>
    public bool TryGet(string? name, out IRemoteTool tool)
    {
        if (name is not null && _byName.TryGetValue(name, out var found))
        {
            tool = found;
            return true;
        }

        tool = null!;
        return false;
    }

    /// <summary>
    /// Runs the named tool. An unknown name, or arguments that are not a JSON object, come back as
    /// <see cref="ToolResult.Error"/> rather than an exception: both are caller mistakes, not bugs.
    /// </summary>
    public Task<ToolResult> InvokeAsync(string? name, JsonElement arguments, CancellationToken cancellationToken)
    {
        if (!TryGet(name, out var tool))
        {
            return Task.FromResult(ToolResult.Error($"Unknown tool '{name}'."));
        }

        if (arguments.ValueKind != JsonValueKind.Object)
        {
            return Task.FromResult(ToolResult.Error($"Arguments for '{tool.Name}' must be a JSON object."));
        }

        return tool.InvokeAsync(arguments, cancellationToken);
    }

    private static void Validate(IRemoteTool tool)
    {
        var name = tool.Name;
        if (string.IsNullOrEmpty(name) || name.Length > MaxNameLength || !name.All(IsNameChar))
        {
            throw new ArgumentException(
                $"Tool name '{name}' is invalid: use 1 to {MaxNameLength} characters from A-Z, a-z, 0-9, '_' and '-'.",
                nameof(tool));
        }

        if (string.IsNullOrWhiteSpace(tool.Description))
        {
            throw new ArgumentException($"Tool '{name}' has no description.", nameof(tool));
        }

        if (tool.InputSchema.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException($"Tool '{name}' must declare its input schema as a JSON object.", nameof(tool));
        }
    }

    private static bool IsNameChar(char c)
        => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_' or '-';
}
