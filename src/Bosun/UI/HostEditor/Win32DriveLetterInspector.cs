using System.IO;

namespace Bosun.UI.HostEditor;

/// <summary>
/// Real <see cref="IDriveLetterInspector"/>, reading the machine's mounted volumes.
/// </summary>
/// <remarks>
/// Reads live machine state, so it is never used by the default test suite -- see the interface.
/// Note this deliberately reports what is in use *right now*, including a drive Bosun itself has
/// mounted; deciding what that means for a given host is
/// <see cref="HostEditorController.BuildDriveLetterOptions"/>'s job, not this class's.
/// </remarks>
public sealed class Win32DriveLetterInspector : IDriveLetterInspector
{
    public IReadOnlySet<string> InUseLetters()
    {
        var letters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var drive in DriveInfo.GetDrives())
        {
            var name = drive.Name;
            if (name.Length > 0 && char.IsLetter(name[0]))
            {
                letters.Add(char.ToUpperInvariant(name[0]).ToString());
            }
        }

        return letters;
    }
}
