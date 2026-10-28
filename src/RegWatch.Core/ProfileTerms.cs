using System.Text.RegularExpressions;

namespace RegWatch.Core;

/// <summary>
/// The core of PROFILE.md as search terms: the IRC sections and other terms of every bucket
/// weighted 2 or 3. Sources too broad to score in full (IRS letter rulings, Tax Court opinions,
/// Treasury press releases) are narrowed with these, so editing the profile also changes what
/// those sources bring in.
/// </summary>
public sealed partial class ProfileTerms
{
    public const int CoreWeight = 2;

    public static ProfileTerms Empty { get; } = new([], []);

    private ProfileTerms(IReadOnlyList<string> sections, IReadOnlyList<string> phrases)
    {
        SectionRefs = sections;
        Sections = sections.Select(BaseSection).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Phrases = phrases;
    }

    /// <summary>Section references as written, subsection kept: "679", "672(f)", "6039F".</summary>
    public IReadOnlyList<string> SectionRefs { get; }

    /// <summary>Bare section numbers: "679", "672", "6039F".</summary>
    public IReadOnlySet<string> Sections { get; }

    /// <summary>Every other term, with bare form numbers written out ("8938" becomes "Form 8938").</summary>
    public IReadOnlyList<string> Phrases { get; }

    public bool IsEmpty => SectionRefs.Count == 0 && Phrases.Count == 0;

    public static ProfileTerms From(string profileText)
    {
        var sections = new List<string>();
        var phrases = new List<string>();
        foreach (var bucket in ProfileDocument.Parse(profileText).Buckets.Where(b => b.Weight >= CoreWeight))
        {
            foreach (var raw in bucket.Terms.Select(t => t.Trim()).Where(t => t.Length > 0))
            {
                var refs = SectionsIn(raw);
                if (refs.Count > 0) sections.AddRange(refs);
                else phrases.Add(FormNumber().IsMatch(raw) ? "Form " + raw : raw);
            }
        }
        return new ProfileTerms(
            sections.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            phrases.Distinct(StringComparer.OrdinalIgnoreCase).ToList());
    }

    /// <summary>True when the text cites a core section ("section 6048", "§ 679") or contains a core term.</summary>
    public bool Matches(string text) =>
        SectionsIn(text).Any(s => Sections.Contains(BaseSection(s)))
        || Phrases.Any(p => Regex.IsMatch(text, $@"(?<![\w-]){Regex.Escape(p)}(?![\w-])", RegexOptions.IgnoreCase));

    /// <summary>
    /// Section references in a term or sentence: "§679", "§ 672(f)", "Section 2010(c)(5)(A)",
    /// "IRC 2642(g)", ranges ("§665–668" gives 665 to 668) and regulations ("§1.6038-4" gives 6038).
    /// </summary>
    public static IReadOnlyList<string> SectionsIn(string text)
    {
        var found = new List<string>();
        foreach (Match m in SectionRef().Matches(text))
        {
            var first = m.Groups["num"].Value;
            if (m.Groups["end"].Success && int.TryParse(first, out var from) && int.TryParse(m.Groups["end"].Value, out var to)
                && to > from && to - from <= 20)
            {
                for (var n = from; n <= to; n++) found.Add(n.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            else
            {
                found.Add(first + (m.Groups["sub"].Success ? $"({m.Groups["sub"].Value})" : ""));
            }
        }
        return found;
    }

    /// <summary>"672(f)" gives "672"; leading zeros go ("0430" gives "430").</summary>
    public static string BaseSection(string section)
    {
        var paren = section.IndexOf('(');
        var bare = paren < 0 ? section : section[..paren];
        return bare.TrimStart('0') is { Length: > 0 } s ? s : bare;
    }

    [GeneratedRegex(@"(?:§+|\b(?:sections?|secs?\.|IRC)\s)\s*(?:1\.)?(?<num>\d{2,4}[A-Z]?)(?:\((?<sub>[a-z0-9]{1,3})\))?(?:\s*[–-]\s*(?<end>\d{2,4})\b)?", RegexOptions.IgnoreCase)]
    private static partial Regex SectionRef();

    [GeneratedRegex(@"^\d{3,4}(-[A-Z]{1,3})?$")]
    private static partial Regex FormNumber();
}
