import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { useState } from 'react';
import { describe, expect, it, vi } from 'vitest';
import type { ViewColumn } from '../../model/view';
import { CardList } from '../modern/CardList';
import { ViewGrid, type GridRow } from './ViewGrid';

const COLUMNS = [{ name: 'name', attribute: 'name', width: 200 }] as ViewColumn[];
const ROWS: GridRow[] = ['a', 'b', 'c'].map((id) => ({ key: id, id, cells: { name: `Account ${id}` } }));

function Harness({ onOpen, initial = [] }: { onOpen: (id: string) => void; initial?: string[] }) {
  const [selected, setSelected] = useState<ReadonlySet<string>>(new Set(initial));
  return (
    <>
      <ViewGrid label="Accounts" columns={COLUMNS} headers={{ name: 'Name' }} rows={ROWS} onSort={() => {}} onOpen={onOpen} selection={{ selected, onChange: setSelected }} />
      <output data-testid="selected">{[...selected].sort().join(',')}</output>
    </>
  );
}

const selected = () => screen.getByTestId('selected').textContent;

describe('ViewGrid selection', () => {
  it('a checkbox per row selects without opening; a row click still opens', async () => {
    const onOpen = vi.fn();
    render(<Harness onOpen={onOpen} />);
    await userEvent.click(screen.getByRole('checkbox', { name: 'Select Account a' }));
    await userEvent.click(screen.getByRole('checkbox', { name: 'Select Account c' }));
    expect(selected()).toBe('a,c');
    expect(onOpen).not.toHaveBeenCalled();
    await userEvent.click(screen.getByRole('checkbox', { name: 'Select Account a' }));
    expect(selected()).toBe('c');
    await userEvent.click(screen.getAllByRole('row')[2]!.querySelectorAll('[role="gridcell"]')[1] as HTMLElement);
    expect(onOpen).toHaveBeenCalledWith('b', expect.anything());
  });

  it('a click in the selection cell beside the checkbox neither opens the record nor drops the selection', async () => {
    const onOpen = vi.fn();
    render(<Harness onOpen={onOpen} initial={['a', 'b']} />);
    const cell = screen.getAllByRole('row')[2]!.querySelectorAll('[role="gridcell"]')[0] as HTMLElement;
    expect(cell).toHaveAttribute('data-select-cell');
    await userEvent.click(cell);
    expect(onOpen).not.toHaveBeenCalled();
    expect(selected()).toBe('a,b');
  });

  it('the header checkbox selects and clears the page', async () => {
    render(<Harness onOpen={() => {}} initial={['b']} />);
    const all = screen.getByRole('checkbox', { name: 'Select all records on this page' });
    expect(all).not.toBeChecked();
    expect(all).toHaveProperty('indeterminate', true);
    await userEvent.click(all);
    expect(selected()).toBe('a,b,c');
    expect(all).toBeChecked();
    await userEvent.click(all);
    expect(selected()).toBe('');
  });

  it('without selection there is no checkbox column', () => {
    render(<ViewGrid label="Accounts" columns={COLUMNS} headers={{ name: 'Name' }} rows={ROWS} onSort={() => {}} onOpen={() => {}} />);
    expect(screen.queryByRole('checkbox')).toBeNull();
    expect(within(screen.getByRole('grid')).getAllByRole('columnheader')).toHaveLength(1);
  });

  it('the cards have the same checkboxes', async () => {
    const onChange = vi.fn();
    render(<CardList label="Accounts" columns={COLUMNS} headers={{ name: 'Name' }} rows={ROWS} onSort={() => {}} onOpen={() => {}} selection={{ selected: new Set(['a']), onChange }} />);
    expect(screen.getByRole('checkbox', { name: 'Select Account a' })).toBeChecked();
    await userEvent.click(screen.getByRole('checkbox', { name: 'Select Account b' }));
    expect([...onChange.mock.calls[0][0]].sort()).toEqual(['a', 'b']);
    await userEvent.click(screen.getByRole('checkbox', { name: 'Select all records on this page' }));
    expect([...onChange.mock.calls[1][0]].sort()).toEqual(['a', 'b', 'c']);
  });
});
