import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { describe, expect, it, vi } from 'vitest';
import type { ViewColumn } from '../../model/view';
import { readableText } from '../../renderers/modern/choiceColors';
import { loadColumnKinds, type ColumnKind } from './columnKinds';
import { KanbanBoard, type KanbanBoardProps } from './KanbanBoard';

// A stable api object, as the signed-in one is.
const AUTH = vi.hoisted(() => ({ api: {} }));
vi.mock('../../shell/auth/AuthProvider', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../../shell/auth/AuthProvider')>()),
  useOptionalAuth: () => AUTH,
}));
vi.mock('./columnKinds', () => ({ loadColumnKinds: vi.fn() }));

const col = (attribute: string): ViewColumn => ({ name: attribute, width: 100, attribute, isHidden: false });
const COLUMNS = [col('name'), col('city'), col('phone'), col('email'), col('fax')];
const HEADERS = { name: 'Name', city: 'City', phone: 'Phone', email: 'Email', fax: 'Fax' };
const COLUMN: Extract<ColumnKind, { kind: 'choice' }> = {
  kind: 'choice',
  type: 'Picklist',
  options: [
    { value: 1, label: 'New' },
    { value: 2, label: 'Working' },
    { value: 3, label: 'Done' },
  ],
};
const ROWS = [
  { id: 'a', name: 'Alpha', city: 'Rome', phone: '1', email: 'a@x', fax: 'f', stage: 1 },
  { id: 'b', name: 'Beta', stage: 2 },
  { id: 'c', name: 'Gamma', stage: null },
  { id: 'd', name: 'Delta', stage: 9 },
  { id: 'e', name: 'Epsilon', stage: 1 },
];

function board(props: Partial<KanbanBoardProps> = {}) {
  const onMove = vi.fn(async () => {});
  const onOpen = vi.fn();
  render(
    <FluentProvider theme={webLightTheme}>
      <KanbanBoard
        label="Pipeline"
        columns={COLUMNS}
        headers={HEADERS}
        groupBy="stage"
        column={COLUMN}
        rows={ROWS}
        primaryId="id"
        onMove={onMove}
        onOpen={onOpen}
        {...props}
      />
    </FluentProvider>,
  );
  return { onMove, onOpen };
}
const list = (name: string) => screen.getByRole('list', { name });

