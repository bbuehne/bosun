using System.Text.Json;

namespace Bosun.Remote.Tools;

/// <summary>
/// One remote tool (ADR-024, "what the remote tools may do" addendum). Each tool is one class
/// behind this interface, registered in one list (<see cref="RemoteToolCatalog"/>). Adding a tool is a
/// new class, one registration line, and its tests; nothing else changes.
///
/// The shape follows MCP's tool definition (name, description, JSON Schema for the input, a call
/// that returns text plus optional structured data) so the MCP layer (bs-4xw.10) can expose the
/// registry without translation, but nothing here depends on MCP.
///
/// Security contract for implementers:
/// <list type="bullet">
/// <item>A tool never runs a command, or any part of a command line, taken from the caller. Tools
/// start fixed programs with arguments they build themselves from validated values.</item>
/// <item>Every filesystem path a tool receives goes through <see cref="DevelopmentAreaGuard"/>
/// before it is used. Arguments are untrusted input from outside the machine.</item>
/// </list>
/// </summary>
public interface IRemoteTool
{
    /// <summary>
    /// The tool's wire name: 1 to 64 characters from <c>A-Z a-z 0-9 _ -</c>. Unique within a
    /// <see cref="ToolRegistry"/> (compared ignoring case).
    /// </summary>
    string Name { get; }

    /// <summary>What the tool does, in words the calling model can choose it by. Not empty.</summary>
    string Description { get; }

    /// <summary>
    /// JSON Schema for the tool's arguments. The root must be a JSON object, normally
    /// <c>{"type":"object","properties":{...}}</c>; a tool with no arguments uses an empty
    /// <c>properties</c> object.
    /// </summary>
    JsonElement InputSchema { get; }

    /// <summary>
    /// Runs the tool. <paramref name="arguments"/> is the caller's JSON object, parsed but not
    /// validated against <see cref="InputSchema"/> (the schema describes it to the caller; the tool
    /// must check what it uses). Expected failures, such as a rejected path, are returned as
    /// <see cref="ToolResult.Error"/>; exceptions are for bugs.
    /// </summary>
    Task<ToolResult> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken);
}
