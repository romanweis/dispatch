using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dispatch.Api.Data;
using Dispatch.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Dispatch.Api.Tests;

public sealed class CliAuthTests : IClassFixture<ApiFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    private readonly ApiFactory _factory;

    public CliAuthTests(ApiFactory factory)
    {
        _factory = factory;
    }

    private async Task<(long Id, string Token)> CreateTicketAsync(HttpClient client)
    {
        var projects = await client.GetFromJsonAsync<List<ProjectDto>>("/api/projects", Json);
        Assert.NotNull(projects);
        var demo = Assert.Single(projects!);
        Assert.Equal("demo", demo.Name);
        Assert.Equal(["backend"], demo.Repos);

        var response = await client.PostAsJsonAsync("/api/tickets", new { projectId = demo.Id, title = "Hello", body = "world" }, Json);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var ticket = await response.Content.ReadFromJsonAsync<TicketDto>(Json);
        Assert.NotNull(ticket);
        Assert.Equal(TicketStatus.Backlog, ticket!.Status);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DispatchDbContext>();
        var token = await db.Tickets.Where(t => t.Id == ticket.Id).Select(t => t.Token).FirstAsync();
        return (ticket.Id, token);
    }

    [Fact]
    public async Task Healthz_ok()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/healthz");
        response.EnsureSuccessStatusCode();
        Assert.Equal("{\"status\":\"ok\"}", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Creating_a_task_queues_work_unless_start_is_false()
    {
        var client = _factory.CreateClient();
        var projects = await client.GetFromJsonAsync<List<ProjectDto>>("/api/projects", Json);
        var projectId = Assert.Single(projects!).Id;

        var started = await client.PostAsJsonAsync("/api/tickets", new { projectId, title = "Fix footer typo", body = "", type = "task" }, Json);
        Assert.Equal(HttpStatusCode.Created, started.StatusCode);
        var startedTicket = (await started.Content.ReadFromJsonAsync<TicketDto>(Json))!;
        Assert.Equal(TicketType.Task, startedTicket.Type);
        Assert.NotEqual(TicketStatus.Backlog, startedTicket.Status);

        var parked = await client.PostAsJsonAsync("/api/tickets", new { projectId, title = "Later", body = "", type = "task", start = false }, Json);
        var parkedTicket = (await parked.Content.ReadFromJsonAsync<TicketDto>(Json))!;
        Assert.Equal(TicketStatus.Backlog, parkedTicket.Status);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DispatchDbContext>();
        Assert.True(await db.Runs.AnyAsync(r => r.TicketId == startedTicket.Id && r.Kind == RunKind.Work));
        Assert.False(await db.Runs.AnyAsync(r => r.TicketId == parkedTicket.Id));

        var bad = await client.PostAsJsonAsync("/api/tickets", new { projectId, title = "x", type = "epic" }, Json);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task Cli_endpoints_require_bearer_token()
    {
        var client = _factory.CreateClient();
        var (id, token) = await CreateTicketAsync(client);

        // no header
        var none = await client.PostAsJsonAsync($"/api/tickets/{id}/questions", new { questions = new[] { "Q?" } }, Json);
        Assert.Equal(HttpStatusCode.Unauthorized, none.StatusCode);
        var error = await none.Content.ReadFromJsonAsync<ErrorDto>(Json);
        Assert.Equal("unauthorized", error!.Code);

        // wrong token
        using (var wrong = new HttpRequestMessage(HttpMethod.Post, $"/api/tickets/{id}/progress"))
        {
            wrong.Headers.Authorization = new("Bearer", new string('0', 64));
            wrong.Content = JsonContent.Create(new { phase = "x" });
            var response = await client.SendAsync(wrong);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        // another ticket's token
        var (_, otherToken) = await CreateTicketAsync(client);
        using (var other = new HttpRequestMessage(HttpMethod.Post, $"/api/tickets/{id}/spec"))
        {
            other.Headers.Authorization = new("Bearer", otherToken);
            other.Content = JsonContent.Create(new { spec = "nope" });
            var response = await client.SendAsync(other);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        // correct token
        using (var ok = new HttpRequestMessage(HttpMethod.Post, $"/api/tickets/{id}/questions"))
        {
            ok.Headers.Authorization = new("Bearer", token);
            ok.Content = JsonContent.Create(new { questions = new[] { "Which DB?", "Which UI?" } });
            var response = await client.SendAsync(ok);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            var questions = await response.Content.ReadFromJsonAsync<List<QuestionDto>>(Json);
            Assert.Equal(2, questions!.Count);
        }

        var detail = await client.GetFromJsonAsync<JsonElement>($"/api/tickets/{id}", Json);
        Assert.Equal("needs_input", detail.GetProperty("status").GetString());
        Assert.Equal(2, detail.GetProperty("openQuestions").GetInt32());
        Assert.Equal("none", detail.GetProperty("containerState").GetString());
        Assert.True(detail.TryGetProperty("attachCommand", out _));
        Assert.True(detail.TryGetProperty("lastProgress", out var progress));
        Assert.Equal(JsonValueKind.Null, progress.ValueKind);
    }

    [Fact]
    public async Task Comments_author_depends_on_bearer()
    {
        var client = _factory.CreateClient();
        var (id, token) = await CreateTicketAsync(client);

        var user = await client.PostAsJsonAsync($"/api/tickets/{id}/comments", new { text = "from ui" }, Json);
        Assert.Equal(HttpStatusCode.Created, user.StatusCode);
        Assert.Equal(CommentAuthor.User, (await user.Content.ReadFromJsonAsync<CommentDto>(Json))!.Author);

        using var agent = new HttpRequestMessage(HttpMethod.Post, $"/api/tickets/{id}/comments");
        agent.Headers.Authorization = new("Bearer", token);
        agent.Content = JsonContent.Create(new { text = "from agent" });
        var response = await client.SendAsync(agent);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(CommentAuthor.Agent, (await response.Content.ReadFromJsonAsync<CommentDto>(Json))!.Author);
    }

    [Fact]
    public async Task Progress_shows_up_as_last_progress()
    {
        var client = _factory.CreateClient();
        var (id, token) = await CreateTicketAsync(client);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/tickets/{id}/progress");
        request.Headers.Authorization = new("Bearer", token);
        request.Content = JsonContent.Create(new { phase = "implementing", note = "backend done" });
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var ticket = await client.GetFromJsonAsync<JsonElement>($"/api/tickets/{id}", Json);
        var progress = ticket.GetProperty("lastProgress");
        Assert.Equal("implementing", progress.GetProperty("phase").GetString());
        Assert.Equal("backend done", progress.GetProperty("note").GetString());
    }

    [Fact]
    public async Task Errors_use_contract_shape()
    {
        var client = _factory.CreateClient();
        var (id, _) = await CreateTicketAsync(client);

        var start = await client.PostAsync($"/api/tickets/{id}/start", null);
        Assert.Equal(HttpStatusCode.BadRequest, start.StatusCode);
        var error = await start.Content.ReadFromJsonAsync<ErrorDto>(Json);
        Assert.Equal("invalid_transition", error!.Code);
        Assert.False(string.IsNullOrEmpty(error.Error));

        var missing = await client.GetAsync("/api/tickets/999999");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("not_found", (await missing.Content.ReadFromJsonAsync<ErrorDto>(Json))!.Code);

        var refine = await client.PostAsync($"/api/tickets/{id}/refine", null);
        Assert.Equal(HttpStatusCode.Accepted, refine.StatusCode);
        var again = await client.PostAsync($"/api/tickets/{id}/refine", null);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("run_active", (await again.Content.ReadFromJsonAsync<ErrorDto>(Json))!.Code);

        var list = await client.GetFromJsonAsync<JsonElement>("/api/tickets?status=refining", Json);
        Assert.Contains(list.EnumerateArray(), t => t.GetProperty("id").GetInt64() == id);
    }

    [Fact]
    public async Task Run_events_endpoint_and_spa_fallback()
    {
        var client = _factory.CreateClient();
        var (id, _) = await CreateTicketAsync(client);
        var run = await (await client.PostAsync($"/api/tickets/{id}/refine", null)).Content.ReadFromJsonAsync<RunDto>(Json);

        var events = await client.GetFromJsonAsync<List<RunEventDto>>($"/api/runs/{run!.Id}/events?since=0&limit=10", Json);
        Assert.Empty(events!);

        var api404 = await client.GetAsync("/api/nope");
        Assert.Equal(HttpStatusCode.NotFound, api404.StatusCode);
    }
}
