import { EditorView } from '@codemirror/view';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { parse } from 'yaml';
import { afterEach, describe, expect, it } from 'vitest';
import { App } from '../../shell/App';
import { en } from '../../shell/i18n/en';
import type { WorkflowSummary } from '../api/studioApi';
import { mockStudio, summary } from '../testSupport';
import { newProcessYaml } from './NewProcessDialog';

const WF = '11111111-0000-0000-0000-000000000001';
const RULE = '11111111-0000-0000-0000-000000000002';
const ACTION = '11111111-0000-0000-0000-000000000003';
const ROWS: WorkflowSummary[] = [
  summary({ id: WF, name: 'OnCreate', kind: 'workflow', entity: 'xrmv_order', mode: 'realtime', state: 'activated' }),
  summary({ id: RULE, name: 'Amount rule', kind: 'businessRule', entity: 'xrmv_order', mode: null, state: 'draft' }),
  summary({ id: ACTION, name: 'Recalculate', kind: 'action', entity: 'none', mode: null, state: 'draft', unsupportedSteps: 2 }),
];

afterEach(() => sessionStorage.clear());

/** The editor's YAML, read from the code view. */
async function editorText(): Promise<string> {
  await userEvent.click(await screen.findByRole('tab', { name: en['studio.editor.view.code'] }));
  const dom = await waitFor(() => {
    const found = document.querySelector('.cm-editor');
    expect(found).not.toBeNull();
    return found as HTMLElement;
  });
  return EditorView.findFromDOM(dom)!.state.doc.toString();
}

async function table() {
  return screen.findByRole('table', { name: en['studio.list.title'] });
}

const names = (t: HTMLElement) =>
  within(t)
    .getAllByRole('row')
    .slice(1)
    .map((r) => within(within(r).getAllByRole('cell')[1]!).getByRole('link').textContent);

const rowOf = (t: HTMLElement, name: string) => within(t).getAllByRole('row').find((r) => r.textContent?.includes(name))!;

