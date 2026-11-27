using System.Text;
using System.Text.Json;

namespace ParentalControl.Domain.Sessions;

/// <summary>A log line of the Session Probe (<c>{ "log": "..." }</c>), at most 256 characters.</summary>
public sealed record ProbeLogLine(string Text);

/// <summary>
/// Reads the lines of one Session Probe run (session-probe-and-agent-channel, design D3, S7, S9). The service feeds the
/// bytes of the pipe; the reader keeps a fixed buffer of <see cref="MaxLineBytes"/> per line, plus one byte for a
/// <c>\r</c> before the <c>\n</c>. A longer line is dropped up to its line end, without a larger buffer.
/// <para>
/// Each sample line needs a valid <c>t</c> that is larger than the <c>t</c> of the previous accepted sample line of
/// this run and not larger than the current tick of the service; else the line is dropped. A window sample needs a
/// valid <c>fg</c>. A missing or wrong <c>dur</c> counts as 0. Any other field with a wrong type, and a <c>cursor</c> or <c>children</c> list with more than 8
/// entries, drops only that field. A text field with a lone surrogate escape (for example <c>"\ud800"</c>) drops only
/// that field; any other read error of a line drops only that line. No input throws. Content never ends the probe: the reader only counts the problems, and the service
/// logs the first one once per run. Not thread-safe: one reader per probe run.
/// </para>
/// </summary>
public sealed class ProbeLineReader
{
    public const int MaxLineBytes = 4096;
    public const int MaxEntries = 8;
    public const int MaxTitleChars = 256;
    public const int MaxDesktopChars = 64;
    public const int MaxLogChars = 256;
    private const int MaxDepth = 8;

    private readonly byte[] _line = new byte[MaxLineBytes + 1];
    private int _fill;
    private bool _discarding;
    private long? _lastT;

    /// <summary>The number of lines that the reader dropped in this run.</summary>
    public int DroppedLines { get; private set; }

    /// <summary>The number of fields that the reader dropped in this run.</summary>
    public int DroppedFields { get; private set; }

    /// <summary>The number of sample lines without <c>dur</c> in this run.</summary>
    public int MissingDurations { get; private set; }

    /// <summary>The first problem of this run (a dropped line or field), for one log line; null if none.</summary>
    public string? FirstProblem { get; private set; }

    /// <summary>The <c>t</c> of the last accepted sample line, or null.</summary>
    public long? LastT => _lastT;

    /// <summary>
    /// Takes bytes of the pipe and adds each accepted line (a <see cref="ProbeSample"/> or a
    /// <see cref="ProbeLogLine"/>) to <paramref name="output"/>.
    /// </summary>
    /// <param name="serviceTickNow">The <c>Environment.TickCount64</c> of the service at the read.</param>
    public void Feed(ReadOnlySpan<byte> data, long serviceTickNow, List<object> output)
    {
        foreach (var b in data)
        {
            if (b == (byte)'\n')
            {
                if (_discarding)
                {
                    _discarding = false;
                    DropLine("line over 4 KB");
                }
                else
                {
                    var length = _fill > 0 && _line[_fill - 1] == (byte)'\r' ? _fill - 1 : _fill;
                    if (length > 0) Parse(_line.AsSpan(0, length), serviceTickNow, output);
                }
                _fill = 0;
                continue;
            }
            if (_discarding) continue;
            // The one byte over MaxLineBytes may only be the \r of a \r\n line end.
            if (_fill > MaxLineBytes || (_fill == MaxLineBytes && b != (byte)'\r'))
            {
                _discarding = true;
                _fill = 0;
                continue;
            }
            _line[_fill++] = b;
        }
    }

    private void Parse(ReadOnlySpan<byte> line, long serviceTickNow, List<object> output)
    {
        try
        {
            ParseLine(line, serviceTickNow, output);
        }
        catch (InvalidOperationException)
        {
            // GetString of a string with a lone surrogate escape throws this exception (review S1).
            DropLine("line has a string that is not valid UTF-16");
        }
    }

