namespace Dispatch.Api;

public sealed class DispatchException(string code, string message, int statusCode = 400) : Exception(message)
{
    public string Code { get; } = code;

    public int StatusCode { get; } = statusCode;

    public static DispatchException NotFound(string what, object id) =>
        new("not_found", $"{what} {id} not found", 404);

    public static DispatchException InvalidTransition(string message) =>
        new("invalid_transition", message, 400);

    public static DispatchException RunActive(long ticketId) =>
        new("run_active", $"Ticket {ticketId} already has an active run", 409);

    public static DispatchException BadRequest(string message) =>
        new("bad_request", message, 400);
}
