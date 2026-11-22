import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { useState } from 'react';
import { describe, expect, it } from 'vitest';
import { en } from '../../shell/i18n/en';
import { PlainText, setAt } from '../model/edit';
import { parseText } from '../model/read';
import { valueAt } from '../model/values';
import { ConditionBuilder } from './ConditionBuilder';
import { context } from './testMeta';
import { conditionForYaml } from './valueModel';

let written: unknown[] = [];

function Harness({ initial }: { initial: unknown }) {
  const [value, setValue] = useState(initial);
  return (
    <ConditionBuilder
      label="When"
      value={value}
      onChange={(next) => {
        written.push(next);
        setValue(next);
      }}
      ctx={context()}
    />
  );
}

function setup(initial: unknown) {
  written = [];
  const user = userEvent.setup();
  render(<Harness initial={initial} />);
  return user;
}

const last = () => written.at(-1);
const group = (name: string) => screen.getByRole('group', { name });

describe('ConditionBuilder', () => {
  it('a row is value · op · value, the right value typed by the left column', async () => {
    const user = setup({ field: '', op: 'eq', value: '' });
    const row = group('When');
    await user.selectOptions(within(row).getByRole('combobox', { name: en['studio.props.column'] }), 'Employees (numberofemployees)');
    expect(last()).toEqual({ field: 'numberofemployees', op: 'eq', value: '' });
    await user.selectOptions(within(row).getByRole('combobox', { name: en['studio.props.condition.op'] }), en['studio.props.op.gt']);
    // The right value of an integer column is a number field.
    await user.type(within(row).getByRole('spinbutton', { name: 'Value of With' }), '1000');
    expect(last()).toEqual({ field: 'numberofemployees', op: 'gt', value: '1000' });
    expect(Object.keys(last() as object)).toEqual(['field', 'op', 'value']);
  });

  it('op null hides the right value and leaves it out', async () => {
    const user = setup({ field: 'name', op: 'eq', value: 'x' });
    expect(screen.getByRole('group', { name: en['studio.props.condition.right'] })).toBeInTheDocument();
    await user.selectOptions(screen.getByRole('combobox', { name: en['studio.props.condition.op'] }), en['studio.props.op.null']);
    expect(last()).toEqual({ field: 'name', op: 'null' });
    expect(screen.queryByRole('group', { name: en['studio.props.condition.right'] })).toBeNull();
    await user.selectOptions(screen.getByRole('combobox', { name: en['studio.props.condition.op'] }), en['studio.props.op.ne']);
    expect(last()).toEqual({ field: 'name', op: 'ne', value: '' });
  });

  it('the left operand is a dynamic value: no literal, no empty value, no concat', () => {
    setup({ field: 'name', op: 'eq', value: 'x' });
    const left = screen.getByRole('combobox', { name: en['studio.props.condition.left'] });
    const options = within(left).getAllByRole('option').map((o) => o.textContent);
    expect(options).not.toContain(en['studio.props.source.literal']);
    expect(options).not.toContain(en['studio.props.source.empty']);
    expect(options).not.toContain(en['studio.props.source.concat']);
    expect(options).toContain(en['studio.props.source.related']);
  });

  it('builds all / any groups and not, in the 2.21 shapes', async () => {
    const user = setup({ all: [] });
    await user.click(screen.getByRole('button', { name: en['studio.props.condition.addRow'] }));
    expect(last()).toEqual({ all: [{ field: '', op: 'eq', value: '' }] });
    const first = group('Condition 1');
    await user.selectOptions(within(first).getByRole('combobox', { name: en['studio.props.column'] }), 'Account Name (name)');
    await user.selectOptions(within(first).getByRole('combobox', { name: en['studio.props.condition.op'] }), en['studio.props.op.notNull']);
    await user.click(screen.getAllByRole('button', { name: en['studio.props.condition.addGroup'] })[0]);
    expect(last()).toEqual({ all: [{ field: 'name', op: 'notNull' }, { all: [{ field: '', op: 'eq', value: '' }] }] });

    // The inner group becomes any, its row is negated.
    const second = group('Condition 2');
    await user.selectOptions(within(second).getByRole('combobox', { name: 'Condition 2' }), en['studio.props.condition.any']);
    const inner = within(second).getByRole('group', { name: 'Condition 1' });
    await user.selectOptions(within(inner).getByRole('combobox', { name: 'Condition 1' }), en['studio.props.condition.not']);
    expect(last()).toEqual({ all: [{ field: 'name', op: 'notNull' }, { any: [{ not: { field: '', op: 'eq', value: '' } }] }] });
    const negated = within(second).getByRole('group', { name: en['studio.props.condition.negated'] });
    await user.selectOptions(within(negated).getByRole('combobox', { name: en['studio.props.column'] }), 'Revenue (revenue)');
    await user.selectOptions(within(negated).getByRole('combobox', { name: en['studio.props.condition.op'] }), en['studio.props.op.null']);
    expect(last()).toEqual({ all: [{ field: 'name', op: 'notNull' }, { any: [{ not: { field: 'revenue', op: 'null' } }] }] });

    // Removing a row.
    await user.click(screen.getAllByRole('button', { name: 'Remove Condition 1' })[0]);
    expect(last()).toEqual({ all: [{ any: [{ not: { field: 'revenue', op: 'null' } }] }] });
  });

  it('a comparison turned into a group becomes its first condition, and back', async () => {
    const user = setup({ field: 'name', op: 'notNull' });
    await user.selectOptions(screen.getByRole('combobox', { name: 'When' }), en['studio.props.condition.any']);
    expect(last()).toEqual({ any: [{ field: 'name', op: 'notNull' }] });
    await user.selectOptions(screen.getAllByRole('combobox', { name: 'When' })[0], en['studio.props.condition.compare']);
    expect(last()).toEqual({ field: 'name', op: 'notNull' });
  });

  it('reads op: null written plain, and writes it back plain, as the server writes it', () => {
    const { doc } = parseText(['kind: workflow', 'name: X', 'entity: account', 'mode: realtime', 'steps:', '  - if:', '      when: { all: [{ field: name, op: null }] }', '      then: []', ''].join('\n'));
    const read = valueAt(doc, ['steps', 0, 'if', 'when']);
    expect(read).toEqual({ all: [{ field: 'name', op: null }] });
    render(<ConditionBuilder label="When" value={read} onChange={() => undefined} ctx={context()} />);
    expect(screen.getByRole('combobox', { name: en['studio.props.condition.op'] })).toHaveValue('null');

    const yaml = conditionForYaml({ all: [{ field: 'name', op: 'null' }, { not: { arg: 'x', op: 'gt', value: 'null' } }] });
    expect(yaml).toEqual({ all: [{ field: 'name', op: new PlainText('null') }, { not: { arg: 'x', op: new PlainText('gt'), value: 'null' } }] });
    const target = parseText(['kind: workflow', 'name: X', 'entity: account', 'mode: realtime', 'steps:', '  - if:', '      when: { all: [] }', '      then: []', ''].join('\n')).doc;
    setAt(target, ['steps', 0, 'if', 'when'], yaml);
    // The operator is plain; the text 'null' as a value is quoted.
    expect(target.toString()).toContain("      when: { all: [{ field: name, op: null }, { not: { arg: x, op: gt, value: 'null' } }] }\n");
    expect(parseText(target.toString()).problems).toEqual([]);
  });
});
