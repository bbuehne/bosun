using System.IO;

namespace Bosun.Supervisor;

/// <summary>
/// Reads the Windows drive type of a mounted drive letter (bs-dtk). The seam exists so the
/// supervisor's post-mount I7 check can be tested with a fake: no default-suite test may touch a
/// real drive letter.
/// </summary>
/// <remarks>
/// Only the TYPE is ever read. Anything else on <see cref="DriveInfo"/> (<c>DriveFormat</c>,
/// <c>IsReady</c>, <c>TotalSize</c>, ...) touches the volume, and touching the volume of a wedged
/// SFTP mount is precisely the call that hangs (docs/ARCHITECTURE.md §4 rule 3a). The type comes
/// from <c>GetDriveType</c>, which is answered from the local mount-manager state without I/O to
/// the mounted file system.
/// </remarks>
public interface IDriveTypeReader
{
    /// <summary>
    /// The <see cref="DriveType"/> of <paramref name="drive"/> (a configured letter, e.g.
    /// <c>"P:"</c>). Reports <see cref="DriveType.NoRootDirectory"/> when no such drive exists.
    /// </summary>
    DriveType GetDriveType(string drive);
}

/// <summary>Production <see cref="IDriveTypeReader"/>: <c>new DriveInfo(drive).DriveType</c>.
/// Pure BCL, no P/Invoke.</summary>
public sealed class DriveInfoDriveTypeReader : IDriveTypeReader
{
    public DriveType GetDriveType(string drive) => new DriveInfo(drive).DriveType;
}
