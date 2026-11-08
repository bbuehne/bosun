using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ConPtyProbe;

/// <summary>
/// ADR-024 spike (bs-4xw.3): run Claude Code in a pseudo console this program owns, the way Bosun
/// would, and record what happens. Two modes:
/// <list type="bullet">
/// <item><c>--mirror</c>: shows Claude Code's screen in this console and forwards the keyboard to it,
/// so the maintainer can watch and answer prompts by hand. Ctrl+] then q quits.</item>
/// <item><c>--headless</c> (default): shows only detected events. Answers prompts automatically when
/// <c>--auto-trust</c> / <c>--auto-rc</c> are given, or by typed commands (<c>help</c> lists them).
/// This is the shape Bosun would have: no window, prompts relayed.</item>
/// </list>
/// Logs go to %LOCALAPPDATA%\BosunSpike\conpty\&lt;time&gt;\ (events.jsonl, raw.log). The probe
/// never edits Claude Code's settings; answering a prompt is the same keystroke a person would type.
/// </summary>
internal static class Program
{
    private const string Usage = """
        ConPtyProbe --dir <folder> [options]
          --dir <folder>        Folder to start Claude Code in (created if missing).
          --name <name>         Remote Control session name (default bosun-spike).
          --resume <id>         Resume this conversation instead of starting a new one.
          --extra "<args>"      Extra arguments appended to the claude command line.
          --mirror              Show Claude Code's screen here and forward the keyboard.
          --headless            Events only (default).
          --auto-trust[=keys]   Answer the trust prompt automatically (default keys: Enter).
          --auto-rc[=keys]      Answer "Enable Remote Control?" automatically (default keys: y).
          --start-delay <s>     Wait this many seconds before starting (time to lock the workstation).
          --cols <n> --rows <n> Pseudo console size in headless mode (default 120 x 40).
          --cmd "<command line>" Run this instead of claude (self-test of the probe only).
        Keys accept \r (Enter), \e (Esc) and \n.
        """;

    private static int Main(string[] args)
    {
        Options options;
        try
        {
            options = Options.Parse(args);
        }
        catch (ArgumentException e)
        {
            Console.Error.WriteLine(e.Message);
            Console.Error.WriteLine(Usage);
            return 2;
        }

        var runDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BosunSpike", "conpty", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(runDir);
        Directory.CreateDirectory(options.Dir);

        Console.WriteLine($"Run folder (send this back): {runDir}");
        using var log = new EventLog(Path.Combine(runDir, "events.jsonl"), options.Mirror);

        if (options.StartDelaySeconds > 0)
        {
            Console.WriteLine($"Starting in {options.StartDelaySeconds} s. Lock the workstation now (Win+L) if this is the locked test.");
            Thread.Sleep(TimeSpan.FromSeconds(options.StartDelaySeconds));
        }

        var claude = options.Command is not null
            ? null
            : FindOnPath("claude.exe") ?? throw new FileNotFoundException("claude.exe was not found on PATH.");
        var commandLine = new StringBuilder(options.Command ?? Quote(claude!));
        // A self-test command (--cmd) is used exactly as given.
        if (options.Command is null && options.Resume is not null)
        {
            commandLine.Append(" --resume ").Append(Quote(options.Resume));
        }

        if (options.Command is null)
        {
            commandLine.Append(" --remote-control ").Append(Quote(options.Name));
        }

        if (options.Extra is not null)
        {
            commandLine.Append(' ').Append(options.Extra);
        }

        // A probe started from inside a Claude Code session would otherwise pass this on, and the child
        // would think it is nested.
        Environment.SetEnvironmentVariable("CLAUDECODE", null);

        var size = options.Mirror
            ? new Native.Coord { X = (short)Console.WindowWidth, Y = (short)Console.WindowHeight }
            : new Native.Coord { X = options.Cols, Y = options.Rows };

        log.Write("start", $"cmd={commandLine} dir={options.Dir} size={size.X}x{size.Y} mode={(options.Mirror ? "mirror" : "headless")}");

        using var session = PseudoConsoleSession.Start(commandLine, options.Dir, size);
        log.Write("child_started", $"pid={session.ProcessId}");

        using var raw = new FileStream(Path.Combine(runDir, "raw.log"), FileMode.Create, FileAccess.Write, FileShare.Read);
        var stdout = options.Mirror ? Console.OpenStandardOutput() : null;
        var restoreConsole = options.Mirror ? EnterRawConsole() : null;

        var detector = new Detector(log, prompt =>
        {
            var keys = prompt switch
            {
                "trust_prompt" => options.AutoTrust,
                "enable_rc_prompt" => options.AutoRc,
                _ => null,
            };
            if (keys is null)
            {
                return false;
            }

            Thread.Sleep(500); // let the prompt finish drawing before answering
            session.Send(keys);
            log.Write("answered", $"{prompt} with {Visible(keys)}");
            return true;
        });

        var reader = new Thread(() =>
        {
            var buffer = new byte[8192];
            try
            {
                int n;
                while ((n = session.Output.Read(buffer, 0, buffer.Length)) > 0)
                {
                    raw.Write(buffer, 0, n);
                    raw.Flush();
                    stdout?.Write(buffer, 0, n);
                    stdout?.Flush();
                    detector.Feed(buffer, n);
                }
            }
            catch (IOException)
            {
                // The pseudo console closed.
            }
        })
        { IsBackground = true, Name = "pty-output" };
        reader.Start();

        var input = new Thread(() =>
        {
            if (options.Mirror)
            {
                ForwardKeyboard(session, log);
            }
            else
            {
                ReadCommands(session, log);
            }
        })
        { IsBackground = true, Name = "input" };
        input.Start();

        session.WaitForExitOrQuit();
        log.Write("child_exited", session.ExitDescription);
        session.Dispose();
        reader.Join(TimeSpan.FromSeconds(5));
        restoreConsole?.Invoke();
        Console.WriteLine();
        Console.WriteLine($"Done. Logs: {runDir}");
        return 0;
    }

