using System.IO;
using System.Text.Json;
using Bosun.Configuration;
using Microsoft.Extensions.Logging;

namespace Bosun.Terminal;

/// <summary>
/// Default <see cref="IFragmentWriter"/> (E7c, bs-k41). Generates the fragment document via
/// <see cref="FragmentProfileGenerator"/>, validates its own JSON output, then writes it
/// atomically (temp file + move) to <see cref="FragmentWriterOptions.FragmentPath"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why atomic, and why self-validate.</b> Verified research (bs-3ir) found that Windows
/// Terminal silently SKIPS a malformed fragment file -- no toast, no error, no log entry anywhere.
/// A torn write (process killed mid-<c>File.WriteAllTextAsync</c>, disk full, etc.) would produce
/// exactly that: invalid JSON that looks, from Bosun's side, indistinguishable from "nothing went
/// wrong". Writing to a temp file and moving it into place means a torn write only ever corrupts
/// the temp file -- the previous, valid fragment stays in place until the move succeeds. Validating
/// the generated JSON with <see cref="JsonDocument.Parse(string)"/> before ever touching the
/// filesystem is the only other defence available, for the same reason: Terminal will never tell us
/// our own output was rejected.
/// </para>
/// <para>
/// <b>Invariant I5.</b> This class never reads, writes, or opens any path other than
/// <see cref="FragmentWriterOptions.FragmentPath"/> and its own temp file (which is derived from
/// that same path, in the same directory). The constructor rejects an <see cref="FragmentWriterOptions"/>
/// whose path contains <c>settings.json</c> as a defence-in-depth belt-and-suspenders check, on top
/// of the fact that nothing in this class ever constructs or discovers a path itself -- see
/// <c>FragmentWriterTests.WriteAsync_never_touches_a_path_containing_settingsJson</c>.
/// </para>
/// </remarks>
public sealed class FragmentWriter : IFragmentWriter
{
    private readonly string _fragmentPath;
    private readonly IFragmentFileSystem _fileSystem;
    private readonly ILogger<FragmentWriter> _logger;

    public FragmentWriter(FragmentWriterOptions options, IFragmentFileSystem fileSystem, ILogger<FragmentWriter> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(logger);

        if (options.FragmentPath.Contains("settings.json", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "FragmentWriter must never target a path containing settings.json (Invariant I5).",
                nameof(options));
        }

        _fragmentPath = options.FragmentPath;
        _fileSystem = fileSystem;
        _logger = logger;
    }

    public async Task WriteAsync(BosunConfig config, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);

        var document = FragmentProfileGenerator.CreateDocument(config.Hosts.Values);
        var json = FragmentSerializer.Serialize(document);

        ValidateBeforeWriting(json, document);

        var directory = Path.GetDirectoryName(_fragmentPath)
            ?? throw new InvalidOperationException($"Fragment path '{_fragmentPath}' has no containing directory.");
        _fileSystem.CreateDirectory(directory);

        var tempPath = Path.Combine(directory, $".{Path.GetFileName(_fragmentPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await _fileSystem.WriteAllTextAsync(tempPath, json, cancellationToken).ConfigureAwait(false);
            _fileSystem.Move(tempPath, _fragmentPath);
            _logger.LogInformation(
                "Wrote Windows Terminal fragment with {ProfileCount} profile(s) to {FragmentPath}",
                document.Profiles.Count, _fragmentPath);
        }
        finally
        {
            // Reached both on success (the move already renamed the temp file away, so this is a
            // no-op) and on failure (the move never happened, so the half-written temp file is
            // cleaned up rather than left behind next to the still-intact previous fragment).
            if (_fileSystem.FileExists(tempPath))
            {
                _fileSystem.DeleteFile(tempPath);
            }
        }
    }

    /// <summary>
    /// Parses our own serialised output back and checks it has the shape we just asked for. This
    /// is deliberately paranoid: see the class remarks on why Terminal gives us no other signal.
    /// </summary>
    private static void ValidateBeforeWriting(string json, FragmentDocument document)
    {
        using var validation = JsonDocument.Parse(json);

        if (validation.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("Generated fragment JSON is not a JSON object; refusing to write it.");
        }

        if (!validation.RootElement.TryGetProperty("profiles", out var profiles) || profiles.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("Generated fragment JSON has no \"profiles\" array; refusing to write it.");
        }

        if (profiles.GetArrayLength() != document.Profiles.Count)
        {
            throw new InvalidOperationException(
                $"Generated fragment JSON's profiles array has {profiles.GetArrayLength()} entries; expected {document.Profiles.Count}. Refusing to write it.");
        }

        if (validation.RootElement.TryGetProperty("schemes", out _))
        {
            // bs-289: Bosun never emits a schemes array. If this ever fires it means the model or
            // serializer regressed, not that the user did anything wrong.
            throw new InvalidOperationException("Generated fragment JSON unexpectedly contains a \"schemes\" array; refusing to write it.");
        }
    }
}
