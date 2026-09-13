using Dispatch.Api.Services;
using Xunit;

namespace Dispatch.Api.Tests;

public sealed class SlugGeneratorTests
{
    [Theory]
    [InlineData("Add login page", "add-login-page")]
    [InlineData("Add Login Page For Admin Users", "add-login-page-for")]
    [InlineData("  Fix:  the   BUG!!  ", "fix-the-bug")]
    [InlineData("Ünïcödé Tëst", "unicode-test")]
    [InlineData("!!!", "ticket")]
    [InlineData("v2 API_refresh (beta)", "v2-api-refresh-beta")]
    public void Kebab_case_max_four_words(string title, string expected)
    {
        Assert.Equal(expected, SlugGenerator.Kebab(title));
    }

    [Fact]
    public void FromTitle_appends_three_char_lowercase_alnum_salt()
    {
        var slug = SlugGenerator.FromTitle("Add login page");
        Assert.Matches("^add-login-page-[a-z0-9]{3}$", slug);
    }

    [Fact]
    public void Salts_are_random()
    {
        var salts = Enumerable.Range(0, 50).Select(_ => SlugGenerator.Salt(3)).Distinct().Count();
        Assert.True(salts > 40);
    }

    [Fact]
    public void Token_is_32_bytes_hex()
    {
        var token = SlugGenerator.NewToken();
        Assert.Equal(64, token.Length);
        Assert.Matches("^[0-9a-f]{64}$", token);
    }
}
