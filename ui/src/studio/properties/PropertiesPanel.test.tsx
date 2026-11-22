import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { useState } from 'react';
import { describe, expect, it } from 'vitest';
import { XrmApi } from '../../shell/api/client';
import { en } from '../../shell/i18n/en';
import { server } from '../../test/server';
import { Canvas } from '../canvas/Canvas';
import { useEditorDocument } from '../editor/useEditorDocument';
import { parseText } from '../model/read';
import type { StepPath } from '../model/types';
import { PropertiesPanel } from './PropertiesPanel';

const REALTIME = [
  'kind: workflow',
  'name: Flow',
  'entity: account',
  'mode: realtime',
  'runAs: owner',
  'trigger:',
  '  create: { stage: pre }',
  '  update: { columns: [name, revenue], stage: pre }',
  'steps:',
  '  - update:',
  '      record: primary # the record',
  '      set:',
  '        name: X',
  '  - if:',
  '      when: { field: name, op: null }',
  '      then:',
  '        - stop: { status: canceled }',
  '',
].join('\n');

/** The document's text after the last change. */
let current = '';

function Harness({ text, api, initial }: { text: string; api?: XrmApi; initial?: StepPath | 'trigger' }) {
  const doc = useEditorDocument(undefined, text);
  const [selected, setSelected] = useState<StepPath | 'trigger' | undefined>(initial);
  current = doc.text;
  if (!doc.model) return <p>broken: {doc.localProblems.map((p) => p.message).join('; ')}</p>;
  return (
    <>
      <Canvas model={doc.model} selected={selected} onSelect={setSelected} mutate={doc.mutate} problemsByLine={new Map()} />
      <PropertiesPanel api={api} model={doc.model} doc={doc.doc} selected={selected} mutate={doc.mutate} />
    </>
  );
}

function setup(text: string, opts: { api?: XrmApi; initial?: StepPath | 'trigger' } = {}) {
  const user = userEvent.setup();
  render(<Harness text={text} {...opts} />);
  return user;
}

const panel = () => within(screen.getByRole('complementary', { name: en['studio.props.title'] }));
const card = (name: string) => screen.getByRole('group', { name });
const reread = () => {
  const { model, problems } = parseText(current);
  expect(problems).toEqual([]);
  return model!;
};

