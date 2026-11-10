using System.Text.Json.Nodes;

namespace Bosun.Remote.Tools;

/// <summary>
/// What a tool hands back: text for the model to read, optional structured data, and whether the
/// call failed in an expected way. Mirrors MCP's call-tool result (content, structuredContent,
/// isError) without depending on it.
/// </summary>
public sealed record ToolResult
{
    private ToolResult(string text, JsonNode? structuredContent, bool isError)
    {
        Text = text;
        StructuredContent = structuredContent;
        IsError = isError;
    }

    /// <summary>Human- and model-readable result. Never null.</summary>
    public string Text { get; }

    /// <summary>Machine-readable result, when the tool has one.</summary>
    public JsonNode? StructuredContent { get; }

    /// <summary>True when the tool ran but could not do what was asked (bad input, refused path).</summary>
    public bool IsError { get; }

    public static ToolResult Success(string text, JsonNode? structuredContent = null)
        => new(text ?? throw new ArgumentNullException(nameof(text)), structuredContent, isError: false);

    public static ToolResult Error(string text)
        => new(text ?? throw new ArgumentNullException(nameof(text)), structuredContent: null, isError: true);
}
