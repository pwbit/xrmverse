import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { loadColumnKinds, type ColumnKind } from '../../apps/views/columnKinds';
import type { ViewColumn } from '../../model/view';
import { parsePresentation } from '../../parsers/presentationJson';
import { CHOICE_TEXT_DARK, CHOICE_TEXT_LIGHT } from '../../shell/theme';
import { readableText } from './choiceColors';
import { PresentationProvider } from './PresentationContext';
import type { GridRow, ViewGridProps } from '../classic/ViewGrid';
import { ViewGrid as ClassicViewGrid } from '../classic/ViewGrid';
import { ModernViewGrid } from './ModernViewGrid';

// A stable api object, as the signed-in one is.
const AUTH = vi.hoisted(() => ({ api: {} }));
vi.mock('../../shell/auth/AuthProvider', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../../shell/auth/AuthProvider')>()),
  useOptionalAuth: () => AUTH,
}));
vi.mock('../../apps/views/columnKinds', () => ({ loadColumnKinds: vi.fn() }));

afterEach(() => vi.unstubAllGlobals());

const col = (name: string): ViewColumn => ({ name, attribute: name, width: 100 }) as ViewColumn;
const columns = ['name', 'a', 'b', 'c', 'd', 'e'].map(col);
const headers = { name: 'Name', a: 'A', b: 'B', c: 'C', d: 'D', e: 'E' };
const rows: GridRow[] = [
  { key: '1', id: 'id1', cells: { name: 'Contoso', a: 'x', b: '', c: 'z', d: 'w', e: 'v' } },
  { key: '2', id: 'id2', cells: { name: 'Fabrikam', a: '', b: '', c: '', d: '', e: '' } },
];

function props(over: Partial<ViewGridProps> = {}): ViewGridProps {
  return { label: 'Accounts', columns, headers, rows, onSort: vi.fn(), onOpen: vi.fn(), ...over };
}

function mockMatch(matches: boolean) {
  vi.stubGlobal('matchMedia', (query: string) => ({ matches, media: query, addEventListener: () => {}, removeEventListener: () => {} }));
}

describe('ModernViewGrid cards', () => {
  it('renders rows as cards: title link, up to 4 header: value lines, empty skipped', async () => {
    const p = props({ layout: 'cards' });
    render(<ModernViewGrid {...p} />);
    const items = within(screen.getByRole('list', { name: 'Accounts' })).getAllByRole('listitem');
    expect(items).toHaveLength(2);
    expect(items[0].textContent).toContain('Contoso');
    expect(items[0].textContent).toContain('A: x');
    expect(items[0].textContent).toContain('C: z');
    expect(items[0].textContent).toContain('D: w');
    expect(items[0].textContent).not.toContain('B:');
    expect(items[0].textContent).not.toContain('E:');
    expect(items[1].textContent).not.toContain(':');
    await userEvent.click(screen.getByRole('button', { name: 'Contoso' }));
    expect(p.onOpen).toHaveBeenCalledWith('id1');
  });

  it('Sort by menu sorts and toggles descending on the same column', async () => {
    const onSort = vi.fn();
    const { rerender } = render(<ModernViewGrid {...props({ layout: 'cards', onSort })} />);
    await userEvent.click(screen.getByRole('button', { name: 'Sort by' }));
    await userEvent.click(screen.getByRole('menuitem', { name: 'A' }));
    expect(onSort).toHaveBeenLastCalledWith({ attribute: 'a', descending: false, linkAlias: undefined });
    rerender(<ModernViewGrid {...props({ layout: 'cards', onSort, sort: { attribute: 'a', descending: false } })} />);
    await userEvent.click(screen.getByRole('button', { name: 'Sort by' }));
    await userEvent.click(screen.getByRole('menuitem', { name: /^A/ }));
    expect(onSort).toHaveBeenLastCalledWith({ attribute: 'a', descending: true, linkAlias: undefined });
  });

  it('shows cards on a narrow screen without layout, the grid otherwise', () => {
    mockMatch(true);
    const { unmount } = render(<ModernViewGrid {...props()} />);
    expect(screen.getByRole('list', { name: 'Accounts' })).toBeInTheDocument();
    unmount();
    mockMatch(false);
    render(<ModernViewGrid {...props()} />);
    expect(screen.getByRole('grid', { name: 'Accounts' })).toBeInTheDocument();
  });

  it('the classic grid ignores layout', () => {
    render(<ClassicViewGrid {...props({ layout: 'cards' })} />);
    expect(screen.getByRole('grid', { name: 'Accounts' })).toBeInTheDocument();
  });
});

