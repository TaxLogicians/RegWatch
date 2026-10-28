using System.Text;
using System.Text.RegularExpressions;

namespace RegWatch.Core;

/// <summary>
/// PROFILE.md split into editable topic buckets and false-positive guards, with everything
/// else kept as verbatim text. Entries that are not edited render exactly as they were read,
/// so opening and saving the profile never reformats it.
/// </summary>
public sealed partial class ProfileDocument
{
    private const int WrapWidth = 84;

    private readonly List<object> _segments = [];
    private string _newline = "\n";

    public List<BucketEntry> Buckets { get; } = [];
    public List<GuardEntry> Guards { get; } = [];

    /// <summary>False when PROFILE.md has no "Topic buckets" / "guards" section to edit as cards.</summary>
    public bool HasBucketSection { get; private set; }
    public bool HasGuardSection { get; private set; }

    /// <summary>Bullets in the bucket or guard sections that did not parse; kept as-is, editable in raw text.</summary>
    public int UnparsedBullets { get; private set; }

    public static ProfileDocument Parse(string text)
    {
        var doc = new ProfileDocument { _newline = text.Contains("\r\n") ? "\r\n" : "\n" };
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var buffer = new StringBuilder();

        void FlushText()
        {
            if (buffer.Length > 0) doc._segments.Add(buffer.ToString());
            buffer.Clear();
        }

        var section = Section.Other;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.StartsWith("## ", StringComparison.Ordinal))
                section = line.Contains("bucket", StringComparison.OrdinalIgnoreCase) ? Section.Buckets
                        : line.Contains("guard", StringComparison.OrdinalIgnoreCase) ? Section.Guards
                        : Section.Other;

            var inList = section != Section.Other && line.StartsWith("- ", StringComparison.Ordinal);
            if (!inList || (section == Section.Buckets ? doc.HasBucketSection : doc.HasGuardSection))
            {
                // Only the first bullet run in each section is structured; later runs stay text.
                buffer.Append(line);
                if (i < lines.Length - 1) buffer.Append('\n');
                continue;
            }

            // A bullet run: bullets plus their indented continuation lines, up to a blank line.
            FlushText();
            var bullets = new List<string>();
            while (i < lines.Length && lines[i].StartsWith("- ", StringComparison.Ordinal))
            {
                var sb = new StringBuilder(lines[i]);
                while (i + 1 < lines.Length && lines[i + 1].Length > 0 && char.IsWhiteSpace(lines[i + 1][0]))
                    sb.Append('\n').Append(lines[++i]);
                bullets.Add(sb.ToString());
                i++;
            }
            i--; // the for loop's i++ moves to the line after the run

