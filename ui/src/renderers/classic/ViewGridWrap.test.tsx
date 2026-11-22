import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import type { ViewColumn } from '../../model/view';
import { ViewGrid } from './ViewGrid';

const col = (name: string): ViewColumn => ({ name, attribute: name, width: 100, isHidden: false });
const LONG = 'GBS EMEA (vendorcare-emea@example.test)';

function show(wrap?: string[]) {
  render(
    <ViewGrid
      label="Cases"
      columns={[col('ticketnumber'), col('queue'), col('title')]}
      headers={{ ticketnumber: 'Case Number', queue: 'Queue', title: 'Case Title' }}
      rows={[{ key: '1', id: '1', cells: { ticketnumber: 'CAS-1', queue: LONG, title: 'A title' } }]}
      onSort={() => {}}
      onOpen={() => {}}
      wrap={wrap}
    />,
  );
}

describe('ViewGrid text wrapping', () => {
  it('keeps every cell on one line by default, the full text in its tooltip', () => {
    show();
    const cell = screen.getByText(LONG);
    expect(cell.getAttribute('title')).toBe(LONG);
    expect(cell.hasAttribute('data-wrap')).toBe(false);
    expect(screen.getByText('CAS-1').closest('[title]')?.getAttribute('title')).toBe('CAS-1');
  });

  it('wraps only the columns the view names, whatever their case', () => {
    show(['QUEUE']);
    const cell = screen.getByText(LONG);
    expect(cell.hasAttribute('data-wrap')).toBe(true);
    expect(cell.hasAttribute('title')).toBe(false);
    expect(screen.getByText('A title').hasAttribute('data-wrap')).toBe(false);
  });
});
