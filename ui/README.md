# XRMverse web admin (`ui/`)

Vite + React 18 + TypeScript (strict) + Fluent UI v9, tested with Vitest and Testing Library.
All dependencies are pinned to exact versions; `pnpm-lock.yaml` is committed.

## Dev loop

```
pnpm -C ui install
pnpm -C ui dev        # http://localhost:5173
```

`pnpm dev` proxies `/api`, `/admin` and `/ui/config.json` to the host at `http://localhost:5000`, so run the
.NET host there first.

## Scripts

- `pnpm -C ui test` runs the unit tests (Vitest, jsdom).
- `pnpm -C ui typecheck` runs `tsc --noEmit`.
- `pnpm -C ui build` type-checks and writes `ui/dist/` (`index.html` plus hashed `assets/`), which the host serves.
- `pnpm -C ui e2e` runs the Playwright end-to-end scenarios (`e2e/`). Its global setup starts SQL Server with
  `docker run` (the image of the .NET integration tests), builds the UI, the host and the fixture solution, and runs
  `Xrmd.Host` in development sign-in mode on a free port; the teardown stops both, also after a failure or Ctrl+C.
  Needs Docker, the .NET SDK, `zip` and a Chromium (`XRMD_E2E_CHROMIUM`, else `/opt/pw-browsers/chromium`, else
  Playwright's own). The host's output is kept in `e2e/.host.log`.
  `pnpm -C ui e2e model-driven.spec.ts` runs one file: that one imports the real export
  `tests/fixtures/solutions/RealExports/OpenProject365_1_0_1_1.zip` through the Web API and drives the ProjectIQ app
  (shell, views). `model-driven-forms.spec.ts` drives the Project form of the same app: create, edit, subgrid, assign,
  share, read-only, delete and unsaved changes (it imports the export only when an earlier spec has not).
  `model-driven-scripts.spec.ts` runs the export's own form scripts and HTML web resource unchanged: the Material Usage,
  Account, WBS Node and Supplier Document forms, the decision page as a page and as a dialog, and the session cookie.

## Layout

- `src/shell/`: routing, sign-in, the API client, the theme (`theme.ts`, xrmverse "Indigo" brand ramp, system font
  stack, no Segoe UI) and the UI strings.
- `src/admin/`: the web admin under `/manage` (see `docs/web-admin.md`).
- `src/apps/`: the model-driven app shell (see `docs/model-driven-apps.md`): `/apps` (`AppPicker`), `/main.aspx`
  (`AppShell`, `mainAspx.ts`), app visibility (`loadApps.ts`), site map trimming (`trimSiteMap.ts`), user settings, and
  the list page (`EntityListPage`, `loadViews.ts`, `runView.ts`) and the record page (`RecordPage`, `record/`: forms,
  record loading, form state, the unsaved-changes guard).
- `src/client-api/`: web resources and the Client API (see `docs/model-driven-apps.md`): the per-form script host
  (`scriptHost.ts`), form events and the execution context, `formContext` over the form state and the UI overlay store,
  `Xrm.WebApi`, `Xrm.Navigation` and `Xrm.Utility` (`xrm.ts`), the shell's dialogs (`DialogHost.tsx`) and the explicit
  "not available" stubs (`notImplemented.ts`).
- `src/parsers/`: SiteMap, LayoutXml, FetchXML, FormXml and AppModule readers that build the semantic model in `src/model/`.
- `src/renderers/classic/`: the classic renderer (site map navigation, view grid, view selector, quick find,
  placeholders, and `form/`: tabs, sections, controls, subgrids, quick views, the command bar and the Assign and Share dialogs). It draws the model and never reads XML.

UI strings live in `src/shell/i18n/en.ts`; `it.ts` holds the Italian strings of the app shell (a partial catalogue: a
missing key falls back to English). Use `t()` from `src/shell/i18n/t.ts`.
