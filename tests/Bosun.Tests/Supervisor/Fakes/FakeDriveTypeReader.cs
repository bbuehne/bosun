using Bosun.Supervisor;

namespace Bosun.Tests.Supervisor.Fakes;

/// <summary>
/// Fake <see cref="IDriveTypeReader"/> (bs-dtk). Reports <see cref="DriveType.Network"/> -- the
/// verified good baseline -- for every drive unless told otherwise, so every pre-existing
/// supervisor test is unaffected by the post-mount I7 check. Never touches a real drive letter.
/// </summary>
internal sealed class FakeDriveTypeReader : IDriveTypeReader
{
    private readonly Dictionary<string, DriveType> overrides = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Exception> failures = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every drive the supervisor asked about, in order.</summary>
    public List<string> Reads { get; } = [];

    public FakeDriveTypeReader Reports(string drive, DriveType type)
    {
        overrides[drive] = type;
        return this;
    }

    public FakeDriveTypeReader Throws(string drive, Exception exception)
    {
        failures[drive] = exception;
        return this;
    }

    /// <summary>Back to the default (Network) for <paramref name="drive"/>.</summary>
    public FakeDriveTypeReader Clear(string drive)
    {
        overrides.Remove(drive);
        failures.Remove(drive);
        return this;
    }

    public DriveType GetDriveType(string drive)
    {
        Reads.Add(drive);
        if (failures.TryGetValue(drive, out var failure))
        {
            throw failure;
        }

        return overrides.TryGetValue(drive, out var type) ? type : DriveType.Network;
    }
}
