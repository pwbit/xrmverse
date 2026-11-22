import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { useState } from 'react';
import { describe, expect, it, vi } from 'vitest';
import type { AttributeMeta } from '../../admin/tables/metadata';
import { en } from '../../shell/i18n/en';
import { column, context } from './testMeta';
import { ValueEditor } from './ValueEditor';
import type { LiteralType, ValueContext } from './valueModel';

// The record picker searches the server; here it offers one record, so the editor's wiring is what is tested.
vi.mock('../../admin/common/EntityPicker', () => ({
  EntityPicker: (p: { label: string; onSelect(picked: { id: string; name: string }): void }) => (
    <button type="button" onClick={() => p.onSelect({ id: '6a2d0f1e-0000-0000-0000-000000000001', name: 'Contoso' })}>
      {p.label}
    </button>
  ),
}));

/** The values the editor wrote, the last one last. */
let written: unknown[] = [];

function Harness({ initial, ctx, col, literal }: { initial: unknown; ctx?: ValueContext; col?: AttributeMeta; literal?: LiteralType }) {
  const [value, setValue] = useState(initial);
  return (
    <ValueEditor
      label="Target"
      value={value}
      onChange={(next) => {
        written.push(next);
        setValue(next);
      }}
      ctx={ctx ?? context()}
      column={col}
      literal={literal}
    />
  );
}

function setup(props: Parameters<typeof Harness>[0]) {
  written = [];
  const user = userEvent.setup();
  render(<Harness {...props} />);
  return user;
}

const last = () => written.at(-1);
const source = () => screen.getByRole('combobox', { name: 'Target' });

