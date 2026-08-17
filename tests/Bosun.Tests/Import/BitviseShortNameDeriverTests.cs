using Bosun.Import;

namespace Bosun.Tests.Import;

/// <summary>
/// <see cref="BitviseShortNameDeriver"/> (bs-ww9.9, ADR-019): "TrainingGrounds.tlp" -> "traininggrounds".
/// </summary>
public sealed class BitviseShortNameDeriverTests
{
    [Theory]
    [InlineData("TrainingGrounds.tlp", "traininggrounds")]
    [InlineData("mediaserver.bscp", "mediaserver")]
    [InlineData("Media Server.tlp", "media-server")]
    [InlineData("UPPER_CASE-Name123.tlp", "upper-case-name123")]
    [InlineData("  spaced out  .tlp", "spaced-out")]
    public void Derive_LowercasesAndCleansTheFileNameStem(string fileName, string expected)
    {
        Assert.Equal(expected, BitviseShortNameDeriver.Derive(fileName));
    }

    [Fact]
    public void Derive_StripsTheExtension()
    {
        Assert.Equal("traininggrounds", BitviseShortNameDeriver.Derive("TrainingGrounds.tlp"));
        Assert.DoesNotContain("tlp", BitviseShortNameDeriver.Derive("TrainingGrounds.tlp"));
    }

    [Fact]
    public void Derive_FallsBackToAPlaceholder_WhenNothingAlphanumericSurvives()
    {
        Assert.Equal("imported-host", BitviseShortNameDeriver.Derive("----.tlp"));
        Assert.Equal("imported-host", BitviseShortNameDeriver.Derive("!!!.bscp"));
    }

    [Fact]
    public void Derive_NeverProducesLeadingOrTrailingHyphens()
    {
        var result = BitviseShortNameDeriver.Derive("-- weird -- name --.tlp");

        Assert.False(result.StartsWith('-'));
        Assert.False(result.EndsWith('-'));
    }

    [Fact]
    public void Derive_ThrowsArgumentNullException_ForNullFileName()
    {
        Assert.Throws<ArgumentNullException>(() => BitviseShortNameDeriver.Derive(null!));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Derive_ThrowsArgumentException_ForBlankFileName(string fileName)
    {
        Assert.Throws<ArgumentException>(() => BitviseShortNameDeriver.Derive(fileName));
    }
}