    private static void ForwardKeyboard(PseudoConsoleSession session, EventLog log)
    {
        var stdin = Console.OpenStandardInput();
        var buffer = new byte[256];
        var escapePending = false;
        while (!session.HasExited)
        {
            var n = stdin.Read(buffer, 0, buffer.Length);
            if (n <= 0)
            {
                return;
            }

            for (var i = 0; i < n; i++)
            {
                if (escapePending)
                {
                    escapePending = false;
                    if (buffer[i] is (byte)'q' or (byte)'Q')
                    {
                        log.Write("quit_requested");
                        session.RequestQuit();
                        return;
                    }

                    continue;
                }

                if (buffer[i] == 0x1D) // Ctrl+]
                {
                    escapePending = true;
                    continue;
                }

                session.SendBytes(buffer.AsSpan(i, 1));
            }
        }
    }

    private static void ReadCommands(PseudoConsoleSession session, EventLog log)
    {
        Console.WriteLine("Type 'help' for commands.");
        while (!session.HasExited)
        {
            var line = Console.ReadLine();
            if (line is null)
            {
                return;
            }

            var keys = line.Trim() switch
            {
                "help" => null,
                "enter" => "\r",
                "y" => "y",
                "n" => "n",
                "1" => "1",
                "2" => "2",
                "3" => "3",
                "esc" => "\x1b",
                "up" => "\x1b[A",
                "down" => "\x1b[B",
                "quit" => null,
                var other when other.StartsWith("type ", StringComparison.Ordinal) => other[5..] + "\r",
                var other when other.StartsWith("send ", StringComparison.Ordinal) => Options.Unescape(other[5..]),
                _ => null,
            };

            if (line.Trim() == "quit")
            {
                log.Write("quit_requested");
                session.RequestQuit();
                return;
            }

            if (keys is null)
            {
                Console.WriteLine("Commands: enter | y | n | 1 | 2 | 3 | esc | up | down | type <text> | send <keys> | quit");
                continue;
            }

            session.Send(keys);
            log.Write("sent", Visible(keys));
        }
    }

