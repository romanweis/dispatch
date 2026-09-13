using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;

namespace Dispatch.Api.Endpoints;

/// <summary>Server-sent events writer: `event: name\ndata: json\n\n`, heartbeat comment every 15s.</summary>
public sealed class SseWriter(HttpResponse response, JsonSerializerOptions json)
{
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(15);

    private readonly SemaphoreSlim _lock = new(1, 1);

    public static SseWriter Start(HttpContext context)
    {
        var json = context.RequestServices.GetRequiredService<IOptions<JsonOptions>>().Value.SerializerOptions;
        var response = context.Response;
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache";
        response.Headers.Connection = "keep-alive";
        response.Headers["X-Accel-Buffering"] = "no";
        return new SseWriter(response, json);
    }

    public async Task SendAsync(string eventName, object payload, CancellationToken ct)
    {
        var data = JsonSerializer.Serialize(payload, json);
        var text = $"event: {eventName}\ndata: {data}\n\n";
        await WriteAsync(text, ct);
    }

    public Task PingAsync(CancellationToken ct) => WriteAsync(": ping\n\n", ct);

    private async Task WriteAsync(string text, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            await response.Body.WriteAsync(Encoding.UTF8.GetBytes(text), ct);
            await response.Body.FlushAsync(ct);
        }
        finally
        {
            _lock.Release();
        }
    }
}