describe('PropertiesPanel', () => {
  it('asks for a card while none is selected', () => {
    setup(REALTIME);
    expect(panel().getByText(en['studio.props.none'])).toBeInTheDocument();
  });

  it('the trigger card shows the header: mode, scope, run as, the triggers with their update columns and stage', async () => {
    const user = setup(REALTIME);
    await user.click(card(en['studio.canvas.trigger']));
    const p = panel();
    expect(p.getByRole('heading', { name: en['studio.props.header.title'] })).toBeInTheDocument();
    expect(p.getByRole('textbox', { name: /^Name/ })).toHaveValue('Flow');
    expect(p.getByRole('combobox', { name: /^Mode/ })).toHaveValue('realtime');
    expect(p.getByRole('combobox', { name: en['studio.props.header.scope'] })).toHaveValue('user');
    expect(p.getByRole('combobox', { name: en['studio.props.header.runAs'] })).toHaveValue('owner');
    expect(p.getByRole('checkbox', { name: en['studio.canvas.trigger.create'] })).toBeChecked();
    expect(p.getByRole('checkbox', { name: en['studio.canvas.trigger.updateAny'] })).toBeChecked();
    expect(p.getByRole('checkbox', { name: en['studio.canvas.trigger.delete'] })).not.toBeChecked();
    expect(p.getByRole('combobox', { name: en['studio.props.trigger.createStage'] })).toHaveValue('pre');
    expect(p.getByRole('combobox', { name: en['studio.props.trigger.updateStage'] })).toHaveValue('pre');
    const columns = p.getByRole('list', { name: en['studio.props.trigger.columns'] });
    expect(within(columns).getAllByRole('listitem').map((item) => item.textContent)).toEqual(['name', 'revenue']);
    expect(p.getByRole('checkbox', { name: en['studio.props.header.logFailures'] })).toBeChecked();
    expect(p.queryByRole('checkbox', { name: en['studio.props.header.autoDelete'] })).toBeNull();
  });

  it('switching to background hides the stages and removes what a background workflow does not take', async () => {
    const user = setup(REALTIME, { initial: 'trigger' });
    await user.selectOptions(panel().getByRole('combobox', { name: /^Mode/ }), en['studio.mode.background']);
    expect(current).toContain(['mode: background', 'trigger:', '  create: true', '  update: { columns: [name, revenue] }', 'steps:'].join('\n'));
    expect(current).not.toContain('runAs');
    expect(panel().queryByRole('combobox', { name: en['studio.props.trigger.createStage'] })).toBeNull();
    expect(panel().queryByRole('combobox', { name: en['studio.props.trigger.updateStage'] })).toBeNull();
    expect(panel().queryByRole('combobox', { name: en['studio.props.header.runAs'] })).toBeNull();
    expect(panel().getByRole('checkbox', { name: en['studio.props.header.autoDelete'] })).not.toBeChecked();
    expect(reread().mode).toBe('background');
  });

  it('writes header settings at their place and leaves out the defaults', async () => {
    const user = setup(REALTIME, { initial: 'trigger' });
    const p = panel();
    await user.type(p.getByRole('textbox', { name: /^Name/ }), ' 2');
    expect(current).toContain('name: Flow 2\n');
    await user.type(p.getByRole('textbox', { name: en['studio.props.header.description'] }), 'null');
    // A text the format would read as empty is quoted, at its canonical place.
    expect(current).toContain("name: Flow 2\ndescription: 'null'\nentity: account\n");
    await user.selectOptions(p.getByRole('combobox', { name: en['studio.props.header.scope'] }), en['studio.props.scope.organization']);
    expect(current).toContain('mode: realtime\nscope: organization\nrunAs: owner\n');
    await user.selectOptions(p.getByRole('combobox', { name: en['studio.props.header.runAs'] }), en['studio.props.runAs.caller']);
    expect(current).not.toContain('runAs');
    await user.click(p.getByRole('checkbox', { name: en['studio.props.header.logFailures'] }));
    await user.click(p.getByRole('checkbox', { name: en['studio.props.header.onDemand'] }));
    expect(current).toContain('scope: organization\nlogFailures: false\nonDemand: true\ntrigger:\n');
    await user.selectOptions(p.getByRole('combobox', { name: en['studio.props.trigger.updateStage'] }), en['studio.props.stage.post']);
    expect(current).toContain('  update: { columns: [name, revenue] }\n');
    expect(reread().header).toMatchObject({ name: 'Flow 2', description: 'null', scope: 'organization', logFailures: 'false', onDemand: 'true' });
  });

  it('edits the update trigger columns; the last one cannot be removed, the trigger can', async () => {
    const user = setup(REALTIME, { initial: 'trigger' });
    const p = panel();
    await user.click(p.getByRole('button', { name: 'Remove revenue' }));
    expect(current).toContain('  update: { columns: [name], stage: pre }\n');
    expect(p.getByRole('button', { name: 'Remove name' })).toBeDisabled();
    await user.type(p.getByRole('textbox', { name: en['studio.props.trigger.addColumn'] }), 'telephone1');
    await user.click(p.getByRole('button', { name: en['studio.props.trigger.addColumnButton'] }));
    expect(current).toContain('  update: { columns: [name, telephone1], stage: pre }\n');
    await user.click(p.getByRole('checkbox', { name: en['studio.canvas.trigger.updateAny'] }));
    await user.click(p.getByRole('checkbox', { name: en['studio.canvas.trigger.create'] }));
    // No event left: the trigger is removed (a workflow without one runs on demand or as a child).
    expect(current).not.toContain('trigger');
    await user.click(p.getByRole('checkbox', { name: en['studio.canvas.trigger.delete'] }));
    expect(current).toContain('runAs: owner\ntrigger:\n  delete: true\nsteps:\n');
    // An update trigger is written with its first column.
    await user.click(p.getByRole('checkbox', { name: en['studio.canvas.trigger.updateAny'] }));
    expect(current).not.toContain('  update: {');
    expect(p.getByText(en['studio.props.trigger.needsColumn'])).toBeInTheDocument();
    await user.type(p.getByRole('textbox', { name: en['studio.props.trigger.addColumn'] }), 'name');
    await user.click(p.getByRole('button', { name: en['studio.props.trigger.addColumnButton'] }));
    expect(current).toContain('trigger:\n  update: { columns: [name] }\n  delete: true\n');
    reread();
  });

  it('editing a step field changes the document at that path, keeping its comments', async () => {
    const user = setup(REALTIME);
    await user.click(card('Update record: primary'));
    const p = panel();
    expect(p.getByRole('heading', { name: en['studio.step.update'] })).toBeInTheDocument();
    const value = p.getByRole('textbox', { name: 'Value of name' });
    expect(value).toHaveValue('X');
    await user.type(value, 'Y');
    expect(current).toContain('      record: primary # the record\n      set:\n        name: XY\n');
    await user.type(p.getByRole('textbox', { name: en['studio.props.key.description'] }), 'Rename');
    // A new key goes to the writer's place: description first in a block step.
    expect(current).toContain('  - update:\n      description: Rename\n      record: primary # the record\n');
    await user.clear(p.getByRole('textbox', { name: en['studio.props.key.description'] }));
    expect(current).not.toContain('description');
    // A set row is added by name and removed with its button.
    await user.type(p.getByRole('textbox', { name: en['studio.props.set.add'] }), 'telephone1');
    await user.click(p.getByRole('button', { name: `Add to ${en['studio.props.key.set']}` }));
    expect(current).toContain('      set:\n        name: XY\n        telephone1: ~\n');
    await user.click(p.getByRole('button', { name: 'Remove name' }));
    expect(current).toContain('      set:\n        telephone1: ~\n');
    reread();
  });

  it('the condition of an if is edited with the condition builder and written as 2.21 writes it', async () => {
    const user = setup(REALTIME);
    await user.click(card('If: name null'));
    const p = panel();
    expect(p.getByRole('combobox', { name: en['studio.props.condition.op'] })).toHaveValue('null');
    expect(p.getByText(en['studio.props.branchesOnCanvas'])).toBeInTheDocument();
    await user.selectOptions(p.getByRole('combobox', { name: /^Condition/ }), en['studio.props.condition.all']);
    expect(current).toContain('      when: { all: [{ field: name, op: null }] }\n');
    await user.click(p.getByRole('button', { name: en['studio.props.condition.addRow'] }));
    expect(current).toContain("      when: { all: [{ field: name, op: null }, { field: '', op: eq, value: '' }] }\n");
    reread();
  });

  it('a stop step offers its status as a list, and a missing selection asks for a card', async () => {
    const user = setup(REALTIME, { initial: ['steps', 1, 'if', 'then', 0] });
    await user.selectOptions(panel().getByRole('combobox', { name: /^Status/ }), en['studio.props.enum.succeeded']);
    expect(current).toContain('        - stop: { status: succeeded }\n');
    await user.click(panel().getByRole('button', { name: `Add ${en['studio.props.key.message']}` }));
    expect(current).toContain("        - stop: { status: succeeded, message: '' }\n");
    // Deleted from the canvas: the selection goes to the trigger card.
    await user.click(within(card('Stop: succeeded')).getByRole('button', { name: en['studio.canvas.cardMenu'] }));
    await user.click(await screen.findByRole('menuitem', { name: en['studio.canvas.delete'] }));
    await waitFor(() => expect(current).not.toContain('stop'));
    expect(panel().queryByRole('combobox', { name: /^Status/ })).toBeNull();
  });

  it('a path that names no step any more asks for a card', () => {
    setup(REALTIME, { initial: ['steps', 7] });
    expect(panel().getByText(en['studio.props.none'])).toBeInTheDocument();
  });

  it('reads the tables through EntityDefinitions and offers their columns', async () => {
    const asked: string[] = [];
    server.use(
      http.get('*/api/data/v9.2/EntityDefinitions*', ({ request }) => {
        asked.push(decodeURIComponent(new URL(request.url).pathname));
        return HttpResponse.json({
          LogicalName: 'account',
          EntitySetName: 'accounts',
          PrimaryIdAttribute: 'accountid',
          PrimaryNameAttribute: 'name',
          DisplayName: { UserLocalizedLabel: { Label: 'Account' } },
          Attributes: [
            { '@odata.type': '#Microsoft.Dynamics.CRM.StringAttributeMetadata', LogicalName: 'name', DisplayName: { UserLocalizedLabel: { Label: 'Account Name' } } },
            { '@odata.type': '#Microsoft.Dynamics.CRM.StringAttributeMetadata', LogicalName: 'telephone1', DisplayName: { UserLocalizedLabel: { Label: 'Main Phone' } } },
          ],
        });
      }),
    );
    const api = new XrmApi({ getToken: async () => 'tok', onUnauthorized: () => {} });
    const user = setup(REALTIME, { api, initial: ['steps', 0] });
    const add = await panel().findByRole('combobox', { name: en['studio.props.set.add'] });
    // The columns already set are not offered again.
    expect(within(add).getAllByRole('option').map((o) => o.textContent)).toEqual([en['studio.props.chooseColumn'], 'Main Phone (telephone1)']);
    await user.selectOptions(add, 'Main Phone (telephone1)');
    expect(current).toContain('        name: X\n        telephone1: ~\n');
    expect(panel().getByRole('group', { name: 'Account Name (name)' })).toBeInTheDocument();
    expect(asked).toEqual(["/api/data/v9.2/EntityDefinitions(LogicalName='account')"]);
  });
});