describe('KanbanBoard', () => {
  it('shows a column per option, then (none) and Other when needed, with counts', () => {
    board();
    const names = screen.getAllByRole('list').map((l) => l.getAttribute('aria-label'));
    expect(names).toEqual(['New', 'Working', 'Done', '(none)', 'Other']);
    expect(within(list('New')).getAllByRole('listitem')).toHaveLength(2);
    expect(within(list('(none)')).getAllByRole('listitem')).toHaveLength(1);
    expect(within(list('Other')).getAllByRole('listitem')).toHaveLength(1);
    expect(screen.getByText('(none)').closest('[data-column]')?.textContent).toContain('1');
  });

  it('omits (none) and Other when no row needs them', () => {
    board({ rows: [ROWS[0]] });
    expect(screen.getAllByRole('list').map((l) => l.getAttribute('aria-label'))).toEqual(['New', 'Working', 'Done']);
  });

  it('a card shows the first column as a link and up to three more cells', async () => {
    const { onOpen } = board();
    const card = within(list('New')).getAllByRole('listitem')[0];
    expect(within(card).getByRole('button', { name: 'Alpha' })).toBeInTheDocument();
    expect(card.textContent).toContain('Rome');
    expect(card.textContent).toContain('a@x');
    expect(within(card).queryByText('f')).toBeNull();
    await userEvent.click(within(card).getByRole('button', { name: 'Alpha' }));
    expect(onOpen).toHaveBeenCalledWith('a');
  });

  it('Move to... moves the card at once and calls onMove with the body', async () => {
    const { onMove } = board();
    const card = within(list('New')).getAllByRole('listitem')[0];
    await userEvent.click(within(card).getByRole('button', { name: 'Move to…' }));
    expect(screen.queryByRole('menuitem', { name: 'Other' })).toBeNull();
    expect(screen.queryByRole('menuitem', { name: 'New' })).toBeNull();
    await userEvent.click(await screen.findByRole('menuitem', { name: 'Done' }));
    expect(onMove).toHaveBeenCalledWith('a', { stage: 3 });
    expect(within(list('Done')).getByText('Alpha')).toBeInTheDocument();
    expect(within(list('New')).queryByText('Alpha')).toBeNull();
  });

  it('a refused move puts the card back and shows the server message', async () => {
    const onMove = vi.fn(async () => {
      throw new Error('Access denied');
    });
    board({ onMove });
    const card = within(list('New')).getAllByRole('listitem')[0];
    await userEvent.click(within(card).getByRole('button', { name: 'Move to…' }));
    await userEvent.click(await screen.findByRole('menuitem', { name: 'Done' }));
    await waitFor(() => expect(within(list('New')).getByText('Alpha')).toBeInTheDocument());
    expect(within(list('Done')).queryByText('Alpha')).toBeNull();
    expect(screen.getByText(/Access denied/)).toBeInTheDocument();
  });

  it('dropping a card on a column moves it', () => {
    const { onMove } = board();
    const data: Record<string, string> = {};
    const dataTransfer = { setData: (k: string, v: string) => (data[k] = v), getData: (k: string) => data[k] ?? '', effectAllowed: '' };
    const card = within(list('Working')).getAllByRole('listitem')[0];
    fireEvent.dragStart(card, { dataTransfer });
    const target = list('Done');
    fireEvent.dragOver(target, { dataTransfer });
    fireEvent.drop(target, { dataTransfer });
    expect(onMove).toHaveBeenCalledWith('b', { stage: 3 });
    expect(within(list('Done')).getByText('Beta')).toBeInTheDocument();
  });

  it('statuscode moves send the state of the target when it differs', async () => {
    const status: Extract<ColumnKind, { kind: 'choice' }> = {
      kind: 'choice',
      type: 'Status',
      options: [
        { value: 1, label: 'Active', state: 0 },
        { value: 2, label: 'Inactive', state: 1 },
      ],
    };
    const { onMove } = board({ groupBy: 'statuscode', column: status, rows: [{ id: 'a', name: 'Alpha', statuscode: 1, statecode: 0 }] });
    const card = within(list('Active')).getAllByRole('listitem')[0];
    await userEvent.click(within(card).getByRole('button', { name: 'Move to…' }));
    await userEvent.click(await screen.findByRole('menuitem', { name: 'Inactive' }));
    expect(onMove).toHaveBeenCalledWith('a', { statuscode: 2, statecode: 1 });
  });

  it('Move to... offers (none) to a card outside it while the (none) column is shown', async () => {
    const { onMove } = board();
    const card = within(list('New')).getAllByRole('listitem')[0];
    await userEvent.click(within(card).getByRole('button', { name: 'Move to…' }));
    await userEvent.click(await screen.findByRole('menuitem', { name: '(none)' }));
    expect(onMove).toHaveBeenCalledWith('a', { stage: null });
    expect(within(list('(none)')).getByText('Alpha')).toBeInTheDocument();
  });

  it('Move to... does not offer (none) to a card already in it, nor when no (none) column is shown', async () => {
    board();
    const inNone = within(list('(none)')).getAllByRole('listitem')[0];
    await userEvent.click(within(inNone).getByRole('button', { name: 'Move to…' }));
    expect(await screen.findByRole('menuitem', { name: 'New' })).toBeInTheDocument();
    expect(screen.queryByRole('menuitem', { name: '(none)' })).toBeNull();
  });

  it('Move to... has no (none) item when the (none) column is not shown', async () => {
    board({ rows: [ROWS[0]] });
    await userEvent.click(within(list('New')).getByRole('button', { name: 'Move to…' }));
    await screen.findByRole('menuitem', { name: 'Done' });
    expect(screen.queryByRole('menuitem', { name: '(none)' })).toBeNull();
  });

  describe('drops', () => {
    const drag = (from: string, to: string) => {
      const data: Record<string, string> = {};
      const dataTransfer = { setData: (k: string, v: string) => (data[k] = v), getData: (k: string) => data[k] ?? '', effectAllowed: '' };
      fireEvent.dragStart(within(list(from)).getAllByRole('listitem')[0], { dataTransfer });
      fireEvent.dragOver(list(to), { dataTransfer });
      fireEvent.drop(list(to), { dataTransfer });
    };

    it('dropping on (none) sends null', () => {
      const { onMove } = board();
      drag('Working', '(none)');
      expect(onMove).toHaveBeenCalledWith('b', { stage: null });
    });

    it('dropping on the own column does not call onMove', () => {
      const { onMove } = board();
      drag('Working', 'Working');
      drag('(none)', '(none)');
      expect(onMove).not.toHaveBeenCalled();
    });

    it('dropping on Other is ignored', () => {
      const { onMove } = board();
      drag('Working', 'Other');
      expect(onMove).not.toHaveBeenCalled();
      expect(within(list('Working')).getByText('Beta')).toBeInTheDocument();
    });
  });

  it('names each column as a region and keeps the card list a list', () => {
    board();
    const region = screen.getByRole('region', { name: 'New' });
    expect(within(region).getByRole('list', { name: 'New' })).toBeInTheDocument();
    // The count badge is decoration: the list's items say how many there are.
    expect(region.querySelector('.fui-Badge')?.getAttribute('aria-hidden')).toBe('true');
  });
});

