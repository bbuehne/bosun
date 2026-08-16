using System.Runtime.CompilerServices;
using System.Windows;

// Lets Bosun.Tests reach a handful of `internal` pure-function seams directly (bs-8je: the
// GetExtendedTcpTable struct-layout parser and port byte-swap) without widening the public API
// surface just for testability. See src/Bosun/SessionMonitor/Interop/NativeTcpTable.cs.
[assembly: InternalsVisibleTo("Bosun.Tests")]

[assembly:ThemeInfo(
    ResourceDictionaryLocation.None,            //where theme specific resource dictionaries are located
                                                //(used if a resource is not found in the page,
                                                // or application resource dictionaries)
    ResourceDictionaryLocation.SourceAssembly   //where the generic resource dictionary is located
                                                //(used if a resource is not found in the page,
                                                // app, or any theme specific resource dictionaries)
)]
