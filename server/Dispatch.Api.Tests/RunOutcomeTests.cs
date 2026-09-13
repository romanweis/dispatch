using System.Text.Json;
using Dispatch.Api.Data;
using Dispatch.Api.Services;
using Xunit;

namespace Dispatch.Api.Tests;

public sealed class RunOutcomeTests
{
    private static RunFacts Facts(
        RunKind kind,
        TicketStatus current,
        int exitCode = 0,
        bool resultSeen = true,
        bool resultIsError = false,
        bool questions = false,
        bool spec = false,
        string? gate = null) =>
        new(kind, current, exitCode, resultSeen, resultIsError, questions, spec, gate);

    [Theory]
    [InlineData(RunKind.Refine, TicketStatus.Refining)]
    [InlineData(RunKind.Answer, TicketStatus.Refining)]
    [InlineData(RunKind.Work, TicketStatus.InProgress)]
    [InlineData(RunKind.Resume, TicketStatus.Ready)]
    public void New_questions_always_lead_to_needs_input(RunKind kind, TicketStatus current)
    {
        Assert.Equal(TicketStatus.NeedsInput, RunOutcome.Decide(Facts(kind, current, questions: true)));
    }

    [Fact]
    public void Questions_win_over_spec_and_over_nonzero_exit()
    {
        Assert.Equal(TicketStatus.NeedsInput, RunOutcome.Decide(Facts(RunKind.Refine, TicketStatus.Refining, exitCode: 1, questions: true, spec: true)));
    }

    [Theory]
    [InlineData(RunKind.Refine)]
    [InlineData(RunKind.Answer)]
    public void Spec_from_refinement_runs_leads_to_ready(RunKind kind)
    {
        Assert.Equal(TicketStatus.Ready, RunOutcome.Decide(Facts(kind, TicketStatus.Refining, spec: true)));
    }

    [Fact]
    public void Spec_during_work_run_does_not_change_phase()
    {
        Assert.Equal(TicketStatus.InProgress, RunOutcome.Decide(Facts(RunKind.Work, TicketStatus.InProgress, spec: true)));
    }

    [Fact]
    public void Work_run_with_gate_passed_moves_to_review()
    {
        Assert.Equal(TicketStatus.Review, RunOutcome.Decide(Facts(RunKind.Work, TicketStatus.InProgress, gate: "PASSED")));
    }

    [Fact]
    public void Work_run_without_gate_stays_in_progress()
    {
        Assert.Equal(TicketStatus.InProgress, RunOutcome.Decide(Facts(RunKind.Work, TicketStatus.InProgress, gate: "RUNNING")));
        Assert.Equal(TicketStatus.InProgress, RunOutcome.Decide(Facts(RunKind.Work, TicketStatus.InProgress)));
    }

    [Theory]
    [InlineData(RunKind.Refine, TicketStatus.Refining)]
    [InlineData(RunKind.Work, TicketStatus.InProgress)]
    [InlineData(RunKind.Resume, TicketStatus.Review)]
    public void Nonzero_exit_fails(RunKind kind, TicketStatus current)
    {
        Assert.Equal(TicketStatus.Failed, RunOutcome.Decide(Facts(kind, current, exitCode: 2)));
    }

    [Fact]
    public void Missing_result_or_error_result_fails()
    {
        Assert.Equal(TicketStatus.Failed, RunOutcome.Decide(Facts(RunKind.Work, TicketStatus.InProgress, resultSeen: false)));
        Assert.Equal(TicketStatus.Failed, RunOutcome.Decide(Facts(RunKind.Work, TicketStatus.InProgress, resultIsError: true)));
    }

    [Fact]
    public void Refine_that_records_nothing_fails_instead_of_sticking_in_refining()
    {
        Assert.Equal(TicketStatus.Failed, RunOutcome.Decide(Facts(RunKind.Refine, TicketStatus.Refining)));
    }

    [Fact]
    public void Resume_in_review_with_clean_exit_stays_in_review()
    {
        Assert.Equal(TicketStatus.Review, RunOutcome.Decide(Facts(RunKind.Resume, TicketStatus.Review)));
    }

    [Fact]
    public void ReadGate_reads_string_gate_only()
    {
        Assert.Equal("PASSED", RunOutcome.ReadGate(JsonDocument.Parse("{\"gate\":\"PASSED\"}")));
        Assert.Null(RunOutcome.ReadGate(JsonDocument.Parse("{\"gate\":1}")));
        Assert.Null(RunOutcome.ReadGate(JsonDocument.Parse("[]")));
        Assert.Null(RunOutcome.ReadGate(null));
    }
}
