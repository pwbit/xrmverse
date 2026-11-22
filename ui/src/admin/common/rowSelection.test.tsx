import { FluentProvider, Switch, Table, TableBody, TableCell, TableHeader, TableRow, webLightTheme } from '@fluentui/react-components';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import { RowCommand, SelectCell, SelectHeaderCell, selectableRow, useRowSelection } from './rowSelection';

type Item = { id: string; name: string };

function Harness({ items }: { items: Item[] }) {
  const selection = useRowSelection(items, (i) => i.id);
  return (
    <FluentProvider theme={webLightTheme}>
      <div role="toolbar">{selection.selected && <RowCommand>Act on {selection.selected.name}</RowCommand>}</div>
      <Table>
        <TableHeader>
          <TableRow>
            <SelectHeaderCell />
          </TableRow>
        </TableHeader>
        <TableBody>
          {items.map((i) => (
            <TableRow key={i.id} {...selectableRow(selection, i)}>
              <SelectCell selection={selection} item={i} name={i.name} />
              <TableCell>{i.name}</TableCell>
              <TableCell>
                <Switch aria-label={`switch ${i.name}`} />
              </TableCell>
            </TableRow>
          ))}
        </TableBody>
      </Table>
    </FluentProvider>
  );
}

const items = [
  { id: '1', name: 'One' },
  { id: '2', name: 'Two' },
];

describe('rowSelection', () => {
  it('shows the row commands only while a row is selected', async () => {
    const user = userEvent.setup();
    render(<Harness items={items} />);
    expect(screen.queryByRole('button', { name: /Act on/ })).toBeNull();
    await user.click(screen.getByRole('radio', { name: 'Select One' }));
    expect(screen.getByRole('button', { name: 'Act on One' })).toBeInTheDocument();
    expect(screen.getByText('One').closest('tr')).toHaveAttribute('aria-selected', 'true');
    // A click on another row's cell moves the selection; a click on the selected row clears it.
    await user.click(screen.getByText('Two'));
    expect(screen.getByRole('button', { name: 'Act on Two' })).toBeInTheDocument();
    expect(screen.getByRole('radio', { name: 'Select One' })).not.toBeChecked();
    await user.click(screen.getByText('Two'));
    expect(screen.queryByRole('button', { name: /Act on/ })).toBeNull();
  });

  it('a control inside the row keeps its click', async () => {
    const user = userEvent.setup();
    render(<Harness items={items} />);
    await user.click(screen.getByRole('switch', { name: 'switch One' }));
    expect(screen.queryByRole('button', { name: /Act on/ })).toBeNull();
  });

  it('a row that leaves the list is no longer selected', async () => {
    const user = userEvent.setup();
    const { rerender } = render(<Harness items={items} />);
    await user.click(screen.getByRole('radio', { name: 'Select Two' }));
    rerender(<Harness items={items.slice(0, 1)} />);
    expect(screen.queryByRole('button', { name: /Act on/ })).toBeNull();
  });
});
