import { create } from "zustand";
import { ApiError } from "@/lib/api";
import { errorMessage } from "@/lib/utils";

export type ToastTone = "error" | "info" | "success";

export interface Toast {
  id: number;
  tone: ToastTone;
  title: string;
  detail?: string;
}

interface ToastState {
  toasts: Toast[];
  push: (t: Omit<Toast, "id">, ttlMs?: number) => number;
  dismiss: (id: number) => void;
}

let nextId = 1;

export const useToasts = create<ToastState>((set) => ({
  toasts: [],
  push: (t, ttlMs = 6000) => {
    const id = nextId++;
    set((s) => ({ toasts: [...s.toasts, { ...t, id }].slice(-5) }));
    if (ttlMs > 0) {
      setTimeout(() => set((s) => ({ toasts: s.toasts.filter((x) => x.id !== id) })), ttlMs);
    }
    return id;
  },
  dismiss: (id) => set((s) => ({ toasts: s.toasts.filter((x) => x.id !== id) })),
}));

/** Show an error toast for an API/network failure. Returns the toast id. */
export function toastError(err: unknown, context?: string): number {
  const { push } = useToasts.getState();
  if (err instanceof ApiError) {
    return push({
      tone: "error",
      title: context ? `${context}: ${err.message}` : err.message,
      detail: err.code !== `http_${err.status}` ? `${err.code} · HTTP ${err.status}` : `HTTP ${err.status}`,
    });
  }
  return push({ tone: "error", title: context ? `${context}: ${errorMessage(err)}` : errorMessage(err) });
}

export function toastInfo(title: string, detail?: string) {
  return useToasts.getState().push({ tone: "info", title, detail }, 4000);
}

export function toastSuccess(title: string, detail?: string) {
  return useToasts.getState().push({ tone: "success", title, detail }, 3000);
}
