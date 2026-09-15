using System.Text.Json;
using Dispatch.Api.Data;

namespace Dispatch.Api.Services;

/// <summary>Facts about a finished run needed to decide the ticket's next status.</summary>
public sealed record RunFacts(
    RunKind Kind,
    TicketStatus Current,
    int ExitCode,
    bool ResultSeen,
    bool ResultIsError,
    bool NewQuestions,
    bool SpecSubmitted,
    string? Gate,
    bool Shipped = false,
    bool AutoMerge = false)
{
    public bool Succeeded => ResultSeen && !ResultIsError && ExitCode == 0;
}

/// <summary>
/// docs/api.md: "The run outcome is decided when the claude process exits: if the run recorded new questions -> needs_input;
/// else if it recorded a spec (refine/answer) -> ready; else for work runs the ticket stays in_progress unless
/// workflowState.gate == "PASSED" -> review; non-zero exit or missing result -> failed."
/// Ship runs: `ticket progress shipped` -> done; otherwise back to review for the human. A passed gate on an
/// auto-merge ticket does not stop in review but queues a ship run (see <see cref="ShouldAutoShip"/>).
/// </summary>
public static class RunOutcome
{
    public static TicketStatus Decide(RunFacts f)
    {
        // Refinement-style runs: refine, answer, and resume while the ticket is not in the work phase.
        var refinement = f.Kind is RunKind.Refine or RunKind.Answer
                         || (f.Kind == RunKind.Resume && f.Current is not (TicketStatus.InProgress or TicketStatus.Review or TicketStatus.Done));

        if (f.NewQuestions)
        {
            return TicketStatus.NeedsInput;
        }

        if (f.Kind == RunKind.Ship)
        {
            if (!f.Succeeded)
            {
                return TicketStatus.Failed;
            }

            return f.Shipped ? TicketStatus.Done : TicketStatus.Review;
        }

        if (f.SpecSubmitted && refinement)
        {
            return TicketStatus.Ready;
        }

        if (!f.Succeeded)
        {
            return TicketStatus.Failed;
        }

        if (!refinement)
        {
            if (string.Equals(f.Gate, "PASSED", StringComparison.OrdinalIgnoreCase))
            {
                return TicketStatus.Review;
            }

            return f.Current == TicketStatus.Review ? TicketStatus.Review : TicketStatus.InProgress;
        }

        // Refinement run that ended cleanly without asking anything or submitting a spec:
        // there is nothing actionable, surface it as failed so it is not silently stuck in `refining`.
        return f.Current == TicketStatus.Refining ? TicketStatus.Failed : f.Current;
    }

    /// <summary>True when the decided status is review, the ticket has auto-merge on and the run was not itself a ship run.</summary>
    public static bool ShouldAutoShip(RunFacts f, TicketStatus decided) =>
        f.AutoMerge && decided == TicketStatus.Review && f.Kind != RunKind.Ship;

    public static string? ReadGate(JsonDocument? workflowState)
    {
        if (workflowState is null || workflowState.RootElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return workflowState.RootElement.TryGetProperty("gate", out var gate) && gate.ValueKind == JsonValueKind.String
            ? gate.GetString()
            : null;
    }
}
