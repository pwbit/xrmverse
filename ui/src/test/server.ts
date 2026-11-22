import { http, HttpResponse } from 'msw';
import { setupServer } from 'msw/node';

/**
 * The shared MSW server; tests add handlers with `server.use(...)`. Every signed-in shell posts its token to `/api/session`
 * (the web session cookie), so that endpoint answers by default, as does `/Ribbon/{entity}` (no custom commands). The web
 * admin reads the environment's presentation definition for its theme: by default the organization has none. The workflow
 * list is refused (403) unless a test answers it.
 */
export const server = setupServer(
  http.post('*/api/session', () => new HttpResponse(null, { status: 204 })),
  http.delete('*/api/session', () => new HttpResponse(null, { status: 204 })),
  // Every record form reads its table's custom commands (UI-1e); a test that has none gets no diff.
  http.get('*/Ribbon/:entity', ({ params }) => HttpResponse.json({ entity: params.entity, diffs: [] })),
  http.get('*/api/data/v9.2/settingdefinitions', () => HttpResponse.json({ value: [] })),
  // The apps page and the web admin header ask whether the user may read processes (the workflow studio): by default not.
  http.get('*/authoring/workflows', () => HttpResponse.json({ error: { code: '0x80040220', message: 'Not mocked.' } }, { status: 403 })),
);
