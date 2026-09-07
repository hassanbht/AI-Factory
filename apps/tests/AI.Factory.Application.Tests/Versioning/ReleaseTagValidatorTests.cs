using AI.Factory.Application.Versioning;
using Xunit;

namespace AI.Factory.Application.Tests.Versioning;

/// <summary>
/// Exercises the version-mismatch-rejection logic used by release.yml's tag-validation step
/// (phase 17.5.2, workstreams B/H). This proves the normalization/comparison rule works today,
/// locally, without needing a GitHub remote, a real tag push, or any CI runtime.
/// </summary>
public sealed class ReleaseTagValidatorTests
{
    [Theory]
    [InlineData("v0.1.0", "0.1.0")]
    [InlineData("0.1.0", "0.1.0")]
    public void NormalizeTag_StripsOnlyLeadingV(string tag, string expected)
    {
        Assert.Equal(expected, ReleaseTagValidator.NormalizeTag(tag));
    }

    [Fact]
    public void TagMatchesVersion_ReturnsTrue_WhenTagAndVersionMatch()
    {
        Assert.True(ReleaseTagValidator.TagMatchesVersion("v0.1.0", "0.1.0"));
    }

    [Fact]
    public void TagMatchesVersion_ReturnsFalse_WhenTagAndVersionDiverge()
    {
        Assert.False(ReleaseTagValidator.TagMatchesVersion("v0.2.0", "0.1.0"));
    }

    [Fact]
    public void TagMatchesVersion_ReturnsTrue_WhenTagHasNoVPrefix()
    {
        Assert.True(ReleaseTagValidator.TagMatchesVersion("0.1.0", "0.1.0"));
    }

    [Fact]
    public void TagMatchesVersion_ReturnsFalse_ForPreReleaseSuffixNotPresentInVersionFile()
    {
        // Deliberate, documented behavior (see ReleaseTagValidator.NormalizeTag): a pre-release
        // suffix is never stripped, so "v0.1.0-rc1" only matches a VERSION file that itself
        // contains "0.1.0-rc1" — never plain "0.1.0".
        Assert.False(ReleaseTagValidator.TagMatchesVersion("v0.1.0-rc1", "0.1.0"));
    }

    [Fact]
    public void TagMatchesVersion_ReturnsTrue_ForMatchingPreReleaseSuffix()
    {
        Assert.True(ReleaseTagValidator.TagMatchesVersion("v0.1.0-rc1", "0.1.0-rc1"));
    }
}
