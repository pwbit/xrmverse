import { EditorView } from '@codemirror/view';
import { act, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { App } from '../../shell/App';
import { en } from '../../shell/i18n/en';
import { server } from '../../test/server';
import type { ApplyResult, WorkflowSummary } from '../api/studioApi';
import { errorBody, mockStudio, summary } from '../testSupport';

const ID = '22222222-0000-0000-0000-000000000001';
const NEW_ID = '22222222-0000-0000-0000-0000000000aa';
const TEXT = [
  'kind: workflow',
  'id: ' + ID,
  'name: Order sync',
  'entity: xrmv_order',
  'mode: background',
  'trigger: { create: true }',
  'steps:',
  '  # the end',
  '  - stop: { status: succeeded }',
  '',
].join('\n');
const NEW_TEXT = ['kind: workflow', 'name: Fresh', 'entity: account', 'mode: background', '# a comment', 'steps: []', ''].join('\n');
const ROW: WorkflowSummary = summary({ id: ID, name: 'Order sync', mode: 'background', state: 'draft' });

interface Apply {
  query: URLSearchParams;
  body: string;
}

interface EditorMock {
  rows?: WorkflowSummary[];
  managed?: string[];
  /** The export answer: the YAML, or a status with the error envelope's message. */
  yaml?: string;
  exportStatus?: number;
  exportMessage?: string;
  xaml?: string;
  /** The result of a save (dryRun=false); a dry run answers no errors unless `dryRunResult` says otherwise. */
  saveResult?: (body: string) => Partial<ApplyResult>;
  dryRunResult?: (body: string) => Partial<ApplyResult>;
}

const applyResult = (over: Partial<ApplyResult>): ApplyResult => ({
  id: ID,
  name: 'Order sync',
  action: 'updated',
  state: 'draft',
  stateChanged: false,
  assumedShapes: [],
  errors: [],
  ...over,
});

function mockEditor(opts: EditorMock = {}) {
  const calls = mockStudio({ workflows: opts.rows ?? [ROW], managed: opts.managed });
  const applies: Apply[] = [];
  const solutionQueries: string[] = [];
  server.use(
    http.get('*/authoring/workflows/:id', ({ request }) => {
      const format = new URL(request.url).searchParams.get('format');
      if (format === 'xaml') return new HttpResponse(opts.xaml ?? '<Activity x:Class="XrmWorkflow" />', { headers: { 'Content-Type': 'text/xml' } });
      if (opts.exportStatus) return HttpResponse.json(errorBody(opts.exportMessage ?? 'refused'), { status: opts.exportStatus });
      return new HttpResponse(opts.yaml ?? TEXT, { headers: { 'Content-Type': 'text/yaml' } });
    }),
    http.post('*/authoring/workflows/apply', async ({ request }) => {
      const query = new URL(request.url).searchParams;
      const body = await request.text();
      applies.push({ query, body });
      const over = query.get('dryRun') === 'false' ? (opts.saveResult?.(body) ?? {}) : (opts.dryRunResult?.(body) ?? { action: 'unchanged' });
      const r = applyResult({ ...over, ...(query.get('activate') === 'true' && !over.errors?.length ? { state: 'activated', stateChanged: true } : {}) });
      return HttpResponse.json(r, { status: r.errors.length > 0 ? 400 : 200 });
    }),
    http.get('*/api/data/v9.2/solutions', ({ request }) => {
      solutionQueries.push(decodeURIComponent(new URL(request.url).search));
      return HttpResponse.json({
        value: [
          { uniquename: 'contoso', friendlyname: 'Contoso' },
          { uniquename: 'fabrikam', friendlyname: 'Fabrikam' },
        ],
      });
    }),
  );
  return { calls, applies, saves: () => applies.filter((a) => a.query.get('dryRun') === 'false'), solutionQueries };
}

afterEach(() => sessionStorage.clear());

function openExisting() {
  let router: { state: { location: { pathname: string } } } | undefined;
  render(<App initialEntries={[`/studio/workflows/${ID}`]} onRouter={(r) => (router = r as unknown as typeof router)} />);
  return () => router!.state.location.pathname;
}

function openNew(yaml = NEW_TEXT) {
  let router: { state: { location: { pathname: string } } } | undefined;
  render(
    <App
      initialEntries={[{ pathname: '/studio/workflows/new', state: { yaml, name: 'Fresh' } }]}
      onRouter={(r) => (router = r as unknown as typeof router)}
    />,
  );
  return () => router!.state.location.pathname;
}

async function editor(): Promise<EditorView> {
  const dom = await waitFor(() => {
    const found = document.querySelector('.cm-editor');
    expect(found).not.toBeNull();
    return found as HTMLElement;
  });
  return EditorView.findFromDOM(dom)!;
}

async function codeView(): Promise<EditorView> {
  await userEvent.click(await screen.findByRole('tab', { name: en['studio.editor.view.code'] }));
  return editor();
}

function type(view: EditorView, text: string) {
  act(() => view.dispatch({ changes: { from: 0, to: view.state.doc.length, insert: text }, userEvent: 'input.type' }));
}

/** jsdom's Blob has no text(). */
const textOf = (blob: Blob) =>
  new Promise<string>((resolve) => {
    const reader = new FileReader();
    reader.onload = () => resolve(reader.result as string);
    reader.readAsText(blob);
  });

/** Saves (dryRun=false) wait until the test releases them; dry runs answer at once. */
function gateSaves(over: Partial<ApplyResult> = {}, dryRun: (body: string) => Partial<ApplyResult> = () => ({ action: 'unchanged' })) {
  let open!: () => void;
  const gate = new Promise<void>((resolve) => (open = resolve));
  const bodies: string[] = [];
  return {
    bodies,
    release: () => act(() => open()),
    install: () =>
      server.use(
        http.post('*/authoring/workflows/apply', async ({ request }) => {
          const isDryRun = new URL(request.url).searchParams.get('dryRun') !== 'false';
          const body = await request.text();
          if (!isDryRun) {
            bodies.push(body);
            await gate;
          }
          const r = applyResult(isDryRun ? dryRun(body) : { action: 'updated', ...over });
          return HttpResponse.json(r, { status: r.errors.length > 0 ? 400 : 200 });
        }),
      ),
  };
}

/** Inserts a Stop step at the end of the flow through the canvas. */
async function insertStopAtEnd() {
  const slots = screen.getAllByRole('button', { name: en['studio.canvas.insert'] });
  await userEvent.click(slots[slots.length - 1]);
  const palette = await screen.findByRole('dialog', { name: en['studio.canvas.palette'] });
  await userEvent.click(within(palette).getByRole('button', { name: en['studio.step.stop'] }));
}

/** Waits until the editor of the saved process (with its state badge) has replaced the editor of the new one. */
async function savedEditor() {
  await waitFor(() => expect(screen.getByRole('region', { name: en['studio.editor.header'] }).textContent).toContain(en['studio.state.draft']));
}

const saveButton = () => screen.getByRole('button', { name: en['studio.editor.save'] });

describe('EditorPage', () => {
  it('opens a process by id with its name, kind, table, mode and state', async () => {
    mockEditor();
    openExisting();
    expect(await screen.findByRole('heading', { name: 'Order sync' })).toBeInTheDocument();
    const header = screen.getByRole('region', { name: en['studio.editor.header'] });
    expect(header.textContent).toContain(en['studio.kind.workflow']);
    expect(header.textContent).toContain('xrmv_order');
    expect(header.textContent).toContain(en['studio.mode.background']);
    await waitFor(() => expect(header.textContent).toContain(en['studio.state.draft']));
    expect(within(header).queryByText(en['studio.list.managed'])).toBeNull();
    expect(screen.getByRole('region', { name: en['studio.editor.view.canvas'] }).textContent).toContain('Stop: succeeded');
  }, 15_000);

  it('shows the Managed badge for a managed process', async () => {
    mockEditor({ managed: [ID] });
    openExisting();
    const header = await screen.findByRole('region', { name: en['studio.editor.header'] });
    await waitFor(() => expect(within(header).getByText(en['studio.list.managed'])).toBeInTheDocument());
  }, 15_000);

  it('keeps broken text across view switches', async () => {
    mockEditor();
    openExisting();
    await screen.findByRole('heading', { name: 'Order sync' });
    const view = await codeView();
    const broken = TEXT.replace('mode: background', 'mode: [background');
    type(view, broken);
    await userEvent.click(screen.getByRole('tab', { name: en['studio.editor.view.canvas'] }));
    expect(await screen.findByText(en['studio.editor.fixProblems'])).toBeInTheDocument();
    expect(saveButton()).toBeDisabled();
    const again = await codeView();
    expect(again.state.doc.toString()).toBe(broken);
    // The local problem is listed.
    expect(within(screen.getByRole('region', { name: /Problems/ })).getAllByRole('listitem').length).toBeGreaterThan(0);
  }, 15_000);

  it('the code view shows a read-only diagram of the text that follows each edit', async () => {
    mockEditor();
    openExisting();
    await screen.findByRole('heading', { name: 'Order sync' });
    const view = await codeView();
    const diagram = () => screen.getByRole('complementary', { name: en['studio.editor.diagram'] });
    expect(within(diagram()).getByRole('group', { name: 'Stop: succeeded' })).toBeInTheDocument();
    // Read-only: no slots and no card menus; the properties panel is the canvas view's.
    expect(within(diagram()).queryAllByRole('button', { name: en['studio.canvas.insert'] })).toEqual([]);
    expect(within(diagram()).queryAllByRole('button', { name: en['studio.canvas.cardMenu'] })).toEqual([]);
    expect(screen.queryByRole('complementary', { name: en['studio.props.title'] })).toBeNull();

    type(view, TEXT.replace('  - stop: { status: succeeded }', '  - stop: { status: canceled }\n  - update: { record: primary, set: { name: x } }'));
    expect(within(diagram()).getByRole('group', { name: 'Stop: canceled' })).toBeInTheDocument();
    expect(within(diagram()).getByRole('group', { name: 'Update record: primary' })).toBeInTheDocument();

    // Broken text: the last diagram stays, with a note.
    type(view, TEXT.replace('mode: background', 'mode: [background'));
    expect(within(diagram()).getByText(en['studio.editor.diagram.stale'])).toBeInTheDocument();
    expect(within(diagram()).getByRole('group', { name: 'Stop: canceled' })).toBeInTheDocument();
    type(view, TEXT);
    expect(within(diagram()).queryByText(en['studio.editor.diagram.stale'])).toBeNull();
    expect(within(diagram()).getByRole('group', { name: 'Stop: succeeded' })).toBeInTheDocument();
  }, 15_000);

  it('a new document that cannot be read shows no diagram in the code view, only the note', async () => {
    mockEditor();
    openNew('kind: workflow\nname: Fresh\nentity: account\nmode: [x\n');
    const view = await codeView();
    expect(view.state.doc.toString()).toContain('mode: [x');
    const diagram = screen.getByRole('complementary', { name: en['studio.editor.diagram'] });
    expect(within(diagram).getByText(en['studio.editor.diagram.none'])).toBeInTheDocument();
    expect(within(diagram).queryByRole('group', { name: en['studio.canvas.trigger'] })).toBeNull();
  }, 15_000);

  it('failed save keeps the document', async () => {
    const mock = mockEditor({ saveResult: () => ({ errors: [{ message: "The table 'nope' does not exist.", at: { line: 4, column: 9 } }] }) });
    openExisting();
    await screen.findByRole('heading', { name: 'Order sync' });
    const view = await codeView();
    const changed = TEXT.replace('entity: xrmv_order', 'entity: nope');
    type(view, changed);
    await userEvent.click(saveButton());
    const problems = screen.getByRole('region', { name: /Problems/ });
    await waitFor(() => expect(problems.textContent).toContain("The table 'nope' does not exist."));
    expect(problems.textContent).toContain('4:9');
    expect(view.state.doc.toString()).toBe(changed);
    expect(screen.getByText(en['studio.editor.unsaved'])).toBeInTheDocument();
    expect(mock.saves()).toHaveLength(1);
    expect(mock.saves()[0].body).toBe(changed);
  }, 15_000);

  it('first save inserts the id and navigates', async () => {
    // The list does not know the new process yet: the editor of its id shows the state the save answered, without waiting
    // for another request (the wait that made this test slow under load).
    const mock = mockEditor({ rows: [ROW], saveResult: () => ({ id: NEW_ID, name: 'Fresh', action: 'created', state: 'draft' }) });
    const path = openNew();
    await screen.findByRole('heading', { name: 'Fresh' });
    await userEvent.click(saveButton());
    await waitFor(() => expect(path()).toBe(`/studio/workflows/${NEW_ID}`));
    // Only the save's answer can show this badge: the list has no row for the new id.
    await savedEditor();
    expect(mock.saves()[0].body).toBe(NEW_TEXT);
    const view = await codeView();
    // The id right after the kind; the comment survives.
    expect(view.state.doc.toString()).toBe(NEW_TEXT.replace('kind: workflow\n', `kind: workflow\nid: ${NEW_ID}\n`));
    expect(screen.queryByText(en['studio.editor.unsaved'])).toBeNull();
  }, 15_000);

  it('an edit made on the canvas while a save is in flight is kept and stays unsaved', async () => {
    const gate = gateSaves();
    mockEditor();
    gate.install();
    openExisting();
    await screen.findByRole('heading', { name: 'Order sync' });
    type(await codeView(), TEXT.replace('name: Order sync', 'name: Sent'));
    await userEvent.click(screen.getByRole('tab', { name: en['studio.editor.view.canvas'] }));
    await userEvent.click(saveButton());
    await waitFor(() => expect(gate.bodies).toHaveLength(1));
    await insertStopAtEnd();
    gate.release();
    await waitFor(() => expect(saveButton()).toBeEnabled());
    expect(gate.bodies[0]).toBe(TEXT.replace('name: Order sync', 'name: Sent'));
    expect(screen.getByText(en['studio.editor.unsaved'])).toBeInTheDocument();
    const text = (await codeView()).state.doc.toString();
    expect(text).toContain('name: Sent');
    expect(text.match(/- stop:/g)).toHaveLength(2);
  }, 15_000);

  it('after a save that kept a canvas edit, the problems are those of the kept text', async () => {
    const twoStops = (body: string): Partial<ApplyResult> =>
      (body.match(/- stop:/g) ?? []).length > 1 ? { errors: [{ message: 'A second stop is never reached.', at: { line: 10, column: 5 } }] } : { action: 'unchanged' };
    const gate = gateSaves({}, twoStops);
    mockEditor();
    gate.install();
    openExisting();
    await screen.findByRole('heading', { name: 'Order sync' });
    type(await codeView(), TEXT.replace('name: Order sync', 'name: Sent'));
    await userEvent.click(screen.getByRole('tab', { name: en['studio.editor.view.canvas'] }));
    await userEvent.click(saveButton());
    await waitFor(() => expect(gate.bodies).toHaveLength(1));
    await insertStopAtEnd();
    gate.release();
    await waitFor(() => expect(saveButton()).toBeEnabled());
    const problems = screen.getByRole('region', { name: /Problems/ });
    await waitFor(() => expect(problems.textContent).toContain('A second stop is never reached.'), { timeout: 4000 });
  }, 15_000);

  it('a first save keeps an edit made while it was in flight, with the id, and stays unsaved', async () => {
    const gate = gateSaves({ id: NEW_ID, name: 'Fresh', action: 'created' });
    mockEditor({ rows: [ROW, summary({ id: NEW_ID, name: 'Fresh', entity: 'account', mode: 'background' })] });
    gate.install();
    const path = openNew();
    await screen.findByRole('heading', { name: 'Fresh' });
    await userEvent.click(saveButton());
    await waitFor(() => expect(gate.bodies).toHaveLength(1));
    await insertStopAtEnd();
    gate.release();
    await waitFor(() => expect(path()).toBe(`/studio/workflows/${NEW_ID}`));
    await savedEditor();
    expect(gate.bodies[0]).toBe(NEW_TEXT);
    const text = (await codeView()).state.doc.toString();
    expect(text.startsWith(`kind: workflow\nid: ${NEW_ID}\n`)).toBe(true);
    expect(text).toContain('# a comment');
    expect(text).toContain('- stop:');
    expect(screen.getByText(en['studio.editor.unsaved'])).toBeInTheDocument();
  }, 15_000);

  it('Activate asks to save unsaved changes first', async () => {
    const mock = mockEditor({ saveResult: () => ({ action: 'updated' }) });
    openExisting();
    await screen.findByRole('heading', { name: 'Order sync' });
    const view = await codeView();
    type(view, TEXT.replace('name: Order sync', 'name: Order sync 2'));
    await userEvent.click(screen.getByRole('button', { name: en['studio.cmd.activate'] }));
    const dialog = await screen.findByRole('dialog');
    expect(dialog.textContent).toContain(en['studio.editor.saveFirst.body']);
    expect(mock.calls.states).toEqual([]);
    await userEvent.click(within(dialog).getByRole('button', { name: en['studio.editor.saveFirst.confirm'] }));
    // The save activates in the same transaction (apply?activate=true).
    await waitFor(() => expect(mock.saves()).toHaveLength(1));
    expect(mock.saves()[0].query.get('activate')).toBe('true');
    expect(mock.calls.states).toEqual([]);
    const header = screen.getByRole('region', { name: en['studio.editor.header'] });
    await waitFor(() => expect(header.textContent).toContain(en['studio.state.activated']));
  }, 15_000);

  it('Activate without changes activates the saved definition at once', async () => {
    const mock = mockEditor();
    openExisting();
    await screen.findByRole('heading', { name: 'Order sync' });
    await userEvent.click(await screen.findByRole('button', { name: en['studio.cmd.activate'] }));
    await waitFor(() => expect(mock.calls.states).toEqual([`${ID}/activate`]));
    expect(mock.saves()).toHaveLength(0);
  }, 15_000);

  it('not representable export shows the read-only page with the message and View XAML', async () => {
    const message = "The process 'Order sync' is not representable in YAML: it has a dialog step.";
    mockEditor({ exportStatus: 400, exportMessage: message, xaml: '<Activity x:Class="Dialog" />' });
    openExisting();
    expect(await screen.findByText(message)).toBeInTheDocument();
    expect(document.querySelector('.cm-editor')).toBeNull();
    expect(screen.queryByRole('button', { name: en['studio.editor.save'] })).toBeNull();
    expect(await screen.findByRole('button', { name: en['studio.cmd.activate'] })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: en['studio.cmd.delete'] })).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: en['studio.editor.viewXaml'] }));
    const dialog = await screen.findByRole('dialog');
    await waitFor(() => expect(dialog.textContent).toContain('<Activity x:Class="Dialog" />'));
  }, 15_000);

  it('any other export failure shows the error page', async () => {
    mockEditor({ exportStatus: 500, exportMessage: 'The database is down.' });
    openExisting();
    expect(await screen.findByText(/The database is down\./)).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: en['studio.cmd.activate'] })).toBeNull();
  }, 15_000);

  it('leaving with changes asks', async () => {
    mockEditor();
    const path = openExisting();
    await screen.findByRole('heading', { name: 'Order sync' });
    const view = await codeView();
    type(view, TEXT.replace('name: Order sync', 'name: Changed'));
    await userEvent.click(screen.getByRole('link', { name: en['studio.editor.back'] }));
    const dialog = await screen.findByRole('dialog');
    expect(dialog.textContent).toContain(en['studio.editor.discard.body']);
    await userEvent.click(within(dialog).getByRole('button', { name: en['common.cancel'] }));
    expect(path()).toBe(`/studio/workflows/${ID}`);
    await userEvent.click(screen.getByRole('link', { name: en['studio.editor.back'] }));
    await userEvent.click(within(await screen.findByRole('dialog')).getByRole('button', { name: en['studio.editor.discard.confirm'] }));
    await waitFor(() => expect(path()).toBe('/studio/workflows'));
  }, 15_000);

  it('Export YAML downloads name.yaml', async () => {
    mockEditor();
    openExisting();
    await screen.findByRole('heading', { name: 'Order sync' });
    const blobs: Blob[] = [];
    const names: string[] = [];
    // jsdom has no object URLs.
    const created = URL.createObjectURL;
    const revoked = URL.revokeObjectURL;
    URL.createObjectURL = vi.fn((b: Blob | MediaSource) => {
      blobs.push(b as Blob);
      return 'blob:x';
    });
    URL.revokeObjectURL = vi.fn();
    const click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(function (this: HTMLAnchorElement) {
      names.push(this.download);
    });
    try {
      await userEvent.click(screen.getByRole('button', { name: en['studio.editor.more'] }));
      await userEvent.click(await screen.findByRole('menuitem', { name: en['studio.editor.exportYaml'] }));
      expect(names).toEqual(['Order sync.yaml']);
      expect(await textOf(blobs[0])).toBe(TEXT);
    } finally {
      URL.createObjectURL = created;
      URL.revokeObjectURL = revoked;
      click.mockRestore();
    }
  }, 15_000);

  it('Add to solution passes solution= on the next save', async () => {
    const mock = mockEditor();
    openExisting();
    await screen.findByRole('heading', { name: 'Order sync' });
    await userEvent.click(screen.getByRole('button', { name: en['studio.editor.more'] }));
    await userEvent.click(await screen.findByRole('menuitem', { name: en['studio.editor.addToSolution'] }));
    const dialog = await screen.findByRole('dialog');
    await userEvent.click(within(dialog).getByRole('combobox'));
    await userEvent.click(await screen.findByRole('option', { name: 'Fabrikam' }));
    await userEvent.click(within(dialog).getByRole('button', { name: en['studio.editor.solution.choose'] }));
    expect(mock.solutionQueries[0]).toContain('$filter=ismanaged eq false and isvisible eq true');
    // The dialog closes before the page is reachable again.
    await userEvent.click(await screen.findByRole('button', { name: en['studio.editor.save'] }));
    await waitFor(() => expect(mock.saves()).toHaveLength(1));
    expect(mock.saves()[0].query.get('solution')).toBe('fabrikam');
  }, 15_000);

  it('live dry run lists server errors and assumed shapes; a click moves the cursor', async () => {
    mockEditor({
      dryRunResult: (body) =>
        body.includes('nope')
          ? { errors: [{ message: "The table 'nope' does not exist.", at: { line: 4, column: 9 } }], assumedShapes: ['xrmv_order.xrmv_total as money'] }
          : { action: 'unchanged' },
    });
    openExisting();
    await screen.findByRole('heading', { name: 'Order sync' });
    const view = await codeView();
    type(view, TEXT.replace('entity: xrmv_order', 'entity: nope'));
    const problems = screen.getByRole('region', { name: /Problems/ });
    await waitFor(() => expect(problems.textContent).toContain("The table 'nope' does not exist."), { timeout: 3000 });
    expect(problems.textContent).toContain('xrmv_order.xrmv_total as money');
    await userEvent.click(within(problems).getByRole('button', { name: /The table 'nope' does not exist\./ }));
    const head = view.state.selection.main.head;
    const line = view.state.doc.lineAt(head);
    expect([line.number, head - line.from + 1]).toEqual([4, 9]);
  }, 15_000);

  it('on the canvas, a click on a problem selects the card that holds its line', async () => {
    mockEditor({ dryRunResult: () => ({ errors: [{ message: "The status 'succeeded' is not valid here.", at: { line: 9, column: 15 } }] }) });
    openExisting();
    await screen.findByRole('heading', { name: 'Order sync' });
    const problems = screen.getByRole('region', { name: /Problems/ });
    const problem = await within(problems).findByRole('button', { name: /is not valid here/ }, { timeout: 3000 });
    const card = screen.getByRole('group', { name: 'Stop: succeeded' });
    expect(card).not.toHaveAttribute('aria-current');
    expect(within(card).getByLabelText('1 problem')).toBeInTheDocument();
    await userEvent.click(problem);
    expect(card).toHaveAttribute('aria-current', 'true');
  }, 15_000);

  it('the properties panel beside the canvas edits the selected card', async () => {
    mockEditor();
    openExisting();
    await screen.findByRole('heading', { name: 'Order sync' });
    const panel = screen.getByRole('complementary', { name: en['studio.props.title'] });
    expect(within(panel).getByText(en['studio.props.none'])).toBeInTheDocument();
    await userEvent.click(screen.getByRole('group', { name: 'Stop: succeeded' }));
    await userEvent.selectOptions(within(panel).getByRole('combobox', { name: /^Status/ }), en['studio.props.enum.canceled']);
    expect(screen.getByRole('group', { name: 'Stop: canceled' })).toBeInTheDocument();
    expect(screen.getByRole('region', { name: en['studio.editor.header'] }).textContent).toContain(en['studio.editor.unsaved']);
    await userEvent.click(screen.getByRole('group', { name: en['studio.canvas.trigger'] }));
    expect(within(panel).getByRole('textbox', { name: /^Name/ })).toHaveValue('Order sync');
    expect(within(panel).queryByRole('combobox', { name: en['studio.props.trigger.createStage'] })).toBeNull();
    const view = await codeView();
    expect(view.state.doc.toString()).toContain('  # the end\n  - stop: { status: canceled }\n');
  }, 15_000);

  it('Delete asks, deletes and goes back to the list', async () => {
    const mock = mockEditor();
    const path = openExisting();
    await screen.findByRole('heading', { name: 'Order sync' });
    await userEvent.click(screen.getByRole('button', { name: en['studio.editor.more'] }));
    await userEvent.click(await screen.findByRole('menuitem', { name: en['studio.cmd.delete'] }));
    await userEvent.click(within(await screen.findByRole('dialog')).getByRole('button', { name: en['studio.delete.confirm'] }));
    await waitFor(() => expect(path()).toBe('/studio/workflows'));
    expect(mock.calls.deletes).toEqual([`workflows(${ID})`]);
  }, 15_000);
});
