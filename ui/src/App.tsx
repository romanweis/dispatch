import { lazy, Suspense } from "react";
import { BrowserRouter, Navigate, Route, Routes } from "react-router";
import { BoardPage } from "@/pages/BoardPage";
import { Toasts } from "@/components/ui/Toasts";

// The drawer pulls in react-markdown; keep it out of the initial board bundle.
const TicketPage = lazy(() => import("@/pages/TicketPage").then((m) => ({ default: m.TicketPage })));

export default function App() {
  return (
    <BrowserRouter>
      <Routes>
        <Route path="/" element={<BoardPage />}>
          <Route
            path="tickets/:id"
            element={
              <Suspense fallback={null}>
                <TicketPage />
              </Suspense>
            }
          />
        </Route>
        <Route path="*" element={<Navigate to="/" replace />} />
      </Routes>
      <Toasts />
    </BrowserRouter>
  );
}