describe('ModernViewGrid choice badges', () => {
  const GREEN = '#107C10';
  const RED = '#D13438';
  const KINDS: Record<string, ColumnKind> = {
    priority: { kind: 'choice', type: 'Picklist', options: [{ value: 1, label: 'Low' }, { value: 2, label: 'High', color: GREEN }] },
    tags: {
      kind: 'choice',
      type: 'MultiSelectPicklist',
      options: [{ value: 1, label: 'Red', color: RED }, { value: 3, label: 'Green', color: GREEN }, { value: 4, label: 'Plain' }],
    },
  };
  const choiceColumns = ['name', 'priority', 'tags'].map(col);
  const choiceHeaders = { name: 'Name', priority: 'Priority', tags: 'Tags' };
  const choiceRows: GridRow[] = [
    { key: '1', id: 'id1', cells: { name: 'Contoso', priority: 'High', tags: 'Green; Red' }, values: { name: 'Contoso', priority: 2, tags: '3,1' } },
    { key: '2', id: 'id2', cells: { name: 'Fabrikam', priority: 'Low', tags: 'Red; Plain' }, values: { name: 'Fabrikam', priority: 1, tags: '1,4' } },
  ];
  const choiceProps = (over: Partial<ViewGridProps> = {}) =>
    props({ table: 'xv_case', columns: choiceColumns, headers: choiceHeaders, rows: choiceRows, ...over });
  const badges = (root: ParentNode = document) => [...root.querySelectorAll('.fui-Badge')] as HTMLElement[];

  afterEach(() => vi.mocked(loadColumnKinds).mockReset());

  it('grid: a coloured option is a badge, an uncoloured one plain text; multi-select one badge per value', async () => {
    vi.mocked(loadColumnKinds).mockResolvedValue(KINDS);
    mockMatch(false);
    render(<ModernViewGrid {...choiceProps()} />);
    // The text shows at once; the badge once the column kinds are read.
    const high = await screen.findByText('High', { selector: '.fui-Badge' });
    expect(high).toHaveStyle({ backgroundColor: GREEN, color: readableText(GREEN) });
    const [, first, second] = screen.getAllByRole('row');
    expect(badges(first!).map((b) => b.textContent)).toEqual(['High', 'Green', 'Red']);
    // Low has no colour, and Plain in "1,4" has none: the formatted text stays.
    expect(badges(second!)).toEqual([]);
    expect(second!.textContent).toContain('Low');
    expect(second!.textContent).toContain('Red; Plain');
    // The first cell stays the record link.
    expect(within(first!).getByRole('button', { name: 'Contoso' })).toBeInTheDocument();
  });

  it('a first cell without a record id keeps its text, in the grid and on the cards', async () => {
    vi.mocked(loadColumnKinds).mockResolvedValue(KINDS);
    mockMatch(false);
    const firstChoice = [col('priority'), col('tags')];
    const row: GridRow = { key: 'x', cells: { priority: 'High', tags: 'Green; Red' }, values: { priority: 2, tags: '3,1' } };
    const { unmount } = render(<ModernViewGrid {...choiceProps({ columns: firstChoice, rows: [row] })} />);
    await screen.findByText('Green', { selector: '.fui-Badge' });
    expect(screen.getByText('High')).not.toHaveClass('fui-Badge');
    unmount();
    render(<ModernViewGrid {...choiceProps({ columns: firstChoice, rows: [row], layout: 'cards' })} />);
    await screen.findByText('Green', { selector: '.fui-Badge' });
    expect(screen.getByText('High')).not.toHaveClass('fui-Badge');
  });

  it("the overlay's choices override the badge text colour", async () => {
    vi.mocked(loadColumnKinds).mockResolvedValue(KINDS);
    mockMatch(false);
    const other = readableText(GREEN) === CHOICE_TEXT_DARK ? CHOICE_TEXT_LIGHT : CHOICE_TEXT_DARK;
    const { model } = parsePresentation(JSON.stringify({ version: 1, choices: { 'xv_case.priority': { 2: { text: other } } } }));
    render(
      <PresentationProvider model={model}>
        <ModernViewGrid {...choiceProps()} />
      </PresentationProvider>,
    );
    expect(await screen.findByText('High', { selector: '.fui-Badge' })).toHaveStyle({ color: other });
  });

  it('cards: the field lines show the badge', async () => {
    vi.mocked(loadColumnKinds).mockResolvedValue(KINDS);
    render(<ModernViewGrid {...choiceProps({ layout: 'cards' })} />);
    const items = within(screen.getByRole('list', { name: 'Accounts' })).getAllByRole('listitem');
    await waitFor(() => expect(badges(items[0]!).map((b) => b.textContent)).toEqual(['High', 'Green', 'Red']));
    expect(items[0]!.textContent).toContain('Priority: High');
    expect(badges(items[1]!)).toEqual([]);
    expect(items[1]!.textContent).toContain('Priority: Low');
  });

  it('the classic grid shows no badge for the same data', async () => {
    vi.mocked(loadColumnKinds).mockResolvedValue(KINDS);
    render(<ClassicViewGrid {...choiceProps()} />);
    expect(screen.getByText('High')).not.toHaveClass('fui-Badge');
    expect(badges()).toEqual([]);
    expect(loadColumnKinds).not.toHaveBeenCalled();
  });
});
