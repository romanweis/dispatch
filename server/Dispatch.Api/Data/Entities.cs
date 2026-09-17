using System.Text.Json;

namespace Dispatch.Api.Data;

public enum TicketStatus
{
    Backlog,
    Refining,
    NeedsInput,
    Ready,
    InProgress,
    Review,
    Done,
    Failed,
}

/// <summary>feature: refine into a spec first. task: clear enough to skip refinement; the body is the spec.</summary>
public enum TicketType
{
    Feature,
    Task,
}

public enum RunKind
{
    Refine,
    Answer,
    Work,
    Resume,
    Ship,
}

public enum RunStatus
{
    Pending,
    Running,
    Done,
    Failed,
    Cancelled,
}

public enum CommentAuthor
{
    User,
    Agent,
    System,
}

public sealed class Project
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public required string DisplayName { get; set; }
    public required string Org { get; set; }
    public required string Workspace { get; set; }
    public required string BaseContainer { get; set; }
    public int MaxParallel { get; set; } = 1;
    public string ReposJson { get; set; } = "[]";
    public required string ConfigPath { get; set; }
    public DateTime CreatedAt { get; set; }

    public List<Ticket> Tickets { get; set; } = [];
}

public sealed class Ticket
{
    public long Id { get; set; }
    public int ProjectId { get; set; }
    public Project? Project { get; set; }
    public required string Title { get; set; }
    public string Body { get; set; } = "";
    public TicketType Type { get; set; } = TicketType.Feature;
    public TicketStatus Status { get; set; } = TicketStatus.Backlog;
    public string? Slug { get; set; }
    public string? Spec { get; set; }

    /// <summary>When true, a passed review gate queues a ship run (/ship-feature) instead of parking the ticket in review.</summary>
    public bool AutoMerge { get; set; }
    public string? Container { get; set; }
    public string? ClaudeSessionId { get; set; }
    public JsonDocument? WorkflowState { get; set; }
    public JsonDocument? Result { get; set; }
    public required string Token { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public List<Question> Questions { get; set; } = [];
    public List<Comment> Comments { get; set; } = [];
    public List<Run> Runs { get; set; } = [];
    public List<ProgressNote> ProgressNotes { get; set; } = [];

    public string ContainerName => $"t-{Id}";
}

public sealed class Question
{
    public long Id { get; set; }
    public long TicketId { get; set; }
    public Ticket? Ticket { get; set; }
    public required string Text { get; set; }
    public string? Answer { get; set; }
    public DateTime AskedAt { get; set; }
    public DateTime? AnsweredAt { get; set; }
    public long? RunId { get; set; }
}

public sealed class Comment
{
    public long Id { get; set; }
    public long TicketId { get; set; }
    public Ticket? Ticket { get; set; }
    public CommentAuthor Author { get; set; }
    public required string Text { get; set; }
    public DateTime CreatedAt { get; set; }
}

public sealed class Run
{
    public long Id { get; set; }
    public long TicketId { get; set; }
    public Ticket? Ticket { get; set; }
    public RunKind Kind { get; set; }
    public RunStatus Status { get; set; } = RunStatus.Pending;
    public string? SessionId { get; set; }
    public required string Prompt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? EndedAt { get; set; }
    public int? ExitCode { get; set; }
    public string? Error { get; set; }
    public bool SpecSubmitted { get; set; }
    public DateTime CreatedAt { get; set; }

    public List<RunEvent> Events { get; set; } = [];

    public bool IsActive => Status is RunStatus.Pending or RunStatus.Running;
}

public sealed class RunEvent
{
    public long Id { get; set; }
    public long RunId { get; set; }
    public Run? Run { get; set; }
    public long Seq { get; set; }
    public required JsonDocument Payload { get; set; }
    public DateTime RecordedAt { get; set; }
}

public sealed class ProgressNote
{
    public long Id { get; set; }
    public long TicketId { get; set; }
    public Ticket? Ticket { get; set; }
    public long? RunId { get; set; }
    public required string Phase { get; set; }
    public string? Note { get; set; }
    public DateTime CreatedAt { get; set; }
}
