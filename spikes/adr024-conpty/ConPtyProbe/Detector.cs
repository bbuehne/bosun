using System.Text;
using System.Text.RegularExpressions;

namespace ConPtyProbe;

/// <summary>
/// Watches Claude Code's terminal output for the things the spike measures: the folder-trust prompt,
/// the one-time "Enable Remote Control?" prompt, any claude.ai/code link, and lines that mention
/// Remote Control. Claude Code draws a full-screen UI with cursor movement, so matching is done on the
/// output with escape sequences replaced by spaces and whitespace collapsed. That is good enough to
/// spot a prompt, not to reconstruct the screen; raw.log keeps the exact bytes.
/// </summary>
internal sealed partial class Detector(EventLog log, Func<string, bool> autoAnswer)
{
    private const int BufferCap = 32_000;
    private static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(5);

    private readonly Decoder decoder = Encoding.UTF8.GetDecoder();
    private readonly Watch[] watches =
    [
        new("trust_prompt", TrustPattern()),
        new("enable_rc_prompt", EnableRcPattern()),
    ];

    private readonly HashSet<string> urls = [];
    private readonly HashSet<string> remoteLines = [];
    private readonly StringBuilder urlBuffer = new();

    public void Feed(byte[] bytes, int count)
    {
        var chars = new char[decoder.GetCharCount(bytes, 0, count)];
        decoder.GetChars(bytes, 0, count, chars, 0);
        var text = new string(chars);

        Append(urlBuffer, text);
        foreach (Match m in UrlPattern().Matches(urlBuffer.ToString()))
        {
            if (urls.Add(m.Value))
            {
                log.Write("url", m.Value);
            }
        }

        foreach (var watch in watches)
        {
            Append(watch.Buffer, text);
            var plain = Plain(watch.Buffer.ToString());
            var match = watch.Pattern.Match(plain);
            if (!match.Success || DateTime.UtcNow - watch.LastFired < Cooldown)
            {
                continue;
            }

            watch.LastFired = DateTime.UtcNow;
            log.Write(watch.Name, Snippet(plain, match.Index));
            watch.Buffer.Clear();
            if (autoAnswer(watch.Name))
            {
                watch.LastFired = DateTime.UtcNow;
            }
        }

        foreach (Match m in RemoteLinePattern().Matches(Plain(text)))
        {
            var line = m.Value.Trim();
            if (line.Length > 0 && remoteLines.Count < 200 && remoteLines.Add(line))
            {
                log.Write("rc_text", line);
            }
        }
    }

    /// <summary>Escape sequences become spaces, runs of whitespace one space.</summary>
    internal static string Plain(string raw) =>
        Whitespace().Replace(Escapes().Replace(raw, " "), " ");

    private static string Snippet(string plain, int index)
    {
        var start = Math.Max(0, index - 80);
        var length = Math.Min(plain.Length - start, 240);
        return plain.Substring(start, length);
    }

    private static void Append(StringBuilder buffer, string text)
    {
        buffer.Append(text);
        if (buffer.Length > BufferCap)
        {
            buffer.Remove(0, buffer.Length - BufferCap);
        }
    }

    private sealed class Watch(string name, Regex pattern)
    {
        public string Name { get; } = name;
        public Regex Pattern { get; } = pattern;
        public StringBuilder Buffer { get; } = new();
        public DateTime LastFired { get; set; } = DateTime.MinValue;
    }

    // OSC ... BEL/ST, CSI ... final byte, and two-byte escapes.
    [GeneratedRegex(@"\x1B\][^\x07\x1B]*(?:\x07|\x1B\\)|\x1B\[[0-?]*[ -/]*[@-~]|\x1B[@-Z\\-_]")]
    private static partial Regex Escapes();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"(?i)do you trust|trust (?:this|the) (?:folder|files|directory|project)|yes, proceed|yes, i trust")]
    private static partial Regex TrustPattern();

    [GeneratedRegex(@"(?i)enable remote control\s*\?")]
    private static partial Regex EnableRcPattern();

    [GeneratedRegex(@"https://claude\.ai/code[^\s\x07\x1B""'<>)\]]*")]
    private static partial Regex UrlPattern();

    [GeneratedRegex(@"(?i)[^.\n]{0,60}remote[ -]control[^.\n]{0,80}")]
    private static partial Regex RemoteLinePattern();
}