    private void ParseLine(ReadOnlySpan<byte> line, long serviceTickNow, List<object> output)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(line.ToArray(), new JsonDocumentOptions { MaxDepth = MaxDepth });
        }
        catch (JsonException)
        {
            DropLine("line is not valid JSON");
            return;
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                DropLine("line is not a JSON object");
                return;
            }

            if (!root.TryGetProperty("t", out var tElement))
            {
                if (root.TryGetProperty("log", out var log) && log.ValueKind == JsonValueKind.String)
                    output.Add(new ProbeLogLine(Clean(log.GetString(), MaxLogChars)));
                else
                    DropLine("sample line without t");
                return;
            }

            if (tElement.ValueKind != JsonValueKind.Number || !tElement.TryGetInt64(out var t) || t < 0)
            {
                DropLine("t is not valid");
                return;
            }
            if (_lastT is { } last && t <= last)
            {
                DropLine("t does not increase");
                return;
            }
            if (t > serviceTickNow)
            {
                DropLine("t is after the service tick");
                return;
            }

            var sample = ParseSample(root, t);
            if (sample is null) return;
            sample = sample with { Duration = Duration(root) };
            _lastT = t;
            output.Add(sample);
        }
    }

    private ProbeSample? ParseSample(JsonElement root, long t)
    {
        if (root.TryGetProperty("blind", out var blind))
        {
            ProbeBlindReason? reason = blind.ValueKind == JsonValueKind.String
                ? blind.GetString() switch
                {
                    "desktop" => ProbeBlindReason.Desktop,
                    "window" => ProbeBlindReason.Window,
                    "children" => ProbeBlindReason.Children,
                    _ => null,
                }
                : null;
            if (reason is null)
            {
                DropLine("blind kind is not valid");
                return null;
            }
            return new ProbeSample(t, ProbeSampleKind.Blind, BlindReason: reason, Desktop: OptionalText(root, "desktop", MaxDesktopChars));
        }

        if (root.TryGetProperty("window", out var window) && window.ValueKind == JsonValueKind.False)
            return new ProbeSample(t, ProbeSampleKind.NoWindow, Desktop: OptionalText(root, "desktop", MaxDesktopChars));

        if (!root.TryGetProperty("fg", out var fgElement) || TryEntry(fgElement) is not { } fg)
        {
            DropLine("window sample without a valid fg");
            return null;
        }

        return new ProbeSample(
            t,
            ProbeSampleKind.Window,
            Foreground: fg,
            Focus: OptionalEntry(root, "focus"),
            Children: OptionalList(root, "children"),
            Cursor: OptionalCursor(root),
            Hung: OptionalEntry(root, "hung"),
            Title: OptionalText(root, "title", MaxTitleChars),
            Desktop: OptionalText(root, "desktop", MaxDesktopChars));
    }

    // dur: an integer of milliseconds. A missing or wrong dur counts as 0, with one problem for the log.
    private TimeSpan Duration(JsonElement root)
    {
        if (!root.TryGetProperty("dur", out var element))
        {
            MissingDurations++;
            FirstProblem ??= "sample line without dur";
            return TimeSpan.Zero;
        }
        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var ms) && ms is >= 0 and < 86_400_000)
            return TimeSpan.FromMilliseconds(ms);
        DropField("dur");
        return TimeSpan.Zero;
    }

    private ProbeWindow? OptionalEntry(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var element) || element.ValueKind == JsonValueKind.Null) return null;
        var entry = TryEntry(element);
        if (entry is null) DropField(name);
        return entry;
    }

    private IReadOnlyList<ProbeWindow>? OptionalList(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var element) || element.ValueKind == JsonValueKind.Null) return null;
        var list = TryList(element);
        if (list is null) DropField(name);
        return list;
    }

    private ProbeCursor? OptionalCursor(JsonElement root)
    {
        if (!root.TryGetProperty("cursor", out var element) || element.ValueKind == JsonValueKind.Null) return null;
        if (element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty("noActivate", out var noActivate)
            && noActivate.ValueKind is JsonValueKind.True or JsonValueKind.False
            && element.TryGetProperty("entries", out var entries)
            && TryList(entries) is { Count: > 0 } list)
            return new ProbeCursor(noActivate.GetBoolean(), list);
        DropField("cursor");
        return null;
    }

    private string? OptionalText(JsonElement root, string name, int maxChars)
    {
        if (!root.TryGetProperty(name, out var element) || element.ValueKind == JsonValueKind.Null) return null;
        if (element.ValueKind != JsonValueKind.String)
        {
            DropField(name);
            return null;
        }
        try
        {
            return Clean(element.GetString(), maxChars);
        }
        catch (InvalidOperationException)
        {
            // A lone surrogate escape: drop the text, keep the sample.
            DropField(name);
            return null;
        }
    }

    private static IReadOnlyList<ProbeWindow>? TryList(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() > MaxEntries) return null;
        var list = new List<ProbeWindow>(element.GetArrayLength());
        foreach (var item in element.EnumerateArray())
        {
            if (TryEntry(item) is not { } entry) return null;
            list.Add(entry);
        }
        return list;
    }

    private static ProbeWindow? TryEntry(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (!element.TryGetProperty("hwnd", out var hwnd) || hwnd.ValueKind != JsonValueKind.Number
            || !hwnd.TryGetInt64(out var hwndValue) || hwndValue == 0) return null;
        if (!TryId(element, "tid", out var tid) || !TryId(element, "pid", out var pid)) return null;
        return new ProbeWindow(hwndValue, tid, pid);
    }

    private static bool TryId(JsonElement element, string name, out int value)
    {
        value = 0;
        return element.TryGetProperty(name, out var id) && id.ValueKind == JsonValueKind.Number
            && id.TryGetInt32(out value) && value > 0;
    }

    /// <summary>Cuts the text and replaces each lone surrogate and each control character with U+FFFD.</summary>
    public static string Clean(string? text, int maxChars)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var builder = new StringBuilder(Math.Min(text.Length, maxChars));
        for (var i = 0; i < text.Length && builder.Length < maxChars; i++)
        {
            var c = text[i];
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                if (builder.Length + 2 > maxChars) break;
                builder.Append(c).Append(text[++i]);
            }
            else if (char.IsSurrogate(c) || char.IsControl(c))
                builder.Append('�');
            else
                builder.Append(c);
        }
        return builder.ToString();
    }

    private void DropLine(string reason)
    {
        DroppedLines++;
        FirstProblem ??= reason;
    }

    private void DropField(string name)
    {
        DroppedFields++;
        FirstProblem ??= $"field {name} has a wrong type";
    }
}
