using RegWatch.Core;

namespace RegWatch.Tests;

public class ProfileDocumentTests
{
    private static string RealProfile()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "PROFILE.md");
        return File.ReadAllText(path);
    }

    private const string Small = """
        # Profile

        ## Topic buckets

        Intro line.

        - **foreign_trusts (3)** — §679, §672(f), Form 3520,
          Form 3520-A
        - **intl_info_returns (3)** — Form 5471, 8975 (+ Schedule A, country-by-country report)
        - something that is not a bucket

        ## False-positive guards (do NOT score up on the term alone)

        - "nonbank trustee" → §408 IRA rules
          → **0**
        - "REIT" → **0**

        ## Scoring instructions

        1. Apply guards first.

        _Last calibrated: 2026-06-12 (initial profile). Edit freely._
        """;

    [Fact]
    public void RealProfile_RoundTripsByteForByte()
    {
        var text = RealProfile();
        var doc = ProfileDocument.Parse(text);

        Assert.True(doc.HasBucketSection);
        Assert.True(doc.HasGuardSection);
        Assert.Equal(0, doc.UnparsedBullets);
        Assert.Equal(11, doc.Buckets.Count);
        Assert.Equal(text, doc.Render());
    }

    [Fact]
    public void RealProfile_ParsesWeightsAndParenthesisedTerms()
    {
        var doc = ProfileDocument.Parse(RealProfile());

        var trusts = doc.Buckets.Single(b => b.Key == "foreign_trusts");
        Assert.Equal(3, trusts.Weight);
        Assert.Contains("Form 3520-A", trusts.Terms);
        Assert.Contains("§672(f)", trusts.Terms);

        var returns = doc.Buckets.Single(b => b.Key == "intl_info_returns");
        Assert.Contains("8975 (+ Schedule A, country-by-country report)", returns.Terms);

        Assert.Equal(1, doc.Buckets.Single(b => b.Key == "estate_trust_valuation").Weight);
        Assert.Contains(doc.Guards, g => g.Text.Contains("nonbank trustee"));
    }

    [Fact]
    public void Edits_RewriteOnlyTheTouchedEntries()
    {
        var doc = ProfileDocument.Parse(Small);
        doc.Buckets[0].Weight = 2;
        doc.Buckets[0].Terms.Add("§6048");

        var text = doc.Render();

        Assert.Contains("- **foreign_trusts (2)** — §679, §672(f), Form 3520, Form 3520-A, §6048", text);
        // Untouched entries keep their exact original text, including wrapping.
        Assert.Contains("- **intl_info_returns (3)** — Form 5471, 8975 (+ Schedule A, country-by-country report)", text);
        Assert.Contains("- \"nonbank trustee\" → §408 IRA rules\n  → **0**", text);
        Assert.Contains("- something that is not a bucket", text);
        Assert.Equal(1, doc.UnparsedBullets);
    }

    [Fact]
    public void AddAndRemove_LandInTheRightSections()
    {
        var doc = ProfileDocument.Parse(Small);
        doc.Buckets.RemoveAt(1);
        doc.Buckets.Add(new BucketEntry("expatriation", 2, ["§877A", "Form 8854"]));
        doc.Guards.RemoveAt(1);
        doc.Guards.Add(new GuardEntry("\"grantor\" outside a trust context → ignore"));

        var text = doc.Render();

        Assert.DoesNotContain("intl_info_returns", text);
        Assert.DoesNotContain("\"REIT\"", text);
        var bucketAt = text.IndexOf("- **expatriation (2)** — §877A, Form 8854", StringComparison.Ordinal);
        var guardsHeading = text.IndexOf("## False-positive", StringComparison.Ordinal);
        var guardAt = text.IndexOf("- \"grantor\" outside a trust context → ignore", StringComparison.Ordinal);
        var instructions = text.IndexOf("## Scoring instructions", StringComparison.Ordinal);
        Assert.InRange(bucketAt, 0, guardsHeading);
        Assert.InRange(guardAt, guardsHeading, instructions);

        // The edited file parses back to the same structure.
        var again = ProfileDocument.Parse(text);
        Assert.Equal(["foreign_trusts", "expatriation"], again.Buckets.Select(b => b.Key));
        Assert.Equal(2, again.Guards.Count);
    }

    [Fact]
    public void LongEntries_WrapWithTwoSpaceContinuation()
    {
        var doc = ProfileDocument.Parse(Small);
        doc.Buckets[0].Terms = Enumerable.Range(1, 30).Select(n => $"§{1000 + n}").ToList();

        var bullet = doc.Render().Split('\n').SkipWhile(l => !l.StartsWith("- **foreign_trusts")).TakeWhile(l => l.StartsWith("- **foreign") || l.StartsWith("  ")).ToList();

        Assert.True(bullet.Count > 1);
        Assert.All(bullet, l => Assert.True(l.Length <= 84, l));
        Assert.Equal(30, ProfileDocument.Parse(doc.Render()).Buckets[0].Terms.Count);
    }

    [Fact]
    public void StampCalibrated_ReplacesFooter()
    {
        var text = ProfileDocument.StampCalibrated(Small, new DateOnly(2026, 10, 28));
        Assert.Contains("_Last calibrated: 2026-10-28 (edited in the RegWatch app). Edit freely._", text);
        Assert.DoesNotContain("2026-06-12", text);
    }

    [Fact]
    public void CrLfFiles_StayCrLf()
    {
        var crlf = Small.Replace("\n", "\r\n");
        var doc = ProfileDocument.Parse(crlf);
        Assert.Equal(crlf, doc.Render());
        doc.Guards[0].Text = "edited";
        Assert.DoesNotContain("\n", doc.Render().Replace("\r\n", ""));
    }
}