    /// <summary>Puts this console into VT pass-through: keys arrive as the bytes a terminal would send,
    /// and output escape sequences are interpreted. Returns the action that restores the old modes.</summary>
    private static Action EnterRawConsole()
    {
        var inHandle = Native.GetStdHandle(Native.StdInputHandle);
        var outHandle = Native.GetStdHandle(Native.StdOutputHandle);
        Native.GetConsoleMode(inHandle, out var oldIn);
        Native.GetConsoleMode(outHandle, out var oldOut);
        Native.SetConsoleMode(
            inHandle,
            (oldIn & ~(Native.EnableLineInput | Native.EnableEchoInput | Native.EnableProcessedInput)) | Native.EnableVirtualTerminalInput);
        Native.SetConsoleMode(outHandle, oldOut | Native.EnableVirtualTerminalProcessing);
        return () =>
        {
            Native.SetConsoleMode(inHandle, oldIn);
            Native.SetConsoleMode(outHandle, oldOut);
        };
    }

    private static string? FindOnPath(string exe) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(dir => Path.Combine(dir.Trim('"'), exe))
            .FirstOrDefault(File.Exists);

    private static string Quote(string s) => s.Contains(' ') || s.Contains('"') ? "\"" + s.Replace("\"", "\\\"") + "\"" : s;

    private static string Visible(string keys) =>
        keys.Replace("\r", "<Enter>").Replace("\n", "<LF>").Replace("\x1b", "<Esc>");
}

/// <summary>Command-line options.</summary>
internal sealed record Options
{
    public required string Dir { get; init; }
    public string Name { get; init; } = "bosun-spike";
    public string? Resume { get; init; }
    public string? Extra { get; init; }
    public string? Command { get; init; }
    public bool Mirror { get; init; }
    public string? AutoTrust { get; init; }
    public string? AutoRc { get; init; }
    public int StartDelaySeconds { get; init; }
    public short Cols { get; init; } = 120;
    public short Rows { get; init; } = 40;

    public static Options Parse(string[] args)
    {
        string? dir = null, name = null, resume = null, extra = null, command = null, autoTrust = null, autoRc = null;
        bool mirror = false;
        int delay = 0;
        short cols = 120, rows = 40;

        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{a} needs a value.");
            switch (a)
            {
                case "--dir": dir = Next(); break;
                case "--name": name = Next(); break;
                case "--resume": resume = Next(); break;
                case "--extra": extra = Next(); break;
                case "--cmd": command = Next(); break;
                case "--mirror": mirror = true; break;
                case "--headless": mirror = false; break;
                case "--start-delay": delay = int.Parse(Next()); break;
                case "--cols": cols = short.Parse(Next()); break;
                case "--rows": rows = short.Parse(Next()); break;
                case "--auto-trust": autoTrust = "\r"; break;
                case "--auto-rc": autoRc = "y"; break;
                default:
                    if (a.StartsWith("--auto-trust=", StringComparison.Ordinal))
                    {
                        autoTrust = Unescape(a["--auto-trust=".Length..]);
                    }
                    else if (a.StartsWith("--auto-rc=", StringComparison.Ordinal))
                    {
                        autoRc = Unescape(a["--auto-rc=".Length..]);
                    }
                    else
                    {
                        throw new ArgumentException($"Unknown option {a}.");
                    }

                    break;
            }
        }

        return new Options
        {
            Dir = Path.GetFullPath(Environment.ExpandEnvironmentVariables(dir ?? throw new ArgumentException("--dir is required."))),
            Name = name ?? "bosun-spike",
            Resume = resume,
            Extra = extra,
            Command = command,
            Mirror = mirror,
            AutoTrust = autoTrust,
            AutoRc = autoRc,
            StartDelaySeconds = delay,
            Cols = cols,
            Rows = rows,
        };
    }

    public static string Unescape(string s) => s.Replace("\\r", "\r").Replace("\\n", "\n").Replace("\\e", "\x1b");
}

