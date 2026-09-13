import { useState } from "react";
import { ExternalLink, GitPullRequest } from "lucide-react";
import type { ResultPr, TicketDetail, WorkflowSlice } from "@/lib/types";
import { cn, safeStringify } from "@/lib/utils";
import { Badge, type Tone } from "@/components/ui/Badge";
import { SectionTitle } from "@/components/ui/Field";

function gateTone(gate: string | undefined): Tone {
  if (!gate) return "neutral";
  const g = gate.toUpperCase();
  if (g === "PASSED" || g === "PASS") return "ok";
  if (g.includes("FAIL") || g.includes("BLOCK")) return "danger";
  if (g.includes("PEND") || g.includes("WAIT") || g.includes("REVIEW")) return "warn";
  return "info";
}

function ciTone(ci: string | undefined): Tone {
  if (!ci) return "neutral";
  const c = ci.toLowerCase();
  if (c.includes("success") || c === "green" || c === "passed" || c === "pass") return "ok";
  if (c.includes("fail") || c === "red" || c.includes("error")) return "danger";
  if (c.includes("pending") || c.includes("running") || c.includes("progress") || c === "yellow") return "warn";
  return "neutral";
}

function count(v: number | string[] | null | undefined): number {
  if (typeof v === "number") return v;
  if (Array.isArray(v)) return v.length;
  return 0;
}

function prLink(s: WorkflowSlice): { url: string | null; label: string } {
  const url = s.prUrl ?? (typeof s.pr === "string" && s.pr.startsWith("http") ? s.pr : null);
  const n = s.prNumber ?? (typeof s.pr === "number" ? s.pr : null);
  if (url) {
    const m = /\/pull\/(\d+)/.exec(url);
    return { url, label: m ? `#${m[1]}` : "PR" };
  }
  if (n !== null) return { url: null, label: `#${n}` };
  return { url: null, label: "—" };
}