            var lastLine = i == lines.Length - 1;
            if (section == Section.Buckets)
            {
                doc.HasBucketSection = true;
                foreach (var b in bullets)
                {
                    var entry = BucketEntry.TryParse(b);
                    if (entry is null) doc.UnparsedBullets++;
                    doc._segments.Add(entry ?? (object)new RawBullet(b));
                    if (entry is not null) doc.Buckets.Add(entry);
                }
                doc._segments.Add(new BucketSlot());
            }
            else
            {
                doc.HasGuardSection = true;
                foreach (var g in bullets)
                {
                    var entry = new GuardEntry(Join(g[2..]), g);
                    doc._segments.Add(entry);
                    doc.Guards.Add(entry);
                }
                doc._segments.Add(new GuardSlot());
            }
            if (!lastLine) buffer.Append('\n');
        }
        FlushText();
        return doc;
    }

    /// <summary>Renders the profile; untouched entries keep their original text.</summary>
    public string Render()
    {
        var sb = new StringBuilder();
        var bulletLines = new List<string>();

        void FlushBullets()
        {
            if (bulletLines.Count == 0) return;
            sb.Append(string.Join("\n", bulletLines));
            bulletLines.Clear();
        }

        foreach (var segment in _segments)
        {
            switch (segment)
            {
                case string text:
                    FlushBullets();
                    sb.Append(text);
                    break;
                case RawBullet raw:
                    bulletLines.Add(raw.Text);
                    break;
                case BucketEntry bucket when !Buckets.Contains(bucket):
                case GuardEntry guard when !Guards.Contains(guard):
                    break; // removed in the editor
                case BucketEntry bucket:
                    bulletLines.Add(bucket.Render());
                    break;
                case GuardEntry guard:
                    bulletLines.Add(guard.Render());
                    break;
                case BucketSlot:
                    // Buckets added in the editor go at the end of the bucket list.
                    bulletLines.AddRange(Buckets.Where(b => !_segments.Contains(b)).Select(b => b.Render()));
                    FlushBullets();
                    break;
                case GuardSlot:
                    bulletLines.AddRange(Guards.Where(g => !_segments.Contains(g)).Select(g => g.Render()));
                    FlushBullets();
                    break;
            }
        }
        FlushBullets();
        return sb.ToString().Replace("\n", _newline);
    }

    /// <summary>Updates the "_Last calibrated: …_" footer line, if the profile has one.</summary>
    public static string StampCalibrated(string text, DateOnly date) =>
        LastCalibrated().Replace(text, $"_Last calibrated: {date:yyyy-MM-dd} (edited in the RegWatch app). Edit freely._", 1);

    internal static string Wrap(string firstPrefix, string body)
    {
        var lines = new List<string>();
        var current = new StringBuilder(firstPrefix);
        var lineStart = 0;
        foreach (var word in body.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (current.Length - lineStart + word.Length + 1 > WrapWidth && current.Length > lineStart + firstPrefix.Length)
            {
                lines.Add(current.ToString(lineStart, current.Length - lineStart).TrimEnd());
                lineStart = current.Length;
                current.Append("  ");
            }
            else if (current.Length > lineStart && !char.IsWhiteSpace(current[^1]))
            {
                current.Append(' ');
            }
            current.Append(word);
        }
        lines.Add(current.ToString(lineStart, current.Length - lineStart).TrimEnd());
        return string.Join("\n", lines);
    }

    internal static string Join(string bullet) =>
        string.Join(" ", bullet.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0));

    /// <summary>Splits on commas that are not inside parentheses: "Form 926, 8975 (+ Schedule A, CbC)".</summary>
    internal static List<string> SplitTerms(string terms)
    {
        var result = new List<string>();
        var depth = 0;
        var sb = new StringBuilder();
        foreach (var c in terms)
        {
            if (c == '(') depth++;
            if (c == ')') depth = Math.Max(0, depth - 1);
            if (c == ',' && depth == 0)
            {
                if (sb.ToString().Trim() is { Length: > 0 } t) result.Add(t);
                sb.Clear();
            }
            else sb.Append(c);
        }
        if (sb.ToString().Trim() is { Length: > 0 } last) result.Add(last);
        return result;
    }

    private enum Section { Other, Buckets, Guards }

    private sealed record RawBullet(string Text);
    private sealed record BucketSlot;
    private sealed record GuardSlot;

    [GeneratedRegex(@"_Last calibrated:[^\n]*_")]
    private static partial Regex LastCalibrated();
}

/// <summary>One topic bucket: "- **key (weight)** — term, term, …".</summary>
public sealed partial class BucketEntry
{
    private readonly string? _original;
    private readonly string? _originalSignature;

    public BucketEntry(string key, int weight, IEnumerable<string> terms)
    {
        Key = key;
        Weight = weight;
        Terms = terms.ToList();
    }

    private BucketEntry(string key, int weight, List<string> terms, string original) : this(key, weight, terms)
    {
        _original = original;
        _originalSignature = Signature();
    }

    public string Key { get; set; }
    public int Weight { get; set; }
    public List<string> Terms { get; set; }

    public static BucketEntry? TryParse(string bullet)
    {
        var m = Pattern().Match(ProfileDocument.Join(bullet[2..]));
        return m.Success
            ? new BucketEntry(m.Groups[1].Value, int.Parse(m.Groups[2].Value), ProfileDocument.SplitTerms(m.Groups[3].Value), bullet)
            : null;
    }

    public string Render() =>
        _original is not null && Signature() == _originalSignature
            ? _original
            : ProfileDocument.Wrap($"- **{Key.Trim()} ({Math.Clamp(Weight, 0, 3)})** — ", string.Join(", ", Terms.Select(t => t.Trim()).Where(t => t.Length > 0)));

    private string Signature() => $"{Key}\u0001{Weight}\u0001{string.Join("\u0001", Terms)}";

    [GeneratedRegex(@"^\*\*([A-Za-z0-9_]+)\s*\((\d)\)\*\*\s*[—–-]+\s*(.*)$")]
    private static partial Regex Pattern();
}

/// <summary>One false-positive guard bullet, kept as Markdown text.</summary>
public sealed class GuardEntry(string text, string? original = null)
{
    private readonly string _originalText = text;

    public string Text { get; set; } = text;

    public string Render() =>
        original is not null && Text == _originalText
            ? original
            : ProfileDocument.Wrap("- ", Text.Replace("\r", " ").Replace("\n", " "));
}
