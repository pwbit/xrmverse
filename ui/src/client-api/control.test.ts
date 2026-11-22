import { act, fireEvent, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { onPath } from '../admin/security/testSupport';
import { rawOneToMany } from '../admin/tables/tableTestSupport';
import { attr } from '../admin/tables/testAttrs';
import type { Cell } from '../model/form';
import { contextOf, field, formOf, RECORD_ID, section, simpleForm, tab } from '../renderers/classic/form/formTestSupport';
import { server } from '../test/server';
import type { Control } from './control';
import type { FormContext } from './formContext';
import { mountForm } from './testForm';

afterEach(() => vi.restoreAllMocks());

const name = attr('xv_name', { isPrimaryName: true, displayName: 'Name' });
const code = attr('xv_code', { displayName: 'Code' });
const RECORD = { xv_projectid: RECORD_ID, xv_name: 'Alpha', xv_code: 'A-1' };

type Ctl = Control & Record<string, (...a: unknown[]) => unknown>;
const ctl = (fc: FormContext, n: string) => fc.getControl(n) as Ctl;

const twoTabs = () =>
  formOf([
    tab('summary', [{ width: '100%', sections: [section([[field('xv_name')]], { name: 'general', labels: { 1033: 'General' } })] }], { labels: { 1033: 'Summary' } }),
    tab('details', [{ width: '100%', sections: [section([[field('xv_code')]], { name: 'more', labels: { 1033: 'More' } })] }], { labels: { 1033: 'Details' } }),
  ]);

describe('control', () => {
  it('header_footer_and_duplicate_control_names', async () => {
    const form = simpleForm([field('xv_name'), { ...field('xv_name'), id: 'second' }, field('xv_code')], {
      header: [{ ...field('xv_name'), id: 'h1' }],
      footer: [{ ...field('xv_code'), id: 'f1' }],
    });
    const m = mountForm(form, contextOf([name, code], RECORD));
    await screen.findAllByLabelText('xv_name label');
    const fc = m.fc();
    expect(fc.ui.controls.get().map((c) => c.getName())).toEqual(['xv_name', 'xv_name1', 'xv_code', 'header_xv_name', 'footer_xv_code']);
    expect((fc.getAttribute('xv_name') as { controls: { getLength(): number } }).controls.getLength()).toBe(3);
    expect(ctl(fc, 'header_xv_name').getParent()).toBeNull();
    expect(ctl(fc, 'footer_xv_code').getDisabled()).toBe(true);
    expect(ctl(fc, 'xv_name1').getControlType()).toBe('standard');
  });

  it('set_visible_hides_control_tab_section', async () => {
    const m = mountForm(twoTabs(), contextOf([name, code], RECORD));
    await screen.findByLabelText('xv_name label');
    const fc = m.fc();
    act(() => ctl(fc, 'xv_name').setVisible(false));
    // The overlay re-renders the form asynchronously on a slow runner.
    await waitFor(() => expect(screen.queryByLabelText('xv_name label')).toBeNull());
    expect(ctl(fc, 'xv_name').getVisible()).toBe(false);
    act(() => ctl(fc, 'xv_name').setVisible(true));
    expect(await screen.findByLabelText('xv_name label')).toBeInTheDocument();
    const general = fc.ui.tabs.get('summary')!.sections.get('general')! as unknown as Record<string, (v?: unknown) => unknown>;
    act(() => void general.setVisible(false));
    await waitFor(() => expect(screen.queryByRole('group', { name: 'General' })).toBeNull());
    expect(general.getVisible()).toBe(false);
    const details = fc.ui.tabs.get('details')! as unknown as Record<string, (v?: unknown) => unknown>;
    act(() => void details.setVisible(false));
    await waitFor(() => expect(screen.queryByRole('tab', { name: 'Details' })).toBeNull());
    act(() => void details.setVisible(true));
    expect(await screen.findByRole('tab', { name: 'Details' })).toBeInTheDocument();
  });

  it('set_disabled', async () => {
    const form = simpleForm([field('xv_name'), field('xv_code', {}, { disabled: true })]);
    const m = mountForm(form, contextOf([name, code], RECORD));
    const input = await screen.findByLabelText('xv_name label');
    const fc = m.fc();
    expect(ctl(fc, 'xv_code').getDisabled()).toBe(true);
    expect(input).toBeEnabled();
    act(() => ctl(fc, 'xv_name').setDisabled(true));
    // Query again: the cell may re-render with a new input element.
    await waitFor(() => expect(screen.getByLabelText('xv_name label')).toBeDisabled());
    expect(ctl(fc, 'xv_name').getDisabled()).toBe(true);
    // A control the FormXml disables is enabled by a script, and its value is then saved.
    act(() => ctl(fc, 'xv_code').setDisabled(false));
    await waitFor(() => expect(screen.getByLabelText('xv_code label')).toBeEnabled());
    const codeInput = screen.getByLabelText('xv_code label');
    await userEvent.clear(codeInput);
    await userEvent.type(codeInput, 'B-2');
    await userEvent.click(screen.getByRole('button', { name: 'Save now' }));
    await waitFor(() => expect(m.sent).toHaveLength(1));
    expect(m.sent[0]!.body).toEqual({ xv_code: 'B-2' });
  });

  it('set_label', async () => {
    const m = mountForm(twoTabs(), contextOf([name, code], RECORD));
    await screen.findByLabelText('xv_name label');
    const fc = m.fc();
    act(() => ctl(fc, 'xv_name').setLabel('Project name'));
    expect(screen.getByLabelText('Project name')).toHaveValue('Alpha');
    expect(ctl(fc, 'xv_name').getLabel()).toBe('Project name');
    const summary = fc.ui.tabs.get('summary')! as unknown as Record<string, (v?: unknown) => unknown>;
    act(() => void summary.setLabel('Overview'));
    expect(screen.getByRole('tab', { name: 'Overview' })).toBeInTheDocument();
    const general = summary.sections as unknown as { get(n: string): Record<string, (v?: unknown) => unknown> };
    act(() => void general.get('general').setLabel('Basics'));
    expect(screen.getByRole('group', { name: 'Basics' })).toBeInTheDocument();
    expect(general.get('general').getLabel()).toBe('Basics');
  });

  it('set_display_state_expanded_selects_tab', async () => {
    const m = mountForm(twoTabs(), contextOf([name, code], RECORD));
    await screen.findByLabelText('xv_name label');
    const details = m.fc().ui.tabs.get('details')! as unknown as Record<string, (v?: unknown) => unknown>;
    expect(details.getDisplayState()).toBe('collapsed');
    act(() => void details.setDisplayState('expanded'));
    expect(screen.getByRole('tab', { name: 'Details' })).toHaveAttribute('aria-selected', 'true');
    expect(await screen.findByLabelText('xv_code label')).toHaveValue('A-1');
    expect(details.getDisplayState()).toBe('expanded');
    expect(() => details.setDisplayState('open')).toThrow('setDisplayState on details');
    // setFocus on a control of another tab opens that tab.
    act(() => void ctl(m.fc(), 'xv_name').setFocus());
    expect(screen.getByRole('tab', { name: 'Summary' })).toHaveAttribute('aria-selected', 'true');
    await waitFor(() => expect(screen.getByLabelText('xv_name label')).toHaveFocus());
  });

  it('control_notification_shows_and_blocks_save', async () => {
    const m = mountForm(simpleForm([field('xv_name'), field('xv_code')]), contextOf([name, code], RECORD));
    await screen.findByLabelText('xv_name label');
    const fc = m.fc();
    act(() => void ctl(fc, 'xv_name').setNotification('Use the official name', 'official'));
    expect(screen.getByText('Use the official name')).toBeInTheDocument();
    act(() => void ctl(fc, 'xv_code').addNotification({ messages: ['Codes look like A-1'], notificationLevel: 'RECOMMENDATION', uniqueId: 'hint' }));
    expect(screen.getByRole('note')).toHaveTextContent('Codes look like A-1');
    await userEvent.click(screen.getByRole('button', { name: 'Save now' }));
    // The error is repeated at the top of the form and the save does not run.
    expect(within(screen.getByRole('region', { name: 'Form notifications' })).getByText('Use the official name')).toBeInTheDocument();
    expect(m.sent).toEqual([]);
    let cleared: unknown;
    act(() => void (cleared = ctl(fc, 'xv_name').clearNotification('official')));
    expect(cleared).toBe(true);
    expect(ctl(fc, 'xv_name').clearNotification('official')).toBe(false);
    expect(ctl(fc, 'xv_code').addNotification({ messages: ['x'], notificationLevel: 'WARNING', uniqueId: 'y' })).toBe(false);
    act(() => void ctl(fc, 'xv_name').setValue?.('ignored'));
    await userEvent.type(screen.getByLabelText('xv_name label'), '!');
    await userEvent.click(screen.getByRole('button', { name: 'Save now' }));
    // A recommendation does not block.
    await waitFor(() => expect(m.sent).toHaveLength(1));
  });

  it('choices_control_type', async () => {
    const tags = attr('xv_tags', { kind: 'choices', displayName: 'Tags', options: [{ value: 10, label: 'Red' }] });
    const m = mountForm(simpleForm([field('xv_name'), field('xv_tags')]), contextOf([name, tags], RECORD));
    await screen.findByLabelText('xv_name label');
    expect(ctl(m.fc(), 'xv_tags').getControlType()).toBe('choices');
    expect(ctl(m.fc(), 'xv_name').getControlType()).toBe('standard');
  });

  it('choice_control_options', async () => {
    const stage = attr('xv_stage', { kind: 'choice', displayName: 'Stage', options: [{ value: 1, label: 'Plan' }, { value: 2, label: 'Build' }, { value: 3, label: 'Run' }] });
    const m = mountForm(simpleForm([field('xv_stage')]), contextOf([stage], { ...RECORD, xv_stage: 1 }));
    const select = await screen.findByLabelText('xv_stage label');
    const fc = m.fc();
    // Ruling R6: Learn's control types.
    expect(ctl(fc, 'xv_stage').getControlType()).toBe('choice');
    act(() => void ctl(fc, 'xv_stage').removeOption(3));
    expect(within(select).queryByRole('option', { name: 'Run' })).toBeNull();
    act(() => void ctl(fc, 'xv_stage').addOption({ value: 3, text: 'Run' }, 0));
    expect(ctl(fc, 'xv_stage').getOptions()).toEqual([
      { value: 3, text: 'Run' },
      { value: 1, text: 'Plan' },
      { value: 2, text: 'Build' },
    ]);
    // The attribute keeps all its options.
    expect(((fc.getAttribute('xv_stage') as unknown as Record<string, () => unknown[]>).getOptions)()).toHaveLength(3);
  });
});

describe('lookup control', () => {
  const NODE_SET = 'xv_wbsnodes';
  const OTHER_PROJECT = 'aaaaaaaa-1111-0000-0000-000000000001';
  const node = attr('xv_parentid', { kind: 'lookup', targets: ['xv_wbsnode'], displayName: 'Parent node' });
  const LOOKUPS = [{ schemaName: 'n', referencingEntity: 'xv_project', referencingAttribute: 'xv_parentid', referencedEntity: 'xv_wbsnode', nav: 'xv_parentid' }];

  function mountLookup() {
    const searches: { fetch?: Element; odata?: string }[] = [];
    const m = mountForm(simpleForm([field('xv_name'), field('xv_parentid')]), contextOf([name, node], RECORD, { lookups: LOOKUPS }), {
      apps: {
        records: (set, fetch) => {
          if (set === NODE_SET) searches.push({ fetch });
          return { value: [{ xv_wbsnodeid: 'n1', xv_name: 'Design' }] };
        },
      },
      setup: () =>
        server.use(
          onPath('get', "EntityDefinitions(LogicalName='xv_wbsnode')", () =>
            HttpResponse.json({ EntitySetName: NODE_SET, PrimaryIdAttribute: 'xv_wbsnodeid', PrimaryNameAttribute: 'xv_name' }),
          ),
          http.get(`*/api/data/v9.2/${NODE_SET}`, ({ request }) => {
            const url = new URL(request.url);
            if (url.searchParams.get('fetchXml')) return undefined;
            searches.push({ odata: url.searchParams.get('$filter') ?? '' });
            return HttpResponse.json({ value: [{ xv_wbsnodeid: 'n1', xv_name: 'Design' }, { xv_wbsnodeid: 'n2', xv_name: 'Other project node' }] });
          }),
        ),
    });
    return { m, searches };
  }

  it('pre_search_custom_filter_narrows_lookup_search', async () => {
    // The OpenProject365 WBS node script: only the nodes of the same project.
    const { m, searches } = mountLookup();
    await screen.findByLabelText('xv_name label');
    const fc = m.fc();
    const parent = ctl(fc, 'xv_parentid');
    expect(parent.getControlType()).toBe('lookup');
    expect(parent.getEntityTypes()).toEqual(['xv_wbsnode']);
    const sources: unknown[] = [];
    act(() =>
      parent.addPreSearch((ctx: { getEventSource(): unknown }) => {
        sources.push(ctx.getEventSource());
        parent.addCustomFilter(`<filter type='and'><condition attribute='xv_projectid' operator='eq' value='${OTHER_PROJECT}' /></filter>`, 'xv_wbsnode');
        parent.addCustomFilter(`<filter><condition attribute='accountid' operator='null' /></filter>`, 'account');
      }),
    );
    await userEvent.click(screen.getByRole('button', { name: 'Choose xv_parentid label' }));
    expect(await screen.findByRole('option', { name: 'Design' })).toBeInTheDocument();
    expect(sources).toEqual([parent]);
    const fetch = searches.at(-1)!.fetch!;
    expect(fetch.getAttribute('count')).toBe('20');
    const conditions = [...fetch.getElementsByTagName('condition')].map((c) => `${c.getAttribute('attribute')} ${c.getAttribute('operator')}`);
    // Only the filter for the searched table; the other table's is left out.
    expect(conditions).toEqual(['xv_projectid eq']);
    await userEvent.type(screen.getByRole('textbox', { name: 'xv_parentid label: search' }), 'Des');
    await waitFor(() => expect(sources).toHaveLength(2));
    await waitFor(() => expect([...searches.at(-1)!.fetch!.getElementsByTagName('condition')].map((c) => c.getAttribute('value'))).toEqual(['%Des%', OTHER_PROJECT]));
    await userEvent.click(screen.getByRole('option', { name: 'Design' }));
    expect((fc.getAttribute('xv_parentid') as unknown as { getValue(): unknown }).getValue()).toEqual([{ id: '{N1}', name: 'Design', entityType: 'xv_wbsnode' }]);
  });

  it('add_custom_filter_outside_pre_search_ignored', async () => {
    const warn = vi.spyOn(console, 'warn').mockImplementation(() => undefined);
    const { m, searches } = mountLookup();
    await screen.findByLabelText('xv_name label');
    const parent = ctl(m.fc(), 'xv_parentid');
    parent.addCustomFilter(`<filter><condition attribute='xv_projectid' operator='null' /></filter>`);
    expect(warn).toHaveBeenCalledWith('addCustomFilter on xv_parentid was called outside a PreSearch handler and is ignored.');
    await userEvent.click(screen.getByRole('button', { name: 'Choose xv_parentid label' }));
    expect(await screen.findByRole('option', { name: 'Other project node' })).toBeInTheDocument();
    // The plain OData search, without any filter.
    expect(searches).toEqual([{ odata: '' }]);
  });
});

describe('subgrid control', () => {
  const VIEW_ID = 'a0000000-0000-0000-0000-0000000000a1';
  const subgrid: Cell = {
    id: 'cell-tasks',
    labels: { 1033: 'Tasks' },
    showLabel: true,
    visible: true,
    colSpan: 1,
    rowSpan: 1,
    control: { kind: 'subgrid', id: 'tasks', relationship: 'xv_project_task', targetEntity: 'xv_task', viewId: VIEW_ID, recordsPerPage: 4, enableViewPicker: false },
  };

  it('subgrid_control_refresh_reloads_rows', async () => {
    let reads = 0;
    const m = mountForm(simpleForm([field('xv_name'), subgrid]), contextOf([name], RECORD), {
      apps: {
        savedQueries: [
          {
            id: VIEW_ID,
            name: 'Active Tasks',
            entity: 'xv_task',
            isdefault: true,
            fetchxml: `<fetch version="1.0" mapping="logical"><entity name="xv_task"><attribute name="xv_title"/><attribute name="xv_taskid"/></entity></fetch>`,
            layoutxml: `<grid name="resultset" jump="xv_title" select="1" icon="1" preview="1"><row name="result" id="xv_taskid"><cell name="xv_title" width="200"/></row></grid>`,
          },
        ],
        tables: { xv_task: { entitySetName: 'xv_tasks', primaryId: 'xv_taskid', labels: { xv_title: { 1033: 'Title' } } } },
        records: () => ({ value: [{ xv_taskid: 't1', xv_title: `Read ${++reads}` }] }),
      },
      setup: () =>
        server.use(
          http.get('*/api/data/v9.2/RelationshipDefinitions', () =>
            HttpResponse.json({ value: [rawOneToMany('xv_project_task', 'xv_task', 'xv_projectid', 'xv_project')] }),
          ),
        ),
    });
    expect(await screen.findByText('Read 1')).toBeInTheDocument();
    const tasks = ctl(m.fc(), 'tasks');
    expect(tasks.getControlType()).toBe('subgrid');
    expect(tasks.getEntityName()).toBe('xv_task');
    act(() => void tasks.refresh());
    expect(await screen.findByText('Read 2')).toBeInTheDocument();
    expect(() => tasks.getGrid()).toThrow('formContext.getControl("tasks").getGrid is not available in this version (stage after UI-1).');
    // Notifications on a control that is not a field fail explicitly.
    expect(() => tasks.addNotification({ messages: ['x'], notificationLevel: 'ERROR', uniqueId: 'u' })).toThrow(
      'formContext.getControl("tasks").addNotification is not available in this version',
    );
    expect(() => tasks.setNotification('x')).toThrow('formContext.getControl("tasks").setNotification is not available');
    expect(() => tasks.clearNotification()).toThrow('formContext.getControl("tasks").clearNotification is not available');
  });
});

describe('timeline control', () => {
  const timeline: Cell = {
    id: 'cell-timeline',
    labels: { 1033: 'Timeline' },
    showLabel: true,
    visible: true,
    colSpan: 1,
    rowSpan: 1,
    control: { kind: 'timeline', id: 'notescontrol', classId: '{06375649-C143-495E-A496-C962E5B4488E}', parameterNames: [] },
  };
  /** A notes-only table: the timeline reads annotations only. */
  const notesOnly = () => {
    const ctx = contextOf([name], RECORD);
    return { ...ctx, entity: { ...ctx.entity, hasActivities: false, hasNotes: true } };
  };
  /** Each page read of the notes answers one note titled after the read number; the page reads' `$filter` are kept. */
  const notesServer = (filters: string[]) => () =>
    server.use(
      http.get('*/api/data/v9.2/annotations', ({ request }) => {
        const url = new URL(request.url);
        if (url.searchParams.get('$count') === 'true') return HttpResponse.json({ '@odata.count': 1, value: [] });
        filters.push(url.searchParams.get('$filter') ?? '');
        return HttpResponse.json({ value: [{ annotationid: 'n1', subject: `Read ${filters.length}`, modifiedon: '2026-11-16T08:00:00Z', _ownerid_value: 'u1' }] });
      }),
    );

  it('timeline_refresh_reloads_first_page', async () => {
    const filters: string[] = [];
    const m = mountForm(simpleForm([field('xv_name'), timeline]), notesOnly(), { apps: { privileges: ['prvReadNote'] }, setup: notesServer(filters) });
    expect(await screen.findByText('Read 1')).toBeInTheDocument();
    const wall = ctl(m.fc(), 'notescontrol');
    expect(wall.getControlType()).toBe('timelinewall');
    // The current filters stay: Mine is on before the refresh and still on after it.
    await userEvent.click(screen.getByRole('button', { name: /^Mine/ }));
    expect(await screen.findByText('Read 2')).toBeInTheDocument();
    expect(filters[1]).toContain('_ownerid_value eq');
    act(() => void wall.refresh());
    expect(await screen.findByText('Read 3')).toBeInTheDocument();
    expect(filters[2]).toContain('_ownerid_value eq');
    // The shared members work on it too.
    expect(wall.getName()).toBe('notescontrol');
    expect(wall.getLabel()).toBe('Timeline');
    act(() => void wall.setLabel('Activity wall'));
    expect(await screen.findByText('Activity wall')).toBeInTheDocument();
    expect(wall.getVisible()).toBe(true);
    act(() => void wall.setVisible(false));
    await waitFor(() => expect(screen.queryByTestId('timeline')).toBeNull());
  });

  it('timeline_set_focus_moves_focus_into_the_timeline', async () => {
    const filters: string[] = [];
    const m = mountForm(simpleForm([field('xv_name'), timeline]), notesOnly(), { apps: { privileges: ['prvReadNote', 'prvCreateNote'] }, setup: notesServer(filters) });
    expect(await screen.findByText('Read 1')).toBeInTheDocument();
    act(() => void ctl(m.fc(), 'notescontrol').setFocus());
    // The first item takes the focus (the list's tab stop), not the first button of the header.
    await waitFor(() => expect(screen.getByRole('listitem')).toHaveFocus());
  });

  it('timeline_set_focus_without_items_focuses_the_search', async () => {
    const m = mountForm(simpleForm([field('xv_name'), timeline]), notesOnly(), {
      apps: { privileges: ['prvReadNote'] },
      setup: () => server.use(http.get('*/api/data/v9.2/annotations', () => HttpResponse.json({ '@odata.count': 0, value: [] }))),
    });
    expect(await screen.findByText('No activities or notes to show.')).toBeInTheDocument();
    act(() => void ctl(m.fc(), 'notescontrol').setFocus());
    await waitFor(() => expect(screen.getByRole('searchbox', { name: 'Search timeline' })).toHaveFocus());
  });

  it('timeline_set_disabled_hides_create_and_actions', async () => {
    const m = mountForm(simpleForm([field('xv_name'), timeline]), notesOnly(), {
      apps: { privileges: ['prvReadNote', 'prvCreateNote', 'prvWriteNote', 'prvDeleteNote'] },
      setup: notesServer([]),
    });
    expect(await screen.findByText('Read 1')).toBeInTheDocument();
    const wall = ctl(m.fc(), 'notescontrol');
    expect(wall.getDisabled()).toBe(false);
    expect(screen.getByRole('button', { name: 'Create a timeline record' })).toBeInTheDocument();
    expect(screen.getByPlaceholderText('Take a note...')).toBeInTheDocument();
    expect(screen.getByRole('toolbar', { name: 'Actions for Read 1' })).toBeInTheDocument();
    act(() => void wall.setDisabled(true));
    await waitFor(() => expect(screen.queryByRole('button', { name: 'Create a timeline record' })).toBeNull());
    expect(wall.getDisabled()).toBe(true);
    expect(screen.queryByPlaceholderText('Take a note...')).toBeNull();
    expect(screen.queryByRole('toolbar', { name: 'Actions for Read 1' })).toBeNull();
    // A dropped file does not open the note entry.
    fireEvent.drop(screen.getByTestId('timeline'), { dataTransfer: { files: [new File(['hi'], 'a.txt')], types: ['Files'] } });
    await new Promise((r) => setTimeout(r, 50));
    expect(screen.queryByRole('textbox', { name: 'Title' })).toBeNull();
    // The list is still shown and can be read.
    expect(screen.getByText('Read 1')).toBeInTheDocument();
    act(() => void wall.setDisabled(false));
    expect(await screen.findByRole('button', { name: 'Create a timeline record' })).toBeInTheDocument();
  });
});
