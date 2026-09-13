using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Dispatch.Api.Services;

public static partial class SlugGenerator
{
    private const string Alphabet = "abcdefghijklmnopqrstuvwxyz0123456789";

    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex NonAlnum();

    /// <summary>kebab-case of the title (max 4 words) + '-' + 3 random lowercase alphanumerics.</summary>
    public static string FromTitle(string title, int maxWords = 4) => $"{Kebab(title, maxWords)}-{Salt(3)}";

    public static string Kebab(string title, int maxWords = 4)
    {
        var normalized = title.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(normalized.Length);
        foreach (var c in normalized)
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark)
            {
                sb.Append(char.ToLowerInvariant(c));
            }
        }

        var words = NonAlnum().Split(sb.ToString())
            .Where(w => w.Length > 0)
            .Take(maxWords)
            .ToList();

        return words.Count == 0 ? "ticket" : string.Join('-', words);
    }

    public static string Salt(int length)
    {
        var chars = new char[length];
        for (var i = 0; i < length; i++)
        {
            chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        }

        return new string(chars);
    }

    public static string NewToken() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
}
