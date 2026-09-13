using System.Text.Json;
using Dispatch.Api.Services;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;

namespace Dispatch.Api.Endpoints;

/// <summary>Maps exceptions to the contract's `{ error, code }` shape.</summary>
public sealed class DispatchExceptionHandler(IOptions<JsonOptions> json, ILogger<DispatchExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var (status, dto) = exception switch
        {
            DispatchException d => (d.StatusCode, new ErrorDto(d.Message, d.Code)),
            BadHttpRequestException b => (b.StatusCode, new ErrorDto(b.Message, "bad_request")),
            JsonException j => (StatusCodes.Status400BadRequest, new ErrorDto("invalid JSON: " + j.Message, "bad_request")),
            _ => (StatusCodes.Status500InternalServerError, new ErrorDto(exception.Message, "internal_error")),
        };

        if (status >= 500)
        {
            logger.LogError(exception, "Unhandled error on {Method} {Path}", httpContext.Request.Method, httpContext.Request.Path);
        }
        else
        {
            logger.LogDebug("{Method} {Path} -> {Status} {Code}: {Message}", httpContext.Request.Method, httpContext.Request.Path, status, dto.Code, dto.Error);
        }

        if (httpContext.Response.HasStarted)
        {
            return false;
        }

        httpContext.Response.StatusCode = status;
        await httpContext.Response.WriteAsJsonAsync(dto, json.Value.SerializerOptions, cancellationToken);
        return true;
    }
}
