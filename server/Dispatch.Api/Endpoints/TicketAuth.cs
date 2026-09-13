using System.Security.Cryptography;
using System.Text;
using Dispatch.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Dispatch.Api.Endpoints;

/// <summary>Bearer-token check for the in-container `ticket` CLI. The token is per ticket and only valid for that ticket.</summary>
public static class TicketAuth
{
    public static string? ReadBearer(HttpRequest request)
    {
        var header = request.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(header) || !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var token = header["Bearer ".Length..].Trim();
        return token.Length == 0 ? null : token;
    }

    public static bool TokenMatches(string? presented, string expected)
    {
        if (presented is null)
        {
            return false;
        }

        var a = Encoding.UTF8.GetBytes(presented);
        var b = Encoding.UTF8.GetBytes(expected);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    /// <summary>Returns true when a valid bearer token for this ticket is present; false when no header; throws 401 on a wrong token.</summary>
    public static async Task<bool> CheckOptionalAsync(HttpContext context, long ticketId, DispatchDbContext db, CancellationToken ct)
    {
        var presented = ReadBearer(context.Request);
        if (presented is null)
        {
            return false;
        }

        var expected = await db.Tickets.AsNoTracking().Where(t => t.Id == ticketId).Select(t => t.Token).FirstOrDefaultAsync(ct);
        if (expected is null || !TokenMatches(presented, expected))
        {
            throw new DispatchException("unauthorized", "invalid ticket token", StatusCodes.Status401Unauthorized);
        }

        return true;
    }

    /// <summary>Endpoint filter for CLI-only routes: 401 unless a valid bearer token for {id} is present.</summary>
    public static async ValueTask<object?> RequireAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var idText = http.Request.RouteValues["id"]?.ToString();
        if (!long.TryParse(idText, out var ticketId))
        {
            return Results.Json(new Services.ErrorDto("invalid ticket id", "bad_request"), statusCode: StatusCodes.Status400BadRequest);
        }

        var presented = ReadBearer(http.Request);
        if (presented is null)
        {
            return Unauthorized("missing bearer token");
        }

        var db = http.RequestServices.GetRequiredService<DispatchDbContext>();
        var expected = await db.Tickets.AsNoTracking().Where(t => t.Id == ticketId).Select(t => t.Token).FirstOrDefaultAsync(http.RequestAborted);
        if (expected is null || !TokenMatches(presented, expected))
        {
            return Unauthorized("invalid ticket token");
        }

        return await next(context);
    }

    private static IResult Unauthorized(string message) =>
        Results.Json(new Services.ErrorDto(message, "unauthorized"), statusCode: StatusCodes.Status401Unauthorized);
}
