import { act, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { afterEach, describe, expect, it } from 'vitest';
import { attr } from '../../../admin/tables/testAttrs';
import { createUiOverlay } from '../../../client-api/uiOverlay';
import { setUiLanguage } from '../../../shell/i18n/t';
import type { Cell } from '../../../model/form';
import { server } from '../../../test/server';
import { contextOf, field, formOf, labelsOf, renderForm, section, simpleForm, tab } from './formTestSupport';

afterEach(() => setUiLanguage(undefined));

const name = attr('xv_name', { isPrimaryName: true, displayName: 'Name' });
const code = attr('xv_code', { displayName: 'Code' });
const note = attr('xv_note', { displayName: 'Note' });
const RECORD = { xv_projectid: 'cccccccc-0000-0000-0000-000000000003', xv_name: 'Alpha', xv_code: 'A-1', xv_note: 'n1' };

describe('FormView layout', () => {
  it('project_form_renders_tabs_in_order_first_open', async () => {
    const form = formOf([
      tab('summary', [{ width: '100%', sections: [section([[field('xv_name')]])] }], { labels: labelsOf('Summary') }),
      tab('details', [{ width: '100%', sections: [section([[field('xv_note')]])] }], { labels: labelsOf('Details') }),
    ]);
    renderForm(form, contextOf([name, note], RECORD));
    const tabs = await screen.findAllByRole('tab');
    expect(tabs.map((x) => x.getAttribute('value'))).toEqual(['0', '1']);
    expect(tabs[0]).toHaveAccessibleName('Summary');
    expect(tabs[1]).toHaveAccessibleName('Details');
    expect(tabs[0]).toHaveAttribute('aria-selected', 'true');
    expect(screen.getByLabelText('xv_name label')).toHaveValue('Alpha');
    expect(screen.queryByLabelText('xv_note label')).toBeNull();
    await userEvent.click(tabs[1]!);
    expect(await screen.findByLabelText('xv_note label')).toHaveValue('n1');
  });

  it('summary_tab_columns_use_percent_widths', async () => {
    const form = formOf([
      tab('summary', [
        { width: '60%', sections: [section([[field('xv_name')]], { name: 'left' })] },
        { width: '40%', sections: [section([[field('xv_code')]], { name: 'right' })] },
      ]),
    ]);
    renderForm(form, contextOf([name, code], RECORD));
    await screen.findByLabelText('xv_name label');
    const columns = screen.getAllByTestId('tab-column');
    expect(columns.map((c) => c.getAttribute('data-width'))).toEqual(['60%', '40%']);
    expect(columns[0]!.parentElement!.style.getPropertyValue('--xrmverse-tab-columns')).toBe('60fr 40fr');
    expect(within(columns[0]!).getByLabelText('xv_name label')).toBeInTheDocument();
    expect(within(columns[1]!).getByLabelText('xv_code label')).toBeInTheDocument();
  });

  it('section_columns_and_colspan_layout', async () => {
    const wide = field('xv_note', { colSpan: 2 });
    const form = simpleForm([]);
    form.tabs[0]!.columns[0]!.sections = [section([[field('xv_name'), field('xv_code')], [wide]], { columns: 2, labels: labelsOf('Project') })];
    renderForm(form, contextOf([name, code, note], RECORD));
    await screen.findByLabelText('xv_name label');
    const rows = screen.getAllByTestId('section-row');
    expect(rows).toHaveLength(2);
    expect(rows[0]!.style.getPropertyValue('--xrmverse-section-tracks')).toBe('repeat(2, minmax(0, 1fr))');
    const cells = screen.getAllByTestId('form-cell');
    expect(cells[0]!.style.gridColumn).toBe('span 1');
    expect(cells[2]!.style.gridColumn).toBe('span 2');
    expect(screen.getByRole('group', { name: 'Project' })).toBeInTheDocument();
  });

  it('section_columns_written_as_digits_keep_their_widths', async () => {
    const form = simpleForm([]);
    form.tabs[0]!.columns[0]!.sections = [section([[field('xv_name'), field('xv_code')]], { columns: 2, widths: [1, 2] })];
    renderForm(form, contextOf([name, code], RECORD));
    await screen.findByLabelText('xv_name label');
    expect(screen.getByTestId('section-row').style.getPropertyValue('--xrmverse-section-tracks')).toBe('minmax(0, 1fr) minmax(0, 2fr)');
  });

  it('label_left_by_default_top_when_set', async () => {
    const form = formOf([
      tab('main', [
        {
          width: '100%',
          sections: [section([[field('xv_name')]], { name: 'a' }), section([[field('xv_code')]], { name: 'b', labelPosition: 'top' })],
        },
      ]),
    ]);
    renderForm(form, contextOf([name, code], RECORD));
    const left = (await screen.findByLabelText('xv_name label')).closest('[data-testid="form-cell"]')!;
    const top = screen.getByLabelText('xv_code label').closest('[data-testid="form-cell"]')!;
    expect(left).toHaveAttribute('data-label-position', 'left');
    expect(top).toHaveAttribute('data-label-position', 'top');
  });

  it('a cell without its label takes the full width in a left-label section (no label track)', async () => {
    const form = formOf([
      tab('main', [
        {
          width: '100%',
          sections: [
            section([[field('xv_name')], [field('xv_note', { showLabel: false })]], { name: 'a' }),
            section([[field('xv_code')]], { name: 'b', labelPosition: 'top' }),
          ],
        },
      ]),
    ]);
    renderForm(form, contextOf([name, code, attr('xv_note', { displayName: 'Note' })], RECORD));
    const cellOf = (label: string) => screen.getByLabelText(label).closest('[data-testid="form-cell"]')!;
    await screen.findByLabelText('xv_name label');
    const classes = (el: Element) => new Set(el.className.split(/\s+/).filter(Boolean));
    const left = classes(cellOf('xv_name label'));
    const top = classes(cellOf('xv_code label'));
    const leftOnly = [...left].filter((c) => !top.has(c));
    expect(leftOnly.length).toBeGreaterThan(0);
    const hidden = classes(cellOf('xv_note label'));
    expect(leftOnly.filter((c) => hidden.has(c))).toEqual([]);
  });

  it('hidden_tab_section_cell_not_rendered', async () => {
    const hiddenSection = section([[field('xv_note')]], { name: 'h', visible: false });
    const form = formOf([
      tab('main', [{ width: '100%', sections: [section([[field('xv_name'), field('xv_code', {}, {}), field('xv_note', { visible: false })]], { columns: 3 }), hiddenSection] }]),
      tab('secret', [{ width: '100%', sections: [section([[field('xv_note')]])] }], { visible: false, labels: labelsOf('Secret') }),
    ]);
    form.tabs[0]!.columns[0]!.sections[0]!.rows[0]![2]!.labels = labelsOf('Hidden cell');
    renderForm(form, contextOf([name, code, note], RECORD));
    await screen.findByLabelText('xv_name label');
    expect(screen.getAllByRole('tab')).toHaveLength(1);
    expect(screen.queryByText('Secret')).toBeNull();
    expect(screen.queryByLabelText('Hidden cell')).toBeNull();
    expect(screen.queryByLabelText('xv_note label')).toBeNull();
    expect(screen.getAllByTestId('form-cell')).toHaveLength(2);
  });

  it('header_cells_compact_and_editable_in_their_panel_footer_read_only', async () => {
    const form = simpleForm([field('xv_name')], { header: [field('xv_code', { id: 'h1' })], footer: [field('xv_note', { id: 'f1' })] });
    renderForm(form, contextOf([name, code, note], RECORD));
    // The header card shows the header fields read-only; its chevron opens them to edit.
    const compact = await screen.findByLabelText('xv_code label');
    expect(compact).toBeDisabled();
    expect(screen.getByRole('group', { name: 'Header' })).toContainElement(compact);
    await userEvent.click(screen.getByRole('button', { name: 'Edit the header fields' }));
    const panel = await screen.findByRole('group', { name: 'Edit the header fields' });
    const headerInput = within(panel).getByLabelText('xv_code label');
    expect(headerInput).toBeEnabled();
    const footerInput = screen.getByLabelText('xv_note label');
    expect(footerInput).toBeDisabled();
    expect(screen.getByRole('group', { name: 'Footer' })).toContainElement(footerInput);
    // The same column in the body stays editable while its footer copy is read-only.
    expect(screen.getByLabelText('xv_name label')).toBeEnabled();
  });

  it('enter_activates_focused_tab', async () => {
    const form = formOf([
      tab('a', [{ width: '100%', sections: [section([[field('xv_name')]])] }], { labels: labelsOf('First') }),
      tab('b', [{ width: '100%', sections: [section([[field('xv_note')]])] }], { labels: labelsOf('Second') }),
    ]);
    renderForm(form, contextOf([name, note], RECORD));
    // The arrow keys move focus inside the tab list through Fluent's focus manager, which jsdom does not drive: the keys that
    // jsdom does deliver (Enter and Space on a focused tab) activate it.
    const second = await screen.findByRole('tab', { name: 'Second' });
    expect(second.tagName).toBe('BUTTON');
    second.focus();
    await userEvent.keyboard('{Enter}');
    expect(await screen.findByLabelText('xv_note label')).toBeInTheDocument();
    expect(screen.getByRole('tab', { name: 'Second' })).toHaveAttribute('aria-selected', 'true');
  });

  it('tab_list_is_named_and_tabs_and_panel_are_linked', async () => {
    const form = formOf([
      tab('a', [{ width: '100%', sections: [section([[field('xv_name')]])] }], { labels: labelsOf('First') }),
      tab('b', [{ width: '100%', sections: [section([[field('xv_note')]])] }], { labels: labelsOf('Second') }),
    ]);
    renderForm(form, contextOf([name, note], RECORD));
    expect(await screen.findByRole('tablist', { name: 'Main' })).toBeInTheDocument();
    const first = screen.getByRole('tab', { name: 'First' });
    const panel = screen.getByRole('tabpanel');
    expect(first.getAttribute('aria-controls')).toBe(panel.id);
    expect(panel.getAttribute('aria-labelledby')).toBe(first.id);
    expect(panel).toHaveAccessibleName('First');
  });

  it('labels_follow_user_language', async () => {
    const form = formOf(
      [
        tab('s', [{ width: '100%', sections: [section([[field('xv_name', { labels: labelsOf('Name', 'Nome') }), field('xv_code', { labels: { 1033: 'Code' } })]], { labels: labelsOf('Project', 'Commessa'), columns: 2 })] }], {
          labels: labelsOf('Summary', 'Riepilogo'),
        }),
      ],
    );
    renderForm(form, contextOf([name, code], RECORD), { lcid: 1040 });
    expect(await screen.findByRole('tab', { name: 'Riepilogo' })).toBeInTheDocument();
    expect(screen.getByRole('group', { name: 'Commessa' })).toBeInTheDocument();
    expect(screen.getByLabelText('Nome')).toBeInTheDocument();
    // No label in 1040: the 1033 label.
    expect(screen.getByLabelText('Code')).toBeInTheDocument();
  });

  it('create_mode_title_new_entity', async () => {
    renderForm(simpleForm([field('xv_name')]), contextOf([name], undefined, { displayName: 'Project' }));
    expect(await screen.findByRole('heading', { level: 1, name: 'New Project' })).toBeInTheDocument();
  });

  it('update_mode_title_primary_name', async () => {
    renderForm(simpleForm([field('xv_code')]), contextOf([name, code], RECORD));
    expect(await screen.findByRole('heading', { level: 1, name: 'Alpha' })).toBeInTheDocument();
  });
});

describe('FormView fields', () => {
  it('required_marker_and_message', async () => {
    const required = attr('xv_name', { requiredLevel: 'SystemRequired', displayName: 'Name' });
    const recommended = attr('xv_note', { requiredLevel: 'Recommended', displayName: 'Note' });
    const form = simpleForm([field('xv_name'), field('xv_note')]);
    renderForm(form, contextOf([required, recommended], { ...RECORD, xv_name: '' }));
    const input = await screen.findByLabelText(/xv_name label/);
    expect(input).toBeRequired();
    expect(screen.getByLabelText('xv_note label')).not.toBeRequired();
    await userEvent.type(screen.getByLabelText('xv_note label'), '!');
    await userEvent.click(screen.getByRole('button', { name: 'Save now' }));
    // Next to the field and in the form notification.
    expect(await screen.findAllByText('xv_name label: a value is required.')).toHaveLength(2);
  });

  it('missing_column_shows_not_available', async () => {
    const form = simpleForm([field('xv_name'), field('xv_ghost', { labels: labelsOf('Invented') }), field('xv_code')]);
    renderForm(form, contextOf([name, code], RECORD));
    expect(await screen.findByText('This field is not available.')).toBeInTheDocument();
    expect(screen.getByText('Invented')).toBeInTheDocument();
    // The rest of the form still works.
    expect(screen.getByLabelText('xv_name label')).toHaveValue('Alpha');
    expect(screen.getByLabelText('xv_code label')).toBeEnabled();
  });

  it('secured_column_uses_secured_field', async () => {
    const secret = attr('xv_secret', { secured: true, displayName: 'Secret' });
    const hidden = attr('xv_hidden', { secured: true, displayName: 'Hidden' });
    const form = simpleForm([field('xv_secret'), field('xv_hidden')]);
    // The server left out xv_hidden: the caller may not read it.
    renderForm(form, contextOf([name, secret, hidden], { ...RECORD, xv_secret: 's' }));
    const input = await screen.findByLabelText('xv_secret label');
    expect(input).toHaveValue('s');
    expect(screen.getAllByRole('img', { name: 'Secured column' }).length).toBeGreaterThan(0);
    expect(screen.getByText('No access')).toBeInTheDocument();
    expect(screen.queryByLabelText('xv_hidden label')).toBeNull();
  });

  it('timeline_cell_renders_the_timeline', async () => {
    const timeline: Cell = {
      ...field('x'),
      id: 't',
      control: { kind: 'timeline', id: 't', classId: '{06375649-C143-495E-A496-C962E5B4488E}', parameterNames: [] },
      labels: labelsOf('Timeline'),
    };
    const custom = field('xv_code', {}, { customControl: { name: 'xv_XvTest.Code', parameters: { value: { value: 'xv_code', static: false } } } });
    const ctx = contextOf([name, code], RECORD);
    ctx.entity.hasNotes = true;
    renderForm(simpleForm([timeline, custom]), ctx, {
      setup: () =>
        server.use(
          http.get('*/api/data/v9.2/customcontrols', () => HttpResponse.json({ value: [] })),
          http.get(/RetrieveUserPrivileges/, () => HttpResponse.json({ RolePrivileges: [{ PrivilegeName: 'prvReadNote' }] })),
          http.get('*/api/data/v9.2/annotations', ({ request }) =>
            HttpResponse.json(
              new URL(request.url).searchParams.has('$count')
                ? { '@odata.count': 1, value: [] }
                : { value: [{ annotationid: 'n1', subject: 'Kickoff minutes', modifiedon: '2026-11-10T08:00:00Z' }] },
            ),
          ),
        ),
    });
    const region = await screen.findByRole('region', { name: 'Timeline' });
    expect(await within(region).findByText('Kickoff minutes')).toBeInTheDocument();
    // The label is the timeline's header, shown once.
    expect(screen.getAllByText('Timeline')).toHaveLength(1);
    // The custom-control cell falls back to the standard control and the form says why (ruling R7).
    expect(await screen.findByText("Custom control 'xv_XvTest.Code' could not be loaded: no custom control with this name is installed")).toBeInTheDocument();
    expect(await screen.findByLabelText('xv_code label')).toHaveValue('A-1');
  });

  it('read_only_notification_without_write_access', async () => {
    renderForm(simpleForm([field('xv_name')]), contextOf([name], RECORD, { rights: ['Read'] }));
    expect(await screen.findByText('You do not have permission to edit this record.')).toBeInTheDocument();
    expect(screen.getByLabelText('xv_name label')).toBeDisabled();
  });

  it('customer_lookup_shows_target_name_and_clears', async () => {
    const customer = attr('xv_customerid', { kind: 'lookup', targets: ['account', 'contact'], displayName: 'Customer' });
    const lookups = [
      { schemaName: 'a', referencingEntity: 'xv_project', referencingAttribute: 'xv_customerid', referencedEntity: 'account', nav: 'xv_customerid_account' },
      { schemaName: 'c', referencingEntity: 'xv_project', referencingAttribute: 'xv_customerid', referencedEntity: 'contact', nav: 'xv_customerid_contact' },
    ];
    const data = {
      ...RECORD,
      _xv_customerid_value: 'dddddddd-0000-0000-0000-000000000001',
      '_xv_customerid_value@Microsoft.Dynamics.CRM.lookuplogicalname': 'contact',
      '_xv_customerid_value@OData.Community.Display.V1.FormattedValue': 'Ada Lovelace',
    };
    const { sent } = renderForm(simpleForm([field('xv_customerid')]), contextOf([name, customer], data, { lookups }));
    expect(await screen.findByDisplayValue('Ada Lovelace')).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Clear xv_customerid label' }));
    expect(screen.queryByDisplayValue('Ada Lovelace')).toBeNull();
    await userEvent.click(screen.getByRole('button', { name: 'Save now' }));
    // Cleared through the navigation property of the relationship to the table the value pointed at.
    await waitFor(() => expect(sent).toHaveLength(1));
    expect(sent[0]!.body).toEqual({ 'xv_customerid_contact@odata.bind': null });
  });

  it('status_reason_lists_current_state_reasons_only', async () => {
    const statuscode = attr('statuscode', {
      kind: 'choice',
      displayName: 'Status Reason',
      options: [
        { value: 1, label: 'Active', state: 0 },
        { value: 2, label: 'On hold', state: 0 },
        { value: 3, label: 'Closed', state: 1 },
      ],
    });
    const statecode = attr('statecode', {
      kind: 'choice',
      displayName: 'Status',
      options: [{ value: 0, label: 'Active' }, { value: 1, label: 'Inactive' }],
    });
    const form = simpleForm([field('statuscode'), field('statecode')]);
    renderForm(form, contextOf([name, statuscode, statecode], { ...RECORD, statecode: 0, statuscode: 1 }));
    const reason = await screen.findByLabelText('statuscode label');
    expect(within(reason).getAllByRole('option').map((o) => o.textContent)).toEqual(['(empty)', 'Active', 'On hold']);
    // The state itself is read-only.
    expect(screen.getByLabelText('statecode label')).toBeDisabled();
  });

  it('money_shows_currency_symbol', async () => {
    const budget = attr('xv_budget', { kind: 'money', displayName: 'Budget' });
    renderForm(simpleForm([field('xv_budget')]), contextOf([name, budget], { ...RECORD, xv_budget: 10 }), { formView: { currencySymbol: '€' } });
    expect(await screen.findByLabelText('xv_budget label (€)')).toHaveValue('10.00');
  });

  it('show_label_false_keeps_an_accessible_name', async () => {
    renderForm(simpleForm([field('xv_name', { showLabel: false })]), contextOf([name], RECORD));
    const input = await screen.findByLabelText('xv_name label');
    expect(input).toBeInTheDocument();
    expect(input.closest('[data-testid="form-cell"]')).toHaveAttribute('data-label-hidden', 'true');
  });
});

describe('FormView overlay', () => {
  const twoTabs = () =>
    formOf([
      tab('summary', [{ width: '100%', sections: [section([[field('xv_name')], [field('xv_code')]], { name: 'general', labels: labelsOf('General') })] }], { labels: labelsOf('Summary') }),
      tab('details', [{ width: '100%', sections: [section([[field('xv_note')]], { name: 'more', labels: labelsOf('More') })] }], { labels: labelsOf('Details') }),
    ]);

  it('overlay_visibility_and_labels_override_the_form_xml', async () => {
    const form = twoTabs();
    const overlay = createUiOverlay();
    renderForm(form, contextOf([name, code, note], RECORD), { formView: { overlay } });
    await screen.findByLabelText('xv_name label');
    const [summary, details] = form.tabs;
    const general = summary!.columns[0]!.sections[0]!;
    const codeCell = general.rows[1]![0]!;
    act(() => {
      overlay.setElement(codeCell, { visible: false });
      overlay.setElement(form.tabs[0]!.columns[0]!.sections[0]!.rows[0]![0]!, { label: 'Project name' });
      overlay.setElement(general, { label: 'Basics' });
      overlay.setElement(details!, { label: 'Everything else' });
    });
    expect(screen.queryByLabelText('xv_code label')).toBeNull();
    expect(screen.getByLabelText('Project name')).toHaveValue('Alpha');
    expect(screen.getByRole('group', { name: 'Basics' })).toBeInTheDocument();
    expect(screen.getByRole('tab', { name: 'Everything else' })).toBeInTheDocument();
    // A section shown hides with its cells; a hidden FormXml cell is shown when a script makes it visible.
    act(() => overlay.setElement(general, { visible: false }));
    expect(screen.queryByLabelText('Project name')).toBeNull();
    act(() => overlay.setElement(details!, { visible: false }));
    expect(screen.queryByRole('tab', { name: 'Everything else' })).toBeNull();
  });

  it('overlay_selected_tab_and_hidden_selected_tab_falls_back', async () => {
    const form = twoTabs();
    const overlay = createUiOverlay();
    renderForm(form, contextOf([name, code, note], RECORD), { formView: { overlay } });
    await screen.findByLabelText('xv_name label');
    act(() => overlay.selectTab(form.tabs[1]!));
    expect(screen.getByRole('tab', { name: 'Details' })).toHaveAttribute('aria-selected', 'true');
    expect(await screen.findByLabelText('xv_note label')).toHaveValue('n1');
    act(() => overlay.setElement(form.tabs[1]!, { visible: false }));
    expect(screen.getByRole('tab', { name: 'Summary' })).toHaveAttribute('aria-selected', 'true');
    // The user's choice goes through the overlay too.
    act(() => overlay.setElement(form.tabs[1]!, { visible: true }));
    await userEvent.click(screen.getByRole('tab', { name: 'Summary' }));
    expect(overlay.selectedTab()).toBe(form.tabs[0]);
  });

  it('overlay_notifications_and_disabled', async () => {
    const form = simpleForm([field('xv_name'), field('xv_code')]);
    const overlay = createUiOverlay();
    renderForm(form, contextOf([name, code], RECORD), { formView: { overlay } });
    await screen.findByLabelText('xv_name label');
    const [nameCell, codeCell] = [form.tabs[0]!.columns[0]!.sections[0]!.rows[0]![0]!, form.tabs[0]!.columns[0]!.sections[0]!.rows[1]![0]!];
    act(() => {
      overlay.setFormNotification({ uniqueId: 'w', message: 'Budget is low', level: 'WARNING' });
      overlay.addControlNotification(nameCell, { uniqueId: 'e', message: 'Name is taken', level: 'ERROR' });
      overlay.setElement(codeCell, { disabled: true });
    });
    expect(screen.getByText('Budget is low').closest('.fui-MessageBar')).toHaveAttribute('data-intent', 'warning');
    expect(screen.getByText('Name is taken')).toBeInTheDocument();
    expect(screen.getByLabelText('xv_code label')).toBeDisabled();
  });
});

describe('FormView renderTabBody (UI-2c)', () => {
  const twoTabs = (over: Parameters<typeof formOf>[1] = {}) =>
    formOf(
      [
        tab('summary', [{ width: '100%', sections: [section([[field('xv_name')]])] }], { labels: labelsOf('Summary') }),
        tab('hidden', [{ width: '100%', sections: [section([[field('xv_code')]])] }], { labels: labelsOf('Hidden'), visible: false }),
        tab('details', [{ width: '100%', sections: [section([[field('xv_note')]])] }], { labels: labelsOf('Details') }),
      ],
      over,
    );

  it('returning undefined keeps the classic tab list', async () => {
    renderForm(twoTabs(), contextOf([name, code, note], RECORD), { formView: { renderTabBody: () => undefined } });
    expect(await screen.findAllByRole('tab')).toHaveLength(2);
    expect(screen.getByLabelText('xv_name label')).toHaveValue('Alpha');
  });

  it('a node replaces the tab list and panel; it gets the shown tabs, the current one and selectTab', async () => {
    const seen: { names: string[]; current?: string; index?: number }[] = [];
    renderForm(twoTabs(), contextOf([name, code, note], RECORD), {
      formView: {
        renderTabBody: ({ tabs, current, env, selectTab }) => {
          seen.push({ names: tabs.map((x) => x.tab.name), current: current?.tab.name, index: current?.index });
          return (
            <div data-testid="custom-body">
              <span>{`on ${current?.tab.name}`}</span>
              <span>{env.lcid}</span>
              <button onClick={() => selectTab(tabs[1]!.tab)}>go details</button>
            </div>
          );
        },
      },
    });
    expect(await screen.findByTestId('custom-body')).toHaveTextContent('on summary');
    expect(screen.queryByRole('tablist')).toBeNull();
    expect(screen.queryByRole('tabpanel')).toBeNull();
    expect(seen.at(-1)).toEqual({ names: ['summary', 'details'], current: 'summary', index: 0 });
    await userEvent.click(screen.getByRole('button', { name: 'go details' }));
    expect(screen.getByTestId('custom-body')).toHaveTextContent('on details');
    expect(seen.at(-1)).toMatchObject({ current: 'details', index: 2 });
    // No Related link without navigation.
    expect(screen.queryByRole('button', { name: 'Related' })).toBeNull();
  });

  it('tabList(renderTab) draws the classic tab list with the panel drawn by renderTab', async () => {
    renderForm(twoTabs(), contextOf([name, code, note], RECORD), {
      formView: { renderTabBody: ({ tabList }) => tabList((t) => <p data-testid="own-tab">{`own ${t.name}`}</p>) },
    });
    const tabs = await screen.findAllByRole('tab');
    expect(tabs).toHaveLength(2);
    expect(within(screen.getByRole('tabpanel')).getByTestId('own-tab')).toHaveTextContent('own summary');
    expect(screen.queryByLabelText('xv_name label')).toBeNull();
    await userEvent.click(tabs[1]!);
    expect(screen.getByTestId('own-tab')).toHaveTextContent('own details');
  });

  it('with a custom body the Related tab is a link after it, and the form comes back with a link', async () => {
    const navigation = [{ kind: 'unsupported' as const, id: 'navLink', labels: { 1033: 'Help page' } }];
    renderForm(twoTabs({ navigation }), contextOf([name, code, note], RECORD), {
      formView: { renderTabBody: () => <div data-testid="custom-body" /> },
    });
    await screen.findByTestId('custom-body');
    await userEvent.click(screen.getByRole('button', { name: 'Related' }));
    expect(screen.queryByTestId('custom-body')).toBeNull();
    expect(screen.getByText(/Help page is not available here/)).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Back to the form' }));
    expect(screen.getByTestId('custom-body')).toBeInTheDocument();
  });

  it('tabList keeps the Related tab inside the tab list (no extra link)', async () => {
    const navigation = [{ kind: 'unsupported' as const, id: 'navLink', labels: { 1033: 'Help page' } }];
    renderForm(twoTabs({ navigation }), contextOf([name, code, note], RECORD), {
      formView: { renderTabBody: ({ tabList }) => tabList() },
    });
    expect(await screen.findByRole('tab', { name: 'Related' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Related' })).toBeNull();
    expect(screen.getByLabelText('xv_name label')).toHaveValue('Alpha');
  });
});

describe('FormView party lists and email (plan 4.1b, task 9)', () => {
  const to = attr('to', { kind: 'partylist', typeName: 'PartyList', displayName: 'To', targets: ['contact'] });
  const statecode = attr('statecode', { kind: 'choice', typeName: 'State', displayName: 'Status', options: [{ value: 0, label: 'Open' }, { value: 1, label: 'Completed' }] });
  const LOGICAL = '@Microsoft.Dynamics.CRM.lookuplogicalname';
  const FORMATTED = '@OData.Community.Display.V1.FormattedValue';
  const withParties = (statecodeValue: number) => ({
    ...RECORD,
    statecode: statecodeValue,
    xv_project_activity_parties: [
      { participationtypemask: 2, _partyid_value: 'c1', [`_partyid_value${LOGICAL}`]: 'contact', [`_partyid_value${FORMATTED}`]: 'Ada' },
      { participationtypemask: 2, _partyid_value: null, addressused: 'orders@example.com' },
    ],
  });
  const ctxWith = (state: number) => {
    const ctx = contextOf([name, to, statecode], withParties(state));
    return { ...ctx, fieldContext: { ...ctx.fieldContext, partyNavigation: 'xv_project_activity_parties' } };
  };

  it('draws a party list as chips that can be removed', async () => {
    renderForm(simpleForm([field('xv_name'), field('to')]), ctxWith(0));
    const list = await screen.findByRole('list', { name: 'to label' });
    expect(within(list).getAllByRole('listitem').map((li) => li.textContent)).toEqual(['Ada', expect.stringContaining('orders@example.com')]);
    expect(screen.getByRole('button', { name: 'Remove Ada' })).toBeInTheDocument();
  });

  it('a completed activity (state 1) opens read-only, its parties too', async () => {
    renderForm(simpleForm([field('xv_name'), field('to')]), ctxWith(1));
    await screen.findByRole('list', { name: 'to label' });
    expect(screen.queryByRole('button', { name: 'Remove Ada' })).toBeNull();
    expect(screen.getByLabelText('xv_name label')).toBeDisabled();
    expect(screen.getByText("Read-only: this record's status is Completed.")).toBeInTheDocument();
  });

  it('shows the banner under the title and the aside next to the body', async () => {
    renderForm(simpleForm([field('xv_name')]), contextOf([name], RECORD), {
      formView: { banner: <div>the banner</div>, aside: <aside aria-label="side">the aside</aside> },
    });
    expect(await screen.findByText('the banner')).toBeInTheDocument();
    expect(screen.getByRole('complementary', { name: 'side' })).toBeInTheDocument();
  });
});

describe('FormView email layout (aside, memo height)', () => {
  const body = attr('xv_body', { kind: 'memo', displayName: 'Body' });
  const aside = { formView: { aside: <div data-testid="aside" /> } };

  it('an aside goes at the top of the last column of a two-column tab', async () => {
    const form = formOf([
      tab('general', [
        { width: '66%', sections: [section([[field('xv_name')]], { name: 'message' })] },
        { width: '34%', sections: [section([[field('xv_code')]], { name: 'details' })] },
      ]),
    ]);
    const { container } = renderForm(form, contextOf([name, code], RECORD), aside);
    await screen.findByLabelText('xv_name label');
    const columns = container.querySelectorAll('[data-form-column]');
    expect(columns).toHaveLength(2);
    const last = columns[1] as HTMLElement;
    const node = within(last).getByTestId('aside');
    expect(within(columns[0] as HTMLElement).queryByTestId('aside')).toBeNull();
    expect(last.firstElementChild!.contains(node)).toBe(true);
    // Wrapped with a section's vertical margins, so it lines up with the first section of the other column.
    expect(last.firstElementChild).toHaveAttribute('data-form-lead');
    expect(node.compareDocumentPosition(within(last).getByLabelText('xv_code label')) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    // No separate aside grid: the body holds the tab only (plus the footer strip).
    expect(container.querySelectorAll('[data-testid="aside"]')).toHaveLength(1);
    expect((container.querySelector('[data-form-body]') as HTMLElement).className).not.toMatch(/withAside/);
  });

  it('an aside stays beside the body of a one-column tab', async () => {
    const { container } = renderForm(simpleForm([field('xv_name')]), contextOf([name], RECORD), aside);
    await screen.findByLabelText('xv_name label');
    expect(within(container.querySelector('[data-form-column]') as HTMLElement).queryByTestId('aside')).toBeNull();
    const asideNode = screen.getByTestId('aside');
    const grid = container.querySelector('[data-form-body]') as HTMLElement;
    expect(grid.children).toHaveLength(2);
    expect(grid.children[1]!.contains(asideNode)).toBe(true);
  });

  it('a memo cell with rowspan 12 shows a 24-row text area', async () => {
    renderForm(simpleForm([field('xv_body', { rowSpan: 12 })]), contextOf([body], { ...RECORD, xv_body: 'hi' }));
    expect(await screen.findByLabelText('xv_body label')).toHaveAttribute('rows', '24');
  });

  it('a memo cell without rowspan keeps the default text area', async () => {
    renderForm(simpleForm([field('xv_body')]), contextOf([body], { ...RECORD, xv_body: 'hi' }));
    expect(await screen.findByLabelText('xv_body label')).not.toHaveAttribute('rows');
  });
});
