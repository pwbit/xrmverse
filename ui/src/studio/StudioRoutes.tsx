import { Navigate, Outlet, Route, Routes } from 'react-router-dom';
import { NotFound } from '../shell/layout/NotFound';
import { EditorPage } from './editor/EditorPage';
import { WorkflowList } from './list/WorkflowList';
import { StudioGate } from './StudioGate';
import { StudioShell } from './StudioShell';

/** `/studio/*`: the workflow studio's pages inside its shell, behind its gate; the list is `/studio/workflows` (`/studio` goes there). */
export function StudioRoutes() {
  return (
    <Routes>
      <Route element={<StudioShell />}>
        <Route index element={<Navigate to="/studio/workflows" replace />} />
        <Route
          element={
            <StudioGate>
              <Outlet />
            </StudioGate>
          }
        >
          <Route path="workflows" element={<WorkflowList />} />
          <Route path="workflows/new" element={<EditorPage />} />
          <Route path="workflows/:id" element={<EditorPage />} />
        </Route>
        <Route path="*" element={<NotFound />} />
      </Route>
    </Routes>
  );
}