describe('ValueEditor', () => {
  it('types a literal by its column: a number field for an integer', async () => {
    const user = setup({ initial: '', col: column('account', 'numberofemployees') });
    const number = screen.getByRole('spinbutton', { name: 'Value of Target' });
    await user.type(number, '12');
    expect(last()).toBe('12');
  });

  it('offers the options of a choice column and writes the number', async () => {
    const user = setup({ initial: '', col: column('account', 'industrycode') });
    await user.selectOptions(screen.getByRole('combobox', { name: 'Value of Target' }), 'Agriculture (2)');
    expect(last()).toBe('2');
  });

  it('writes a Boolean as true or false, labelled by the column', async () => {
    const user = setup({ initial: 'false', col: column('account', 'donotemail') });
    await user.selectOptions(screen.getByRole('combobox', { name: 'Value of Target' }), 'Do Not Allow');
    expect(last()).toBe('true');
  });

  it('a text column takes text, and the empty value is null', async () => {
    const user = setup({ initial: 'x', col: column('account', 'name') });
    expect(screen.getByRole('textbox', { name: 'Value of Target' })).toHaveValue('x');
    await user.selectOptions(source(), en['studio.props.source.empty']);
    expect(last()).toBeNull();
  });

  it('a lookup column takes no text literal', () => {
    setup({ initial: null, col: column('account', 'primarycontactid') });
    const options = within(source()).getAllByRole('option').map((o) => o.textContent);
    expect(options).not.toContain(en['studio.props.source.literal']);
    expect(options).toContain(en['studio.props.source.lookup']);
  });

  it('a column of the primary record writes { field: name }', async () => {
    const user = setup({ initial: '' });
    await user.selectOptions(source(), en['studio.props.source.field']);
    expect(last()).toEqual({ field: '' });
    await user.selectOptions(screen.getByRole('combobox', { name: en['studio.props.column'] }), 'Account Name (name)');
    expect(last()).toEqual({ field: 'name' });
  });

  it('a related record through a lookup writes { field, related }, with the columns of the lookup target', async () => {
    const user = setup({ initial: '' });
    await user.selectOptions(source(), en['studio.props.source.related']);
    const lookup = screen.getByRole('combobox', { name: en['studio.props.lookup'] });
    // Only the lookups of the primary table are offered.
    expect(within(lookup).getAllByRole('option').map((o) => o.textContent)).toEqual([en['studio.props.chooseColumn'], 'Parent Account (parentaccountid)', 'Primary Contact (primarycontactid)']);
    await user.selectOptions(lookup, 'Primary Contact (primarycontactid)');
    expect(last()).toEqual({ field: '', related: 'primarycontactid' });
    await user.selectOptions(screen.getByRole('combobox', { name: en['studio.props.column'] }), 'Email (emailaddress1)');
    expect(last()).toEqual({ field: 'emailaddress1', related: 'primarycontactid' });
    expect(Object.keys(last() as object)).toEqual(['field', 'related']);
  });

  it('a created record lists only the earlier create and sendEmail ids', async () => {
    const ctx = context({
      earlier: [
        { id: 'newTask', type: 'create', entity: 'task' },
        { id: 'act', type: 'performAction' },
        { id: 'mail', type: 'sendEmail', entity: 'email' },
      ],
    });
    const user = setup({ initial: '', ctx });
    await user.selectOptions(source(), en['studio.props.source.created']);
    expect(last()).toEqual({ field: '', created: 'newTask' });
    const step = screen.getByRole('combobox', { name: en['studio.props.step'] });
    expect(within(step).getAllByRole('option').map((o) => o.getAttribute('value')).filter(Boolean)).toEqual(['newTask', 'mail']);
    await user.selectOptions(screen.getByRole('combobox', { name: en['studio.props.column'] }), 'Subject (subject)');
    expect(last()).toEqual({ field: 'subject', created: 'newTask' });
  });

  it('offers created and output only when an earlier step can be named', () => {
    setup({ initial: '' });
    const options = within(source()).getAllByRole('option').map((o) => o.textContent);
    expect(options).not.toContain(en['studio.props.source.created']);
    expect(options).not.toContain(en['studio.props.source.output']);
    expect(options).not.toContain(en['studio.props.source.arg']);
  });

  it('an output of an earlier action step writes { output, of }', async () => {
    const user = setup({ initial: '', ctx: context({ earlier: [{ id: 'act', type: 'performAction' }] }) });
    await user.selectOptions(source(), en['studio.props.source.output']);
    await user.type(screen.getByRole('textbox', { name: en['studio.props.outputName'] }), 'Total');
    expect(last()).toEqual({ output: 'Total', of: 'act' });
  });

  it('an action offers its input arguments', async () => {
    const user = setup({ initial: '', ctx: context({ kind: 'action', inputArgs: ['Increment'] }) });
    await user.selectOptions(source(), en['studio.props.source.arg']);
    expect(last()).toEqual({ arg: 'Increment' });
  });

  it('now writes { now: true }', async () => {
    const user = setup({ initial: '' });
    await user.selectOptions(source(), en['studio.props.source.now']);
    expect(last()).toEqual({ now: 'true' });
  });

  it('operators build { add: [...] } over operands that are values of their own', async () => {
    const user = setup({ initial: '5', col: column('account', 'numberofemployees') });
    await user.selectOptions(source(), en['studio.props.source.add']);
    expect(last()).toEqual({ add: ['5'] });
    await user.click(screen.getByRole('button', { name: en['studio.props.addOperand'] }));
    expect(last()).toEqual({ add: ['5', ''] });
    const second = screen.getByRole('group', { name: 'Operand 2' });
    await user.selectOptions(within(second).getByRole('combobox', { name: 'Operand 2' }), en['studio.props.source.field']);
    await user.selectOptions(within(second).getByRole('combobox', { name: en['studio.props.column'] }), 'Employees (numberofemployees)');
    expect(last()).toEqual({ add: ['5', { field: 'numberofemployees' }] });
    // Another operator keeps the operands.
    await user.selectOptions(source(), en['studio.props.source.multiply']);
    expect(last()).toEqual({ multiply: ['5', { field: 'numberofemployees' }] });
    await user.click(screen.getByRole('button', { name: 'Remove operand 1' }));
    expect(last()).toEqual({ multiply: [{ field: 'numberofemployees' }] });
  });

  it('a lookup literal writes { lookup, id, name } from the record picked', async () => {
    const user = setup({ initial: null, col: column('account', 'primarycontactid') });
    await user.selectOptions(source(), en['studio.props.source.lookup']);
    expect(last()).toEqual({ lookup: 'contact', id: '' });
    await user.click(screen.getByRole('button', { name: 'Search Contact' }));
    expect(last()).toEqual({ lookup: 'contact', id: '6a2d0f1e-0000-0000-0000-000000000001', name: 'Contoso' });
    expect(Object.keys(last() as object)).toEqual(['lookup', 'id', 'name']);
  });

  it('a record reference writes { record }', async () => {
    const user = setup({ initial: null, col: column('account', 'parentaccountid') });
    await user.selectOptions(source(), en['studio.props.source.record']);
    expect(last()).toEqual({ record: 'primary' });
    await user.selectOptions(screen.getByRole('combobox', { name: en['studio.props.record'] }), en['studio.props.record.related']);
    await user.selectOptions(screen.getByRole('combobox', { name: en['studio.props.lookup'] }), 'Parent Account (parentaccountid)');
    expect(last()).toEqual({ record: { related: 'parentaccountid' } });
  });

  it('without the metadata, a column is typed by its logical name', async () => {
    const user = setup({ initial: { field: '' }, ctx: context({ meta: { entity: () => undefined, tables: () => undefined } }) });
    await user.type(screen.getByRole('textbox', { name: en['studio.props.column'] }), 'name');
    expect(last()).toEqual({ field: 'name' });
  });

  it('a value the editor does not know is shown as other, unchanged', () => {
    setup({ initial: { mystery: 'x' } });
    expect(source()).toHaveValue('other');
    expect(screen.getByText(en['studio.props.otherHint'])).toBeInTheDocument();
    expect(written).toEqual([]);
  });
});
