import { useBoardStore } from "@/store/board";
import { cn } from "@/lib/utils";

export function ProjectFilter() {
  const projects = useBoardStore((s) => s.projects);
  const filter = useBoardStore((s) => s.projectFilter);
  const setFilter = useBoardStore((s) => s.setProjectFilter);
  const tickets = useBoardStore((s) => s.tickets);

  const counts: Record<number, number> = {};
  for (const t of Object.values(tickets)) counts[t.projectId] = (counts[t.projectId] ?? 0) + 1;
  const total = Object.keys(tickets).length;

  const Chip = ({ id, label, count }: { id: number | null; label: string; count: number }) => (
    <button
      type="button"
      onClick={() => setFilter(id)}
      className={cn(
        "inline-flex h-6 items-center gap-1.5 rounded-full border px-2.5 text-[12px] transition-colors",
        filter === id
          ? "border-accent/60 bg-accent-soft text-accent"
          : "border-line bg-surface text-fg-muted hover:border-line-strong hover:text-fg",
      )}
    >
      {label}
      <span className="font-mono text-[10.5px] opacity-70">{count}</span>
    </button>
  );

  return (
    <div className="flex flex-wrap items-center gap-1.5">
      <Chip id={null} label="All" count={total} />
      {projects.map((p) => (
        <Chip key={p.id} id={p.id} label={p.displayName || p.name} count={counts[p.id] ?? 0} />
      ))}
    </div>
  );
}
