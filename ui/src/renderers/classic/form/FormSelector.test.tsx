import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import { formOf } from './formTestSupport';
import { FormSelector } from './FormSelector';

const one = formOf([], { id: 'f1', name: 'Main' });
const two = formOf([], { id: 'f2', name: 'Information' });

describe('FormSelector', () => {
  it('is not shown for a single form', () => {
    render(<FormSelector forms={[one]} current={one} onSelect={() => {}} />);
    expect(screen.queryByRole('combobox')).toBeNull();
  });

  it('lists the forms and reports the choice', async () => {
    const onSelect = vi.fn();
    render(<FormSelector forms={[one, two]} current={one} onSelect={onSelect} />);
    const select = screen.getByRole('combobox', { name: 'Form' });
    expect(select).toHaveValue('f1');
    await userEvent.selectOptions(select, 'f2');
    expect(onSelect).toHaveBeenCalledWith(two);
  });
});
