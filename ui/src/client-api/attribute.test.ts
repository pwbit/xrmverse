import { act, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { HttpResponse } from 'msw';
import { onPath } from '../admin/security/testSupport';
import { describe, expect, it } from 'vitest';
import { attr } from '../admin/tables/testAttrs';
import { contextOf, field, RECORD_ID, simpleForm } from '../renderers/classic/form/formTestSupport';
import { server } from '../test/server';
import type { Attribute } from './attribute';
import type { FormContext } from './formContext';
import { mountForm } from './testForm';

const name = attr('xv_name', { isPrimaryName: true, displayName: 'Name', maxLength: 100, format: 'Text' });
const budget = attr('xv_budget', { kind: 'money', displayName: 'Budget', minValue: 0, maxValue: 1000, precision: 2 });
const stage = attr('xv_stage', {
  kind: 'choice',
  displayName: 'Stage',
  options: [
    { value: 1, label: 'Plan' },
    { value: 2, label: 'Build' },
  ],
});
const tags = attr('xv_tags', {
  kind: 'choices',
  displayName: 'Tags',
  options: [
    { value: 10, label: 'Red' },
    { value: 20, label: 'Blue' },
  ],
});
const done = attr('xv_done', { kind: 'bool', displayName: 'Done', options: [{ value: 0, label: 'No' }, { value: 1, label: 'Yes' }] });
const start = attr('xv_start', { kind: 'datetime', displayName: 'Start', dateOnly: true });
const project = attr('xv_parentid', { kind: 'lookup', targets: ['xv_project'], displayName: 'Parent' });
const LOOKUPS = [{ schemaName: 'p', referencingEntity: 'xv_project', referencingAttribute: 'xv_parentid', referencedEntity: 'xv_project', nav: 'xv_parentid' }];
const PARENT = 'dddddddd-0000-0000-0000-00000000000a';
const RECORD = {
  xv_projectid: RECORD_ID,
  xv_name: 'Alpha',
  xv_budget: 12.5,
  xv_stage: 2,
  xv_tags: '10,20',
  xv_done: true,
  xv_start: '2026-03-04',
  _xv_parentid_value: PARENT,
  '_xv_parentid_value@Microsoft.Dynamics.CRM.lookuplogicalname': 'xv_project',
  '_xv_parentid_value@OData.Community.Display.V1.FormattedValue': 'Parent project',
};
const ALL = [name, budget, stage, tags, done, start, project];
const form = () => simpleForm(ALL.map((a) => field(a.logicalName)));

type Attr = Attribute & Record<string, (...a: unknown[]) => unknown>;
const get = (fc: FormContext, n: string) => fc.getAttribute(n) as Attr;

async function mounted(over: Parameters<typeof mountForm>[2] = {}) {
  const m = mountForm(form(), contextOf(ALL, RECORD, { lookups: LOOKUPS }), over);
  await screen.findByLabelText('xv_name label');
  return m;
}

describe('attribute', () => {
  it('value_shapes_by_type', async () => {
    const m = await mounted();
    const fc = m.fc();
    expect(get(fc, 'xv_name').getValue()).toBe('Alpha');
    expect(get(fc, 'xv_budget').getValue()).toBe(12.5);
    expect(get(fc, 'xv_stage').getValue()).toBe(2);
    expect(get(fc, 'xv_tags').getValue()).toEqual([10, 20]);
    expect(get(fc, 'xv_done').getValue()).toBe(true);
    expect(get(fc, 'xv_start').getValue()).toEqual(new Date(Date.UTC(2026, 2, 4)));
    expect(['xv_name', 'xv_budget', 'xv_stage', 'xv_tags', 'xv_done', 'xv_start', 'xv_parentid'].map((n) => get(fc, n).getAttributeType())).toEqual([
      'string',
      'money',
      'optionset',
      'multiselectoptionset',
      'boolean',
      'datetime',
      'lookup',
    ]);
    expect(get(fc, 'xv_start').getFormat()).toBe('date');
    expect(get(fc, 'xv_name').getFormat()).toBe('text');
    expect(get(fc, 'xv_name').getMaxLength()).toBe(100);
    expect([get(fc, 'xv_budget').getMin(), get(fc, 'xv_budget').getMax(), get(fc, 'xv_budget').getPrecision()]).toEqual([0, 1000, 2]);
    expect(get(fc, 'xv_parentid').getIsPartyList()).toBe(false);
    // Members of other types are not there.
    expect(get(fc, 'xv_name').getOptions).toBeUndefined();
    expect(get(fc, 'xv_stage').getMaxLength).toBeUndefined();
    act(() => get(fc, 'xv_name').setValue(null));
    expect(get(fc, 'xv_name').getValue()).toBeNull();
    act(() => get(fc, 'xv_start').setValue(new Date(Date.UTC(2027, 0, 9))));
    expect(screen.getByLabelText('xv_start label')).toHaveValue('2027-01-09');
    expect(() => get(fc, 'xv_budget').setValue('12')).toThrow('setValue on xv_budget');
  });

  it('user_local_dates_follow_the_user_time_zone', async () => {
    // #131: the form shows the user's clock (Rome); getValue is the instant, setValue takes an instant.
    const due = attr('xv_due', { kind: 'datetime', displayName: 'Due' });
    const m = mountForm(simpleForm([field('xv_due')]), contextOf([due], { xv_projectid: RECORD_ID, xv_due: '2026-03-29T01:30:00Z' }), { timeZone: 'Europe/Rome' });
    const time = await screen.findByRole('combobox', { name: /time$/ });
    expect(time).toHaveValue('03:30');
    const a = get(m.fc(), 'xv_due');
    expect((a.getValue() as Date).toISOString()).toBe('2026-03-29T01:30:00.000Z');
    act(() => a.setValue(new Date('2026-07-01T10:00:00Z')));
    expect(screen.getByRole('combobox', { name: /time$/ })).toHaveValue('12:00');
    expect((a.getValue() as Date).toISOString()).toBe('2026-07-01T10:00:00.000Z');
  });

  it('lookup_value_shape_and_set_value_accepts_unbraced_id', async () => {
    const OTHER = 'EEEEEEEE-0000-0000-0000-00000000000B';
    const sets: string[] = [];
    const m = await mounted({
      setup: () =>
        server.use(
          onPath('get', "EntityDefinitions(LogicalName='xv_project')", ({ request }) => {
            sets.push(new URL(request.url).search);
            return HttpResponse.json({ EntitySetName: 'xv_projects' });
          }),
        ),
    });
    const fc = m.fc();
    expect(get(fc, 'xv_parentid').getValue()).toEqual([{ id: `{${PARENT.toUpperCase()}}`, name: 'Parent project', entityType: 'xv_project' }]);
    // No braces, upper case: read back braced and upper case.
    act(() => get(fc, 'xv_parentid').setValue([{ id: OTHER, name: 'Other', entityType: 'xv_project' }]));
    expect(get(fc, 'xv_parentid').getValue()).toEqual([{ id: `{${OTHER}}`, name: 'Other', entityType: 'xv_project' }]);
    act(() => get(fc, 'xv_parentid').setValue([{ id: `{${OTHER.toLowerCase()}}`, name: 'Other', entityType: 'xv_project' }]));
    expect(screen.getByDisplayValue('Other')).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Save now' }));
    // The bind uses the relationship's navigation property and the target's entity set, read once from its metadata.
    await waitFor(() => expect(m.sent).toHaveLength(1));
    expect(m.sent[0]!.body).toEqual({ 'xv_parentid@odata.bind': `/xv_projects(${OTHER.toLowerCase()})` });
    expect(sets).toEqual(['?$select=EntitySetName']);
    expect(() => get(fc, 'xv_parentid').setValue([{ id: 'nope', entityType: 'xv_project' }])).toThrow('setValue on xv_parentid');
  });

  it('choice_options_text_and_selected_option', async () => {
    const m = await mounted();
    const fc = m.fc();
    const s = get(fc, 'xv_stage');
    expect(s.getOptions()).toEqual([
      { value: 1, text: 'Plan' },
      { value: 2, text: 'Build' },
    ]);
    expect(s.getOption(1)).toEqual({ value: 1, text: 'Plan' });
    expect(s.getOption('Build')).toEqual({ value: 2, text: 'Build' });
    expect(s.getOption(9)).toBeNull();
    expect(s.getText()).toBe('Build');
    expect(s.getSelectedOption()).toEqual({ value: 2, text: 'Build' });
    act(() => s.setValue(1));
    expect(s.getText()).toBe('Plan');
    expect(s.getInitialValue()).toBe(2);
    const t = get(fc, 'xv_tags');
    expect(t.getText()).toEqual(['Red', 'Blue']);
    expect(t.getSelectedOption()).toEqual([
      { value: 10, text: 'Red' },
      { value: 20, text: 'Blue' },
    ]);
    act(() => t.setValue(null));
    expect(t.getValue()).toBeNull();
    expect(t.getText()).toBeNull();
    expect(get(fc, 'xv_done').getInitialValue()).toBe(true);
  });

  it('set_value_marks_dirty_without_onchange', async () => {
    const m = await mounted();
    const fc = m.fc();
    const ran: string[] = [];
    get(fc, 'xv_name').addOnChange(() => ran.push('changed'));
    expect(get(fc, 'xv_name').getIsDirty()).toBe(false);
    act(() => get(fc, 'xv_name').setValue('Beta'));
    expect(get(fc, 'xv_name').getValue()).toBe('Beta');
    expect(get(fc, 'xv_name').getIsDirty()).toBe(true);
    expect((fc.data.entity.getIsDirty as () => boolean)()).toBe(true);
    expect(screen.getByLabelText('xv_name label')).toHaveValue('Beta');
    expect(ran).toEqual([]);
    expect(m.formXml).toEqual([]);
  });

  it('fire_on_change_runs_handlers_once', async () => {
    const m = await mounted();
    const fc = m.fc();
    const ran: unknown[] = [];
    const handler = (ctx: { getEventSource(): unknown }) => ran.push(ctx.getEventSource());
    get(fc, 'xv_stage').addOnChange(handler);
    get(fc, 'xv_stage').addOnChange(handler);
    act(() => get(fc, 'xv_stage').fireOnChange());
    // FormXml handlers, then the added one, once; the event source is the attribute.
    expect(m.formXml).toEqual(['onchange:xv_stage']);
    expect(ran).toEqual([fc.getAttribute('xv_stage')]);
    get(fc, 'xv_stage').removeOnChange(handler);
    act(() => get(fc, 'xv_stage').fireOnChange());
    expect(ran).toHaveLength(1);
  });

  it('onchange_handler_setting_own_value_no_loop', async () => {
    const m = await mounted();
    const fc = m.fc();
    let runs = 0;
    get(fc, 'xv_name').addOnChange(() => {
      runs++;
      get(fc, 'xv_name').setValue(String(get(fc, 'xv_name').getValue()).toUpperCase());
    });
    const input = screen.getByLabelText('xv_name label');
    await userEvent.clear(input);
    await userEvent.type(input, 'beta');
    await userEvent.tab();
    expect(runs).toBe(1);
    expect(input).toHaveValue('BETA');
    expect(m.formXml).toEqual(['onchange:xv_name']);
    act(() => get(fc, 'xv_name').fireOnChange());
    expect(runs).toBe(2);
  });

  it('user_change_runs_formxml_onchange_handlers', async () => {
    const m = await mounted();
    const fc = m.fc();
    const seen: unknown[] = [];
    get(fc, 'xv_name').addOnChange(() => seen.push(get(fc, 'xv_name').getValue()));
    const input = screen.getByLabelText('xv_name label');
    await userEvent.type(input, 'X');
    // A typed column raises OnChange when the user leaves it, once.
    expect(m.formXml).toEqual([]);
    await userEvent.type(input, 'Y');
    await userEvent.tab();
    expect(m.formXml).toEqual(['onchange:xv_name']);
    expect(seen).toEqual(['AlphaXY']);
    // A choice raises it at once.
    await userEvent.selectOptions(screen.getByLabelText('xv_stage label'), '1');
    expect(m.formXml).toEqual(['onchange:xv_name', 'onchange:xv_stage']);
  });

  it('a_throwing_onchange_handler_notifies_and_the_next_runs', async () => {
    const m = await mounted();
    const fc = m.fc();
    const ran: string[] = [];
    get(fc, 'xv_stage').addOnChange(function failing() {
      throw new Error('boom');
    });
    get(fc, 'xv_stage').addOnChange(() => ran.push('next'));
    act(() => get(fc, 'xv_stage').fireOnChange());
    expect(m.notes.map((n) => n.text)).toEqual(['Script error in failing: boom']);
    expect(ran).toEqual(['next']);
  });

  it('set_required_level_blocks_save_and_marks_label', async () => {
    const m = await mounted();
    const fc = m.fc();
    const n = get(fc, 'xv_name');
    expect(n.getRequiredLevel()).toBe('none');
    act(() => n.setRequiredLevel('required'));
    expect(n.getRequiredLevel()).toBe('required');
    // Fluent's Field marks a required label with an asterisk.
    expect(screen.getByText('xv_name label').closest('label')!.textContent).toContain('*');
    act(() => n.setValue(null));
    await userEvent.click(screen.getByRole('button', { name: 'Save now' }));
    expect(await screen.findAllByText('xv_name label: a value is required.')).not.toHaveLength(0);
    expect(m.sent).toEqual([]);
    act(() => n.setRequiredLevel('none'));
    expect(screen.getByText('xv_name label').closest('label')!.textContent).not.toContain('*');
    await userEvent.click(screen.getByRole('button', { name: 'Save now' }));
    await waitFor(() => expect(m.sent).toHaveLength(1));
    expect(() => n.setRequiredLevel('mandatory')).toThrow('setRequiredLevel on xv_name');
  });

  it('submit_mode_never_excluded_always_included', async () => {
    const m = await mounted();
    const fc = m.fc();
    expect(get(fc, 'xv_name').getSubmitMode()).toBe('dirty');
    act(() => {
      get(fc, 'xv_name').setValue('Beta');
      get(fc, 'xv_name').setSubmitMode('never');
      get(fc, 'xv_stage').setSubmitMode('always');
      get(fc, 'xv_budget').setValue(20);
    });
    // Learn: a column never submitted cannot be edited on the form.
    expect(screen.getByLabelText('xv_name label')).toBeDisabled();
    await userEvent.click(screen.getByRole('button', { name: 'Save now' }));
    await waitFor(() => expect(m.sent).toHaveLength(1));
    expect(m.sent[0]!.body).toEqual({ xv_budget: 20, xv_stage: 2 });
    expect(() => get(fc, 'xv_name').setSubmitMode('sometimes')).toThrow('setSubmitMode on xv_name');
  });

  it('disabled_column_set_by_script_is_sent_and_always_sends_unchanged', async () => {
    const code = attr('xv_code', { displayName: 'Code' });
    const ref = attr('xv_ref', { displayName: 'Ref', validForUpdate: false });
    const m = mountForm(simpleForm([field('xv_name'), field('xv_code', {}, { disabled: true }), field('xv_budget', {}, { disabled: true }), field('xv_ref')]), contextOf([name, code, budget, ref], { ...RECORD, xv_code: 'A-1', xv_ref: 'R-1' }));
    await screen.findByLabelText('xv_name label');
    const fc = m.fc();
    expect(screen.getByLabelText('xv_code label')).toBeDisabled();
    act(() => {
      // A script fills a disabled column (dirty mode, the default) and forces another, unchanged one.
      get(fc, 'xv_code').setValue('B-2');
      get(fc, 'xv_budget').setSubmitMode('always');
      // The server refuses this column on update: it is never sent, whatever the script does.
      get(fc, 'xv_ref').setValue('R-2');
      get(fc, 'xv_ref').setSubmitMode('always');
    });
    expect((fc.data.entity.getIsDirty as () => boolean)()).toBe(true);
    await userEvent.click(screen.getByRole('button', { name: 'Save now' }));
    await waitFor(() => expect(m.sent).toHaveLength(1));
    expect(m.sent[0]!.body).toEqual({ xv_code: 'B-2', xv_budget: 12.5 });
  });

  it('read_only_record_never_sends_script_values', async () => {
    const m = mountForm(simpleForm([field('xv_name')]), contextOf([name], RECORD, { rights: ['Read'] }));
    await screen.findByLabelText('xv_name label');
    act(() => {
      get(m.fc(), 'xv_name').setValue('Beta');
      get(m.fc(), 'xv_name').setSubmitMode('always');
    });
    await userEvent.click(screen.getByRole('button', { name: 'Save now' }));
    await new Promise((r) => setTimeout(r, 20));
    expect(m.sent).toEqual([]);
  });

  it('attribute_type_of_an_unlisted_column_fails_explicitly', async () => {
    const id = attr('xv_key', { kind: 'unsupported', typeName: 'Uniqueidentifier', displayName: 'Key' });
    const m = mountForm(simpleForm([field('xv_name'), field('xv_key')]), contextOf([name, id], RECORD));
    await screen.findByLabelText('xv_name label');
    expect(() => get(m.fc(), 'xv_key').getAttributeType()).toThrow(
      'formContext.getAttribute("xv_key").getAttributeType is not available in this version for a Uniqueidentifier column',
    );
  });

  it('typed_onchange_only_when_value_differs_on_leaving', async () => {
    const m = await mounted();
    const input = screen.getByLabelText('xv_name label');
    // Typed, then restored: no change when the field is left.
    await userEvent.type(input, 'X');
    await userEvent.type(input, '{Backspace}');
    await userEvent.tab();
    expect(m.formXml).toEqual([]);
    await userEvent.click(input);
    await userEvent.type(input, 'Z');
    await userEvent.tab();
    expect(m.formXml).toEqual(['onchange:xv_name']);
  });

  it('attribute_controls_and_parent', async () => {
    const m = await mounted();
    const fc = m.fc();
    expect(get(fc, 'xv_name').controls.get().map((c) => c.getName())).toEqual(['xv_name']);
    expect(get(fc, 'xv_name').getParent()).toBe(fc.data.entity);
    expect(() => get(fc, 'xv_name').setIsValid(false)).toThrow('formContext.getAttribute("xv_name").setIsValid is not available in this version');
  });
});
