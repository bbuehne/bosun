namespace Bosun.Import;

/// <summary>
/// Extracts what it confidently can from a Bitvise/Tunnelier profile's bytes (bs-ww9.9, ADR-019).
/// Behind an interface, same seam pattern as every other real-input boundary in this codebase
/// (<see cref="Bosun.UI.HostEditor.IIdentityFilePicker"/>, <see cref="Bosun.UI.HostEditor.IDriveLetterInspector"/>)
/// -- so the extraction heuristic is unit-testable against small hand-built byte arrays, never
/// against a real profile on disk.
/// </summary>
public interface IBitviseProfileParser
{
    /// <summary>Parses raw profile bytes. Never throws for input it cannot understand -- truncated
    /// files, absurd length prefixes, non-ASCII content, and empty input all come back as a
    /// <see cref="BitviseImportResult"/> with <see cref="BitviseImportResult.Succeeded"/> false and
    /// an explanatory <see cref="BitviseImportResult.Error"/>.</summary>
    BitviseImportResult Parse(byte[] data);
}