export function WorkflowTab({ ticket }: { ticket: TicketDetail }) {
  const [raw, setRaw] = useState(false);
  const ws = ticket.workflowState;
  const prs = ticket.result?.prs ?? [];
  const slices = ws?.slices ?? [];

  if (!ws && !ticket.result) {
    return (
      <div className="py-10 text-center text-[12.5px] text-fg-faint">
        No workflow state yet. It appears once work has started and the orchestrator has written <span className="font-mono">tasks/{ticket.id}/state.json</span>.
      </div>
    );
  }

  return (
    <div className="flex flex-col gap-6">
      <div className="flex justify-end">
        <button type="button" onClick={() => setRaw((v) => !v)} className={cn("rounded border px-2 h-6 text-[11.5px]", raw ? "border-accent/50 bg-accent-soft text-accent" : "border-line text-fg-muted hover:text-fg")}>
          {raw ? "Pretty" : "Raw JSON"}
        </button>
      </div>

      {raw ? (
        <>
          <section>
            <SectionTitle>workflowState</SectionTitle>
            <pre className="overflow-auto rounded-md border border-line bg-surface-2 p-3 font-mono text-[11.5px] leading-relaxed">{safeStringify(ws)}</pre>
          </section>
          <section>
            <SectionTitle>result</SectionTitle>
            <pre className="overflow-auto rounded-md border border-line bg-surface-2 p-3 font-mono text-[11.5px] leading-relaxed">{safeStringify(ticket.result)}</pre>
          </section>
        </>
      ) : (
        <>
          {ws && (
            <section>
              <SectionTitle>State</SectionTitle>
              <dl className="grid grid-cols-[auto_1fr] gap-x-4 gap-y-1.5 text-[12.5px]">
                <dt className="text-fg-muted">Phase</dt>
                <dd className="font-medium">{ws.phase ?? <span className="text-fg-faint">—</span>}</dd>
                <dt className="text-fg-muted">Gate</dt>
                <dd>{ws.gate ? <Badge tone={gateTone(ws.gate)}>{ws.gate}</Badge> : <span className="text-fg-faint">—</span>}</dd>
                <dt className="text-fg-muted">Round</dt>
                <dd className="font-mono">{ws.round ?? <span className="text-fg-faint">—</span>}</dd>
                {Object.entries(ws)
                  .filter(([k, v]) => !["phase", "gate", "round", "slices"].includes(k) && (typeof v === "string" || typeof v === "number" || typeof v === "boolean"))
                  .map(([k, v]) => (
                    <FragmentRow key={k} k={k} v={String(v)} />
                  ))}
              </dl>
            </section>
          )}

          {slices.length > 0 && (
            <section>
              <SectionTitle>Slices</SectionTitle>
              <div className="overflow-x-auto rounded-md border border-line">
                <table className="w-full text-[12px]">
                  <thead className="bg-surface-2 text-left text-[11px] uppercase tracking-wide text-fg-muted">
                    <tr>
                      <th className="px-2.5 py-1.5 font-medium">Slice</th>
                      <th className="px-2.5 py-1.5 font-medium">Repo</th>
                      <th className="px-2.5 py-1.5 font-medium">Tier</th>
                      <th className="px-2.5 py-1.5 font-medium">PR</th>
                      <th className="px-2.5 py-1.5 text-right font-medium">Blockers</th>
                      <th className="px-2.5 py-1.5 text-right font-medium">Majors</th>
                      <th className="px-2.5 py-1.5 font-medium">Merged</th>
                    </tr>
                  </thead>
                  <tbody>
                    {slices.map((s, i) => {
                      const link = prLink(s);
                      const blockers = count(s.blockers);
                      const majors = count(s.majors);
                      return (
                        <tr key={s.id ?? i} className="border-t border-line">
                          <td className="px-2.5 py-1.5 font-medium">{s.name ?? s.id ?? `#${i + 1}`}{s.status && <span className="ml-1.5 text-fg-faint">{s.status}</span>}</td>
                          <td className="px-2.5 py-1.5 font-mono text-fg-muted">{s.repo ?? "—"}</td>
                          <td className="px-2.5 py-1.5 font-mono">{s.tier ?? "—"}</td>
                          <td className="px-2.5 py-1.5">
                            {link.url ? (
                              <a href={link.url} target="_blank" rel="noreferrer noopener" className="inline-flex items-center gap-1 text-accent hover:underline">
                                <GitPullRequest size={12} /> {link.label}
                              </a>
                            ) : (
                              <span className="text-fg-faint">{link.label}</span>
                            )}
                          </td>
                          <td className={cn("px-2.5 py-1.5 text-right font-mono", blockers > 0 && "text-danger")}>{blockers}</td>
                          <td className={cn("px-2.5 py-1.5 text-right font-mono", majors > 0 && "text-warn")}>{majors}</td>
                          <td className="px-2.5 py-1.5">{s.merged === true ? <Badge tone="ok">merged</Badge> : s.merged === false ? <Badge>open</Badge> : <span className="text-fg-faint">—</span>}</td>
                        </tr>
                      );
                    })}
                  </tbody>
                </table>
              </div>
            </section>
          )}

          <section>
            <SectionTitle>Pull requests</SectionTitle>
            {prs.length === 0 ? (
              <div className="text-[12px] text-fg-faint">No PRs in result.</div>
            ) : (
              <ul className="flex flex-col gap-1.5">
                {prs.map((pr, i) => (
                  <PrRow key={pr.url ?? i} pr={pr} />
                ))}
              </ul>
            )}
          </section>
        </>
      )}
    </div>
  );
}

function FragmentRow({ k, v }: { k: string; v: string }) {
  return (
    <>
      <dt className="text-fg-muted">{k}</dt>
      <dd className="font-mono break-all">{v}</dd>
    </>
  );
}

function PrRow({ pr }: { pr: ResultPr }) {
  const ci = pr.ci ?? pr.ciState;
  const label = pr.title ?? (pr.number !== undefined ? `#${pr.number}` : pr.url ?? "PR");
  return (
    <li className="flex items-center gap-2 rounded-md border border-line px-2.5 py-1.5 text-[12.5px]">
      <GitPullRequest size={13} className="shrink-0 text-accent" />
      {pr.url ? (
        <a href={pr.url} target="_blank" rel="noreferrer noopener" className="min-w-0 truncate font-medium text-fg hover:text-accent hover:underline">
          {label}
        </a>
      ) : (
        <span className="min-w-0 truncate font-medium">{label}</span>
      )}
      {pr.repo && <span className="font-mono text-[11px] text-fg-faint">{pr.repo}</span>}
      {pr.number !== undefined && pr.title && <span className="font-mono text-[11px] text-fg-faint">#{pr.number}</span>}
      <span className="ml-auto flex items-center gap-1.5">
        {pr.merged === true && <Badge tone="ok">merged</Badge>}
        {pr.state && pr.merged !== true && <Badge>{pr.state}</Badge>}
        {ci && <Badge tone={ciTone(ci)}>CI {ci}</Badge>}
        {pr.url && <ExternalLink size={12} className="text-fg-faint" />}
      </span>
    </li>
  );
}
