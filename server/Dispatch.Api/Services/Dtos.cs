using System.Text.Json;
using Dispatch.Api.Data;

namespace Dispatch.Api.Services;

public sealed record ProjectDto(
    int Id,
    string Name,
    string DisplayName,
    string Org,
    string Workspace,
    string BaseContainer,
    int MaxParallel,
    IReadOnlyList<string> Repos,
    DateTime CreatedAt);

public sealed record ProgressDto(string Phase, string? Note, DateTime At);

public record TicketDto(
    long Id,
    int ProjectId,
    string ProjectName,
    string Title,
    string Body,
    TicketStatus Status,
    bool AutoMerge,
    string? Slug,
    string? Spec,
    string? Container,
    string ContainerState,
    string? ClaudeSessionId,
    JsonElement? WorkflowState,
    JsonElement? Result,
    int OpenQuestions,
    long? ActiveRunId,
    ProgressDto? LastProgress,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public sealed record TicketDetailDto : TicketDto
{
    public TicketDetailDto(
        TicketDto ticket,
        IReadOnlyList<QuestionDto> questions,
        IReadOnlyList<CommentDto> comments,
        IReadOnlyList<RunDto> runs,
        string attachCommand)
        : base(ticket)
    {
        Questions = questions;
        Comments = comments;
        Runs = runs;
        AttachCommand = attachCommand;
    }

    public IReadOnlyList<QuestionDto> Questions { get; }

    public IReadOnlyList<CommentDto> Comments { get; }

    public IReadOnlyList<RunDto> Runs { get; }

    public string AttachCommand { get; }
}

public sealed record QuestionDto(long Id, long TicketId, string Text, string? Answer, DateTime AskedAt, DateTime? AnsweredAt);

public sealed record CommentDto(long Id, long TicketId, CommentAuthor Author, string Text, DateTime CreatedAt);

public sealed record RunDto(
    long Id,
    long TicketId,
    RunKind Kind,
    RunStatus Status,
    string? SessionId,
    DateTime? StartedAt,
    DateTime? EndedAt,
    int? ExitCode,
    string? Error,
    int EventCount,
    DateTime CreatedAt);

public sealed record RunEventDto(long Seq, DateTime RecordedAt, JsonElement Payload);

public sealed record ErrorDto(string Error, string Code);

// ---- request bodies -------------------------------------------------------

public sealed record CreateTicketRequest(int ProjectId, string Title, string? Body, bool? AutoMerge);

public sealed record PatchTicketRequest(string? Title, string? Body, string? Spec, bool? AutoMerge);

public sealed record AnswerItem(long QuestionId, string Answer);

public sealed record AnswerRequest(List<AnswerItem>? Answers);

public sealed record ResumeRequest(string? Message);

public sealed record DoneRequest(bool? Snapshot);

public sealed record MoveRequest(string? Status);

public sealed record CommentRequest(string? Text);

public sealed record QuestionsRequest(List<string>? Questions);

public sealed record SpecRequest(string? Spec);

public sealed record ProgressRequest(string? Phase, string? Note);