describe('KanbanBoard choice badges', () => {
  const GREEN = '#107C10';
  const RED = '#D13438';
  const COLOURED: Extract<ColumnKind, { kind: 'choice' }> = {
    ...COLUMN,
    options: [{ value: 1, label: 'New', color: GREEN }, { value: 2, label: 'Working' }, { value: 3, label: 'Done' }],
  };
  const badges = (root: ParentNode) => [...root.querySelectorAll('.fui-Badge')].filter((b) => b.getAttribute('aria-hidden') !== 'true') as HTMLElement[];

  it("a lane's header is its option's badge when the option has a colour", async () => {
    vi.mocked(loadColumnKinds).mockResolvedValue({});
    board({ column: COLOURED, table: 'xv_lead' });
    // Let the cards' palette load settle (no state update after the test).
    await waitFor(() => expect(loadColumnKinds).toHaveBeenCalled());
    await act(async () => {});
    const lane = screen.getByRole('region', { name: 'New' });
    const header = within(lane).getByText('New');
    expect(header).toHaveClass('fui-Badge');
    expect(header).toHaveStyle({ backgroundColor: GREEN, color: readableText(GREEN) });
    expect(within(screen.getByRole('region', { name: 'Working' })).getByText('Working')).not.toHaveClass('fui-Badge');
  });

  it("a card's fields show the badge of a coloured choice", async () => {
    vi.mocked(loadColumnKinds).mockResolvedValue({
      city: { kind: 'choice', type: 'Picklist', options: [{ value: 7, label: 'Rome', color: RED }] },
    });
    board({ table: 'xv_lead', rows: [{ id: 'a', name: 'Alpha', city: 7, stage: 1 }] });
    const card = within(list('New')).getByRole('listitem');
    await waitFor(() => expect(badges(card).map((b) => b.textContent)).toEqual(['Rome']));
    expect(badges(card)[0]).toHaveStyle({ backgroundColor: RED });
    expect(loadColumnKinds).toHaveBeenCalledWith(expect.anything(), 'xv_lead', ['name', 'city', 'phone', 'email', 'fax'], 1033);
  });
});
