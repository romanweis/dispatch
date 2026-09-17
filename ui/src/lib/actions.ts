import type { Ticket } from "./types";

export function gatePassed(t: Ticket): boolean {
  return typeof t.workflowState?.gate === "string" && t.workflowState.gate.toUpperCase() === "PASSED";
}

/** Which actions the current ticket state allows (mirrors docs/api.md transition rules). */
export function allowedActions(t: Ticket) {
  const running = t.activeRunId !== null;
  return {
    refine: !running && (t.status === "backlog" || t.status === "failed" || (t.status === "needs_input" && t.openQuestions === 0)),
    answer: !running && t.status === "needs_input" && t.openQuestions > 0,
    // Tasks skip refinement: they start straight from backlog (or again after a failure) with the body as spec.
    // once the gate has passed the work is done: Start would re-plan it, so the board offers Ship instead
    start: !running && !gatePassed(t) && (t.status === "ready" || (t.type === "task" && (t.status === "backlog" || t.status === "failed"))),
    startNeedsSpec: t.status === "ready" && !t.spec && t.type !== "task",
    resume: !running && !!t.claudeSessionId && t.status !== "done",
    // a ship run that failed halfway leaves the ticket failed; merge-fleet.sh resumes, so offer Ship there too
    ship: !running && gatePassed(t) && (t.status === "review" || t.status === "failed"),
    cancel: running,
    done: !running && (t.status === "review" || t.status === "in_progress" || t.status === "failed"),
    refresh: t.container !== null,
    delete: !running,
  };
}