/// <summary>One child process attached to a pseudo console this program owns.</summary>
internal sealed class PseudoConsoleSession : IDisposable
{
    private readonly IntPtr pseudoConsole;
    private readonly FileStream input;
    private readonly Process process;
    private readonly ManualResetEventSlim quit = new();
    private readonly Lock sendGate = new();
    private bool disposed;

    private PseudoConsoleSession(IntPtr pseudoConsole, FileStream input, FileStream output, Process process)
    {
        this.pseudoConsole = pseudoConsole;
        this.input = input;
        Output = output;
        this.process = process;
    }

    public FileStream Output { get; }

    public int ProcessId => process.Id;

    public bool HasExited => process.HasExited;

    public string ExitDescription => process.HasExited ? $"exit code {process.ExitCode}" : "still running (quit requested)";

    public static PseudoConsoleSession Start(StringBuilder commandLine, string workingDirectory, Native.Coord size)
    {
        if (!Native.CreatePipe(out var ptyInputRead, out var ptyInputWrite, IntPtr.Zero, 0)
            || !Native.CreatePipe(out var ptyOutputRead, out var ptyOutputWrite, IntPtr.Zero, 0))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreatePipe failed");
        }

        var hr = Native.CreatePseudoConsole(size, ptyInputRead, ptyOutputWrite, 0, out var pseudoConsole);
        if (hr != 0)
        {
            throw new Win32Exception(hr, "CreatePseudoConsole failed");
        }

        // The pseudo console holds its own copies of its ends.
        ptyInputRead.Dispose();
        ptyOutputWrite.Dispose();

        var attributeSize = IntPtr.Zero;
        Native.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attributeSize);
        var attributes = Marshal.AllocHGlobal(attributeSize);
        try
        {
            if (!Native.InitializeProcThreadAttributeList(attributes, 1, 0, ref attributeSize)
                || !Native.UpdateProcThreadAttribute(attributes, 0, Native.ProcThreadAttributePseudoConsole, pseudoConsole, IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Setting the pseudo console attribute failed");
            }

            var startup = new Native.StartupInfoEx { AttributeList = attributes };
            startup.StartupInfo.Cb = Marshal.SizeOf<Native.StartupInfoEx>();
            // Without this, a child can pick up this program's own (possibly redirected) handles instead
            // of the pseudo console's.
            startup.StartupInfo.Flags = Native.StartfUseStdHandles;

            if (!Native.CreateProcess(null, commandLine, IntPtr.Zero, IntPtr.Zero, false,
                    Native.ExtendedStartupInfoPresent | Native.CreateUnicodeEnvironment, IntPtr.Zero,
                    workingDirectory, ref startup, out var info))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"CreateProcess failed for: {commandLine}");
            }

            Native.CloseHandle(info.Thread);
            var process = Process.GetProcessById(info.ProcessId);
            Native.CloseHandle(info.Process);
            return new PseudoConsoleSession(
                pseudoConsole,
                new FileStream(ptyInputWrite, FileAccess.Write, 1),
                new FileStream(ptyOutputRead, FileAccess.Read, 1),
                process);
        }
        finally
        {
            Native.DeleteProcThreadAttributeList(attributes);
            Marshal.FreeHGlobal(attributes);
        }
    }

    public void Send(string keys) => SendBytes(Encoding.UTF8.GetBytes(keys));

    public void SendBytes(ReadOnlySpan<byte> bytes)
    {
        lock (sendGate)
        {
            input.Write(bytes);
            input.Flush();
        }
    }

    public void RequestQuit() => quit.Set();

    public void WaitForExitOrQuit()
    {
        var exited = new ManualResetEventSlim();
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => exited.Set();
        if (process.HasExited)
        {
            return;
        }

        WaitHandle.WaitAny([exited.WaitHandle, quit.WaitHandle]);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        input.Dispose();
        // Closing the pseudo console closes the session's console; Claude Code is expected to exit.
        Native.ClosePseudoConsole(pseudoConsole);
        if (!process.WaitForExit(TimeSpan.FromSeconds(5)))
        {
            process.Kill(entireProcessTree: true);
        }

        process.Dispose();
    }
}
