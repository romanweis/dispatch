using System.Text.Json;

namespace Dispatch.Api.Data;

/// <summary>snake_case wire/DB names for enums (matches the API contract: needs_input, in_progress, ...).</summary>
public static class EnumNames
{
    public static string ToWire<T>(T value) where T : struct, Enum =>
        JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString());

    public static bool TryParse<T>(string? text, out T value) where T : struct, Enum
    {
        foreach (var candidate in Enum.GetValues<T>())
        {
            if (string.Equals(ToWire(candidate), text, StringComparison.OrdinalIgnoreCase)
                || string.Equals(candidate.ToString(), text, StringComparison.OrdinalIgnoreCase))
            {
                value = candidate;
                return true;
            }
        }

        value = default;
        return false;
    }

    public static T Parse<T>(string? text) where T : struct, Enum =>
        TryParse<T>(text, out var v)
            ? v
            : throw DispatchException.BadRequest($"Unknown {typeof(T).Name} '{text}'");
}