describe('WorkflowList', () => {
  it('lists the processes with name, kind, table, mode and state', async () => {
    mockStudio({ workflows: ROWS });
    render(<App initialEntries={['/studio/workflows']} />);
    const t = await table();
    const wf = rowOf(t, 'OnCreate');
    expect(wf.textContent).toContain(en['studio.kind.workflow']);
    expect(wf.textContent).toContain('xrmv_order');
    expect(wf.textContent).toContain(en['studio.mode.realtime']);
    expect(wf.textContent).toContain(en['studio.state.activated']);
    const rule = rowOf(t, 'Amount rule');
    expect(rule.textContent).toContain(en['studio.kind.businessRule']);
    expect(rule.textContent).toContain(en['studio.state.draft']);
    const action = rowOf(t, 'Recalculate');
    expect(action.textContent).toContain(en['studio.list.global']);
    expect(action.textContent).toContain('2');
    // Every name opens the editor by id, a process with unsupported steps too.
    expect(within(action).getByRole('link', { name: /Recalculate/ }).getAttribute('href')).toBe(`/studio/workflows/${ACTION}`);
  });

  it('shows the Managed badge for the ids managedIds reports', async () => {
    mockStudio({ workflows: ROWS, managed: [RULE] });
    render(<App initialEntries={['/studio/workflows']} />);
    const t = await table();
    await waitFor(() => expect(within(rowOf(t, 'Amount rule')).getByText(en['studio.list.managed'])).toBeInTheDocument());
    expect(within(rowOf(t, 'OnCreate')).queryByText(en['studio.list.managed'])).toBeNull();
  });

  it('filters by kind and by a search on the name', async () => {
    mockStudio({ workflows: ROWS });
    render(<App initialEntries={['/studio/workflows']} />);
    const t = await table();
    expect(names(t)).toHaveLength(3);
    await userEvent.click(screen.getByRole('combobox', { name: en['studio.list.kindFilter'] }));
    await userEvent.click(screen.getByRole('option', { name: en['studio.kind.businessRule'] }));
    expect(names(t)).toEqual(['Amount rule']);
    await userEvent.click(screen.getByRole('combobox', { name: en['studio.list.kindFilter'] }));
    await userEvent.click(screen.getByRole('option', { name: en['studio.list.allKinds'] }));
    expect(names(t)).toHaveLength(3);
    await userEvent.type(screen.getByRole('searchbox', { name: en['studio.list.search'] }), 'recalc');
    expect(names(t)).toEqual(['Recalculate']);
  });

  it('filters by table, apart from the search', async () => {
    mockStudio({ workflows: [...ROWS, summary({ id: '11111111-0000-0000-0000-000000000004', name: 'Account sync', entity: 'account' })] });
    render(<App initialEntries={['/studio/workflows']} />);
    const t = await table();
    await userEvent.click(screen.getByRole('combobox', { name: en['studio.list.tableFilter'] }));
    expect(screen.getAllByRole('option').map((o) => o.textContent)).toEqual([en['studio.list.allTables'], en['studio.list.global'], 'account', 'xrmv_order']);
    await userEvent.click(screen.getByRole('option', { name: 'xrmv_order' }));
    expect(names(t)).toEqual(['OnCreate', 'Amount rule']);
    // The search is on the name only: a table name finds nothing.
    await userEvent.type(screen.getByRole('searchbox', { name: en['studio.list.search'] }), 'xrmv');
    expect(within(t).getAllByRole('row')).toHaveLength(1);
  });

  it('Open opens the selected process by id', async () => {
    mockStudio({ workflows: ROWS, exports: { [ACTION]: 'kind: action\nname: Recalculate\nentity: none\nmessage: new_Recalculate\nsteps: []\n' } });
    render(<App initialEntries={['/studio/workflows']} />);
    const t = await table();
    expect(screen.queryByRole('button', { name: en['studio.cmd.open'] })).toBeNull();
    await userEvent.click(within(rowOf(t, 'Recalculate')).getByRole('radio'));
    await userEvent.click(screen.getByRole('button', { name: en['studio.cmd.open'] }));
    expect(await screen.findByRole('heading', { name: 'Recalculate' })).toBeInTheDocument();
  });

  it('says 1 unsupported step in the singular', async () => {
    mockStudio({ workflows: [summary({ id: WF, name: 'Old', unsupportedSteps: 1 })] });
    render(<App initialEntries={['/studio/workflows']} />);
    expect(await screen.findByText(en['studio.list.unsupportedOne'])).toBeInTheDocument();
  });

  it('a selected row enables Activate, Deactivate and Delete', async () => {
    const calls = mockStudio({ workflows: ROWS });
    render(<App initialEntries={['/studio/workflows']} />);
    const t = await table();
    expect(screen.queryByRole('button', { name: en['studio.cmd.activate'] })).toBeNull();
    await userEvent.click(within(rowOf(t, 'Amount rule')).getByRole('radio'));
    const activate = screen.getByRole('button', { name: en['studio.cmd.activate'] });
    expect(activate).toBeEnabled();
    // A draft process cannot be deactivated.
    expect(screen.getByRole('button', { name: en['studio.cmd.deactivate'] })).toBeDisabled();
    expect(screen.getByRole('button', { name: en['studio.cmd.delete'] })).toBeEnabled();
    const before = calls.lists.length;
    await userEvent.click(activate);
    await waitFor(() => expect(calls.states).toEqual([`${RULE}/activate`]));
    await waitFor(() => expect(calls.lists.length).toBeGreaterThan(before));
  });

  it('a refused activation shows the server message in a message bar', async () => {
    mockStudio({ workflows: ROWS, writeStatus: 403, writeMessage: 'Principal user is missing prvActivateBusinessRule privilege.' });
    render(<App initialEntries={['/studio/workflows']} />);
    const t = await table();
    await userEvent.click(within(rowOf(t, 'Amount rule')).getByRole('radio'));
    await userEvent.click(screen.getByRole('button', { name: en['studio.cmd.activate'] }));
    const message = await screen.findByText(/missing prvActivateBusinessRule privilege/);
    expect(message.closest('.fui-MessageBar')).not.toBeNull();
  });

  it('Delete asks first, then deletes by id and reloads', async () => {
    let rows = ROWS;
    const calls = mockStudio({ workflows: () => rows });
    render(<App initialEntries={['/studio/workflows']} />);
    const t = await table();
    await userEvent.click(within(rowOf(t, 'OnCreate')).getByRole('radio'));
    await userEvent.click(screen.getByRole('button', { name: en['studio.cmd.delete'] }));
    const dialog = await screen.findByRole('dialog');
    expect(dialog.textContent).toContain('OnCreate');
    expect(calls.deletes).toEqual([]);
    rows = ROWS.filter((r) => r.id !== WF);
    await userEvent.click(within(dialog).getByRole('button', { name: en['studio.delete.confirm'] }));
    await waitFor(() => expect(calls.deletes).toEqual([`workflows(${WF})`]));
    await waitFor(() => expect(names(t)).toEqual(['Amount rule', 'Recalculate']));
  });

  it('New builds the minimal YAML and opens the editor with it', async () => {
    mockStudio({ workflows: ROWS });
    render(<App initialEntries={['/studio/workflows']} />);
    await table();
    await userEvent.click(screen.getByRole('button', { name: en['studio.cmd.new'] }));
    const dialog = await screen.findByRole('dialog');
    await userEvent.type(within(dialog).getByRole('textbox', { name: en['studio.new.name'] }), 'Order: follow up');
    await userEvent.type(within(dialog).getByRole('textbox', { name: en['studio.new.table'] }), 'xrmv_order');
    await userEvent.click(within(dialog).getByRole('radio', { name: en['studio.mode.realtime'] }));
    await userEvent.click(within(dialog).getByRole('button', { name: en['studio.new.create'] }));
    expect(parse(await editorText())).toEqual({ kind: 'workflow', name: 'Order: follow up', entity: 'xrmv_order', mode: 'realtime', steps: [] });
  }, 15_000);

  it('New asks the message of an action and allows a global one', async () => {
    mockStudio({ workflows: ROWS });
    render(<App initialEntries={['/studio/workflows']} />);
    await table();
    await userEvent.click(screen.getByRole('button', { name: en['studio.cmd.new'] }));
    const dialog = await screen.findByRole('dialog');
    await userEvent.click(within(dialog).getByRole('combobox', { name: en['studio.new.kind'] }));
    await userEvent.click(screen.getByRole('option', { name: en['studio.kind.action'] }));
    await userEvent.type(within(dialog).getByRole('textbox', { name: en['studio.new.name'] }), 'Recalculate all');
    const create = within(dialog).getByRole('button', { name: en['studio.new.create'] });
    expect(create).toBeDisabled();
    await userEvent.click(within(dialog).getByRole('checkbox', { name: en['studio.new.global'] }));
    expect(within(dialog).getByRole('textbox', { name: en['studio.new.table'] })).toBeDisabled();
    expect(within(dialog).queryByRole('radio', { name: en['studio.mode.realtime'] })).toBeNull();
    expect(create).toBeDisabled();
    await userEvent.type(within(dialog).getByRole('textbox', { name: en['studio.new.message'] }), 'new_RecalculateAll');
    await userEvent.click(create);
    expect(parse(await editorText())).toEqual({ kind: 'action', name: 'Recalculate all', entity: 'none', message: 'new_RecalculateAll', steps: [] });
  }, 15_000);

  it('newProcessYaml writes the keys each kind requires', () => {
    expect(parse(newProcessYaml({ kind: 'businessRule', name: 'R', entity: 'account' }))).toEqual({ kind: 'businessRule', name: 'R', entity: 'account', steps: [] });
    expect(parse(newProcessYaml({ kind: 'workflow', name: 'true', entity: 'account', mode: 'background' }))).toEqual({
      kind: 'workflow',
      name: 'true',
      entity: 'account',
      mode: 'background',
      steps: [],
    });
    expect(parse(newProcessYaml({ kind: 'action', name: 'A', entity: 'account', message: 'new_A' }))).toEqual({
      kind: 'action',
      name: 'A',
      entity: 'account',
      message: 'new_A',
      steps: [],
    });
  });
});
