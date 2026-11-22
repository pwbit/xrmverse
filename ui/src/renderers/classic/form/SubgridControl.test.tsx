import { render as rtlRender, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { createMemoryRouter, RouterProvider, useLocation } from 'react-router-dom';
import { describe, expect, it } from 'vitest';
import { onPath } from '../../../admin/security/testSupport';
import { useRecordForm } from '../../../apps/record/useRecordForm';
import { useUnsavedGuard } from '../../../apps/record/useUnsavedGuard';
import { mockApps } from '../../../apps/testSupport';
import { AuthProvider, useAuth } from '../../../shell/auth/AuthProvider';
import { FormView } from './FormView';
import { attr } from '../../../admin/tables/testAttrs';
import { rawOneToMany } from '../../../admin/tables/tableTestSupport';
import type { AppsMock, MockView } from '../../../apps/testSupport';
import type { Cell, NavItem } from '../../../model/form';
import { server } from '../../../test/server';
import { contextOf, field, formOf, Page, Providers, RECORD_ID, renderForm, section, tab } from './formTestSupport';

const name = attr('xv_name', { isPrimaryName: true, displayName: 'Name' });
const RECORD = { xv_projectid: RECORD_ID, xv_name: 'Alpha' };
const APP_ID = '11111111-0000-0000-0000-000000000001';
const VIEW_ID = 'a0000000-0000-0000-0000-0000000000a1';

const TASK_VIEW: MockView = {
  id: VIEW_ID,
  name: 'Active Tasks',
  entity: 'xv_task',
  isdefault: true,
  fetchxml: `<fetch version="1.0" mapping="logical"><entity name="xv_task"><attribute name="xv_title"/><attribute name="xv_taskid"/><order attribute="xv_title" descending="false"/></entity></fetch>`,
  layoutxml: `<grid name="resultset" jump="xv_title" select="1" icon="1" preview="1"><row name="result" id="xv_taskid"><cell name="xv_title" width="200"/></row></grid>`,
};
const TASKS: AppsMock = {
  savedQueries: [TASK_VIEW],
  tables: {
    xv_task: {
      entitySetName: 'xv_tasks',
      primaryId: 'xv_taskid',
      labels: { xv_title: { 1033: 'Title' } },
    },
  },
};

function subgridCell(over: Partial<Extract<Cell['control'] & { kind: 'subgrid' }, object>> = {}): Cell {
  return {
    id: 'cell-tasks',
    labels: { 1033: 'Tasks' },
    showLabel: true,
    visible: true,
    colSpan: 1,
    rowSpan: 1,
    control: {
      kind: 'subgrid',
      id: 'tasks',
      relationship: 'xv_project_task',
      targetEntity: 'xv_task',
      viewId: VIEW_ID,
      recordsPerPage: 4,
      enableViewPicker: false,
      ...over,
    },
  };
}

const formWith = (cell: Cell) =>
  formOf([
    tab('main', [
      {
        width: '100%',
        sections: [section([[field('xv_name')], [cell]])],
      },
    ]),
  ]);

const relationships = () =>
  server.use(
    http.get('*/api/data/v9.2/RelationshipDefinitions', () =>
      HttpResponse.json({
        value: [rawOneToMany('xv_project_task', 'xv_task', 'xv_projectid', 'xv_project')],
      }),
    ),
  );

function render(cell: Cell, apps: AppsMock = {}, data: Record<string, unknown> | null = RECORD) {
  return renderForm(formWith(cell), contextOf([name], data ?? undefined), {
    apps: { ...TASKS, ...apps },
    setup: relationships,
    formView: { appId: APP_ID },
  });
}

const rows = () => ({
  value: [
    { xv_taskid: 't1', xv_title: 'Write docs' },
    { xv_taskid: 't2', xv_title: 'Ship it' },
  ],
});

describe('SubgridControl', () => {
  it('subgrid_uses_records_per_page', async () => {
    const fetches: Element[] = [];
    render(subgridCell({ recordsPerPage: 3 }), {
      records: (_set, fetch) => {
        fetches.push(fetch);
        return { value: [{ xv_taskid: 't1', xv_title: 'Write docs' }] };
      },
    });
    expect(await screen.findByText('Write docs')).toBeInTheDocument();
    expect(screen.getByRole('grid', { name: 'Active Tasks' })).toBeInTheDocument();
    expect(fetches).toHaveLength(1);
    expect(fetches[0]!.getAttribute('count')).toBe('3');
    // The view's rows are limited to the record's.
    const condition = fetches[0]!.getElementsByTagName('condition')[0]!;
    expect(condition.getAttribute('attribute')).toBe('xv_projectid');
    expect(condition.getAttribute('value')).toBe(RECORD_ID);
    expect(screen.getByRole('columnheader', { name: 'Title' })).toBeInTheDocument();
    expect(screen.getByText('Tasks')).toBeInTheDocument();
  });

  it('subgrid_pages_with_next_and_previous', async () => {
    const pages: (string | null)[] = [];
    render(subgridCell({ recordsPerPage: 1 }), {
      records: (_set, fetch) => {
        const page = fetch.getAttribute('page');
        pages.push(page);
        return {
          value: [{ xv_taskid: `t${page}`, xv_title: `Task ${page}` }],
          '@Microsoft.Dynamics.CRM.morerecords': page === '1',
        };
      },
    });
    expect(await screen.findByText('Task 1')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Previous page' })).toBeDisabled();
    await userEvent.click(screen.getByRole('button', { name: 'Next page' }));
    expect(await screen.findByText('Task 2')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Next page' })).toBeDisabled();
    expect(pages).toEqual(['1', '2']);
  });

  it('subgrid_on_create_form_says_save_first', async () => {
    const { requests } = render(subgridCell(), {}, null);
    expect(await screen.findByText('Save the record to see related records.')).toBeInTheDocument();
    expect(requests.some((r) => r.includes('fetchXml'))).toBe(false);
  });

  it('unknown_relationship_or_view_shows_reason', async () => {
    const { unmount } = render(subgridCell({ relationship: 'xv_nope' }));
    expect(await screen.findByText('This subgrid cannot be shown: the relationship xv_nope does not exist.')).toBeInTheDocument();
    // The form still renders.
    expect(screen.getByLabelText('xv_name label')).toHaveValue('Alpha');
    unmount();

    const missingView = render(subgridCell({ viewId: '{a0000000-0000-0000-0000-0000000000ff}' }));
    expect(
      await screen.findByText(/This subgrid cannot be shown: the view a0000000-0000-0000-0000-0000000000ff does not exist\./),
    ).toBeInTheDocument();
    missingView.unmount();

    const unset = render(subgridCell({ relationship: '' }));
    expect(await screen.findByText('This subgrid cannot be shown: the control names no relationship.')).toBeInTheDocument();
    unset.unmount();

    render(subgridCell({ viewId: '' }));
    expect(await screen.findByText('This subgrid cannot be shown: the control names no view.')).toBeInTheDocument();
  });

  it('row_click_opens_related_record', async () => {
    render(subgridCell(), { records: rows });
    const link = await screen.findByRole('button', { name: 'Ship it' });
    await userEvent.click(link);
    await waitFor(() => {
      const location = screen.getByTestId('location').textContent!;
      const params = new URLSearchParams(location.slice(location.indexOf('?')));
      expect(location.startsWith('/main.aspx?')).toBe(true);
      expect(params.get('pagetype')).toBe('entityrecord');
      expect(params.get('etn')).toBe('xv_task');
      expect(params.get('id')).toBe('t2');
      expect(params.get('appid')).toBe(APP_ID);
    });
    // Save & Close and Delete on the related record come back to this form (the router starts at `/`).
    expect(screen.getByTestId('location-return-to')).toHaveTextContent(/^\/$/);
  });

  it('main_form_chart_subgrid_shows_notice_and_grid', async () => {
    render(subgridCell({ chartGridMode: 'Chart', visualizationId: '{c0000000-0000-0000-0000-000000000001}' }), { records: rows });
    expect(await screen.findByText("This subgrid's chart is not shown on a form in this version; its records are listed instead.")).toBeInTheDocument();
    expect(await screen.findByRole('button', { name: 'Ship it' })).toBeInTheDocument();
  });

  it('main_form_grid_mode_subgrid_shows_no_chart_notice', async () => {
    render(subgridCell({ chartGridMode: 'Grid', visualizationId: '{c0000000-0000-0000-0000-000000000001}' }), { records: rows });
    expect(await screen.findByRole('button', { name: 'Ship it' })).toBeInTheDocument();
    expect(screen.queryByText(/chart is not shown/)).toBeNull();
  });

  it('subgrid_has_no_new_or_add_existing_commands_without_privileges', async () => {
    render(subgridCell());
    await screen.findByRole('grid', { name: 'Active Tasks' });
    expect(
      within(document.body).queryByRole('button', {
        name: /new|add existing/i,
      }),
    ).toBeNull();
  });
});

describe('SubgridControl shared reads and refresh', () => {
  const twoSubgrids = formOf([
    tab('main', [{ width: '100%', sections: [section([[field('xv_name')], [subgridCell()], [{ ...subgridCell(), id: 'cell-tasks-2' }]])] }]),
  ]);

  it('two_subgrids_on_the_same_table_read_the_relationships_once', async () => {
    let relationshipReads = 0;
    renderForm(twoSubgrids, contextOf([name], RECORD), {
      apps: { ...TASKS, records: () => ({ value: [{ xv_taskid: 't1', xv_title: 'Write docs' }] }) },
      setup: () => {
        server.use(
          http.get('*/api/data/v9.2/RelationshipDefinitions', () => {
            relationshipReads += 1;
            return HttpResponse.json({ value: [rawOneToMany('xv_project_task', 'xv_task', 'xv_projectid', 'xv_project')] });
          }),
        );
      },
      formView: { appId: APP_ID },
    });
    await waitFor(() => expect(screen.getAllByRole('grid', { name: 'Active Tasks' })).toHaveLength(2));
    expect(relationshipReads).toBe(1);
  });

  it('enable_view_picker_shows_a_note', async () => {
    render(subgridCell({ enableViewPicker: true }));
    expect(await screen.findByText('Other views are not available here yet.')).toBeInTheDocument();
  });

  it('refresh_key_reloads_the_subgrid', async () => {
    let reads = 0;
    const apps: AppsMock = {
      ...TASKS,
      records: () => {
        reads += 1;
        return { value: [{ xv_taskid: 't1', xv_title: 'Write docs' }] };
      },
    };
    const form = formWith(subgridCell());
    const ctx = contextOf([name], RECORD);
    const first = renderForm(form, ctx, { apps, setup: relationships, formView: { appId: APP_ID, refreshKey: 0 } });
    await screen.findByText('Write docs');
    expect(reads).toBe(1);
    first.rerender(
      <Providers>
        <Page form={form} ctx={ctx} lcid={1033} onSelectForm={() => {}} appId={APP_ID} refreshKey={1} />
      </Providers>,
    );
    await waitFor(() => expect(reads).toBe(2));
  });
});

describe('SubgridControl unsaved changes', () => {
  const form = formWith(subgridCell());
  const ctx = contextOf([name], RECORD);

  function Guarded() {
    const state = useRecordForm(ctx, form, { lcid: 1033 });
    const { blocker } = useUnsavedGuard(state.dirty);
    const { pathname } = useLocation();
    return (
      <>
        <FormView form={form} ctx={ctx} lcid={1033} state={state} appId={APP_ID} onSelectForm={() => {}} />
        <output data-testid="path">{pathname}</output>
        {blocker.state === 'blocked' && (
          <div role="alert">
            Discard your changes?
            <button onClick={() => blocker.reset()}>Stay</button>
          </div>
        )}
      </>
    );
  }
  const Gate = () => (useAuth().api ? <Guarded /> : null);

  it('row_click_on_changed_form_asks_and_cancel_keeps_edits', async () => {
    mockApps({ ...TASKS, records: rows });
    relationships();
    server.use(onPath('get', `xv_projects(${RECORD_ID})`, () => HttpResponse.json(RECORD)));
    const future = { v7_relativeSplatPath: true, v7_fetcherPersist: true, v7_normalizeFormMethod: true, v7_partialHydration: true, v7_skipActionErrorRevalidation: true };
    const element = (
      <AuthProvider>
        <Gate />
      </AuthProvider>
    );
    const router = createMemoryRouter([{ path: '*', element }], { initialEntries: ['/form'], future });
    rtlRender(<RouterProvider router={router} future={{ v7_startTransition: true }} />);
    const input = await screen.findByLabelText('xv_name label');
    await userEvent.type(input, ' edited');
    await userEvent.click(await screen.findByRole('button', { name: 'Ship it' }));
    expect(await screen.findByText(/Discard your changes\?/)).toBeInTheDocument();
    expect(screen.getByTestId('path')).toHaveTextContent('/form');
    await userEvent.click(screen.getByRole('button', { name: 'Stay' }));
    expect(screen.getByLabelText('xv_name label')).toHaveValue('Alpha edited');
    expect(screen.getByTestId('path')).toHaveTextContent('/form');
  });
});

describe('Related tab (#120)', () => {
  const ASSOCIATED: MockView = { ...TASK_VIEW, id: 'a0000000-0000-0000-0000-0000000000a2', name: 'Task Associated View', querytype: 2, isdefault: true };
  const navForm = (navigation: NavItem[]) =>
    formOf([tab('main', [{ width: '100%', sections: [section([[field('xv_name')]])] }])], { navigation });
  const items: NavItem[] = [
    { kind: 'relationship', id: 'navTasks', relationship: 'xv_project_task', labels: { 1033: 'Tasks' } },
    { kind: 'unsupported', id: 'navLink', labels: { 1033: 'Help page' } },
    { kind: 'relationship', id: 'navOther', relationship: 'xv_other_task', labels: {} },
  ];
  const renderNav = (navigation = items, apps: AppsMock = {}) =>
    renderForm(navForm(navigation), contextOf([name], RECORD), {
      apps: { ...TASKS, savedQueries: [TASK_VIEW, ASSOCIATED], records: rows, ...apps },
      setup: () =>
        server.use(
          http.get('*/api/data/v9.2/RelationshipDefinitions', () =>
            HttpResponse.json({
              value: [rawOneToMany('xv_project_task', 'xv_task', 'xv_projectid', 'xv_project'), rawOneToMany('xv_other_task', 'xv_task', 'xv_otherid', 'xv_other')],
            }),
          ),
        ),
      formView: { appId: APP_ID },
    });

  it('related_tab_lists_the_rows_in_the_associated_view', async () => {
    const fetches: Element[] = [];
    renderNav(items, {
      records: (_set, fetch) => {
        fetches.push(fetch);
        return rows();
      },
    });
    await screen.findByLabelText('xv_name label');
    await userEvent.click(screen.getByRole('tab', { name: 'Related' }));
    expect(await screen.findByRole('grid', { name: 'Task Associated View' })).toBeInTheDocument();
    expect(await screen.findByText('Ship it')).toBeInTheDocument();
    const condition = fetches[0]!.getElementsByTagName('condition')[0]!;
    expect(condition.getAttribute('attribute')).toBe('xv_projectid');
    expect(condition.getAttribute('value')).toBe(RECORD_ID);
    expect(fetches[0]!.getAttribute('count')).toBe('10');
    // Back to the form's own tab.
    await userEvent.click(screen.getByRole('tab', { name: /main/i }));
    expect(screen.getByLabelText('xv_name label')).toHaveValue('Alpha');
  });

  it('related_tab_falls_back_to_the_default_public_view', async () => {
    renderNav(items, { savedQueries: [TASK_VIEW] });
    await userEvent.click(await screen.findByRole('tab', { name: 'Related' }));
    expect(await screen.findByRole('grid', { name: 'Active Tasks' })).toBeInTheDocument();
  });

  it('unsupported_item_says_so_and_foreign_relationship_shows_reason', async () => {
    renderNav();
    await userEvent.click(await screen.findByRole('tab', { name: 'Related' }));
    const chooser = screen.getByRole('combobox', { name: 'Choose a related table' });
    await userEvent.selectOptions(chooser, 'navLink');
    expect(await screen.findByText(/Help page is not available here/)).toBeInTheDocument();
    // An item without a label shows its id.
    await userEvent.selectOptions(chooser, 'navOther');
    expect(screen.getByRole('option', { name: 'navOther' })).toBeInTheDocument();
    expect(
      await screen.findByText("This subgrid cannot be shown: the relationship xv_other_task does not relate this table's records."),
    ).toBeInTheDocument();
  });

  it('form_without_navigation_has_no_related_tab', async () => {
    renderNav([]);
    await screen.findByLabelText('xv_name label');
    expect(screen.queryByRole('tab', { name: 'Related' })).toBeNull();
  });
});

describe('Subgrid New and Add existing (#118)', () => {
  const SCHEMAS = { xv_task: 'xv_Task', xv_project: 'xv_Project', xv_tag: 'xv_Tag' };
  const ALL = ['prvCreatexv_Task', 'prvAppendxv_Task', 'prvAppendToxv_Project', 'prvCreatexv_Tag', 'prvAppendxv_Tag'];
  const TABLES: AppsMock['tables'] = {
    xv_task: { entitySetName: 'xv_tasks', primaryId: 'xv_taskid', primaryName: 'xv_title', displayName: 'Task', labels: { xv_title: { 1033: 'Title' } } },
    xv_tag: { entitySetName: 'xv_tags', primaryId: 'xv_tagid', primaryName: 'xv_name', displayName: 'Tag', labels: {} },
  };
  const TAG_VIEW: MockView = {
    id: 'a0000000-0000-0000-0000-0000000000b1',
    name: 'Active Tags',
    entity: 'xv_tag',
    isdefault: true,
    fetchxml: `<fetch version="1.0" mapping="logical"><entity name="xv_tag"><attribute name="xv_name"/><attribute name="xv_tagid"/></entity></fetch>`,
    layoutxml: `<grid name="resultset" jump="xv_name" select="1" icon="1" preview="1"><row name="result" id="xv_tagid"><cell name="xv_name" width="200"/></row></grid>`,
  };
  const nn = {
    '@odata.type': '#Microsoft.Dynamics.CRM.ManyToManyRelationshipMetadata',
    SchemaName: 'xv_project_tag',
    Entity1LogicalName: 'xv_project',
    Entity2LogicalName: 'xv_tag',
    IntersectEntityName: 'xv_project_tag',
    Entity1IntersectAttribute: 'xv_projectid',
    Entity2IntersectAttribute: 'xv_tagid',
    Entity1NavigationPropertyName: 'xv_project_tags',
    Entity2NavigationPropertyName: 'xv_tag_projects',
  };
  function renderCommands(cell: Cell, privileges = ALL, records?: AppsMock['records']) {
    const base = contextOf([name], RECORD);
    const ctx = { ...base, entity: { ...base.entity, schemaName: 'xv_Project' } };
    return renderForm(formWith(cell), ctx, {
      apps: { ...TASKS, savedQueries: [TASK_VIEW, TAG_VIEW], tables: TABLES, privileges, schemaNames: SCHEMAS, records: records ?? rows },
      setup: () =>
        server.use(
          http.get('*/api/data/v9.2/RelationshipDefinitions', () =>
            HttpResponse.json({ value: [rawOneToMany('xv_project_task', 'xv_task', 'xv_projectid', 'xv_project', 'xv_projectid'), nn] }),
          ),
        ),
      formView: { appId: APP_ID },
    });
  }

  it('new_opens_the_related_create_form_with_the_lookup_set', async () => {
    renderCommands(subgridCell());
    await userEvent.click(await screen.findByRole('button', { name: 'New Task' }));
    await waitFor(() => {
      const location = screen.getByTestId('location').textContent!;
      const params = new URLSearchParams(location.slice(location.indexOf('?')));
      expect(params.get('etn')).toBe('xv_task');
      expect(params.get('id')).toBeNull();
      expect(Object.fromEntries(new URLSearchParams(params.get('extraqs')!))).toEqual({
        xv_projectid: RECORD_ID,
        xv_projectidname: 'Alpha',
        xv_projectidtype: 'xv_project',
      });
    });
    expect(screen.getByTestId('location-return-to')).toHaveTextContent(/^\/$/);
    // The create form fills the mapped columns from this record (#143).
    expect(JSON.parse(screen.getByTestId('location-from-subgrid').textContent!).createFrom).toEqual({
      table: 'xv_project',
      set: 'xv_projects',
      id: RECORD_ID,
      name: 'Alpha',
    });
  });

  it('add_existing_one_to_many_patches_the_lookup_of_the_chosen_rows', async () => {
    const patches: { path: string; body: unknown }[] = [];
    const searches: string[] = [];
    renderCommands(subgridCell());
    server.use(
      http.get('*/api/data/v9.2/xv_tasks', ({ request }) => {
        const url = new URL(request.url);
        if (url.searchParams.has('fetchXml')) return HttpResponse.json(rows());
        searches.push(url.searchParams.get('$filter') ?? '');
        return HttpResponse.json({ value: [{ xv_taskid: 't9', xv_title: 'Loose task' }, { xv_taskid: 't8', xv_title: 'Other task' }] });
      }),
      onPath('patch', 'xv_tasks(t9)', async ({ request }) => {
        patches.push({ path: 'xv_tasks(t9)', body: await request.json() });
        return new HttpResponse(null, { status: 204 });
      }),
      onPath('patch', 'xv_tasks(t8)', async ({ request }) => {
        patches.push({ path: 'xv_tasks(t8)', body: await request.json() });
        return new HttpResponse(null, { status: 204 });
      }),
    );
    await userEvent.click(await screen.findByRole('button', { name: 'Add Existing Task' }));
    const dialog = await screen.findByRole('dialog');
    await userEvent.click(await within(dialog).findByRole('option', { name: 'Loose task' }));
    await userEvent.click(within(dialog).getByRole('option', { name: 'Other task' }));
    await userEvent.click(within(dialog).getByRole('button', { name: 'Add' }));
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
    expect(patches).toEqual([
      { path: 'xv_tasks(t9)', body: { 'xv_projectid@odata.bind': `/xv_projects(${RECORD_ID})` } },
      { path: 'xv_tasks(t8)', body: { 'xv_projectid@odata.bind': `/xv_projects(${RECORD_ID})` } },
    ]);
    expect(searches[0]).toBe(`(_xv_projectid_value eq null or _xv_projectid_value ne ${RECORD_ID})`);
  });

  it('add_existing_many_to_many_posts_ref_and_an_existing_association_counts_as_done', async () => {
    const refs: { path: string; body: unknown }[] = [];
    renderCommands(subgridCell({ relationship: 'xv_project_tag', targetEntity: 'xv_tag', viewId: TAG_VIEW.id }));
    server.use(
      http.get('*/api/data/v9.2/xv_tags', ({ request }) => {
        const url = new URL(request.url);
        if (url.searchParams.has('fetchXml')) return HttpResponse.json({ value: [] });
        return HttpResponse.json({ value: [{ xv_tagid: 'g1', xv_name: 'Urgent' }] });
      }),
      http.post('*/api/data/v9.2/*', async ({ request }) => {
        refs.push({ path: new URL(request.url).pathname, body: await request.json() });
        return HttpResponse.json({ error: { code: '0x80040237', message: 'Cannot insert duplicate key.' } }, { status: 412 });
      }),
    );
    await userEvent.click(await screen.findByRole('button', { name: 'Add Existing Tag' }));
    const dialog = await screen.findByRole('dialog');
    await userEvent.click(await within(dialog).findByRole('option', { name: 'Urgent' }));
    await userEvent.click(within(dialog).getByRole('button', { name: 'Add' }));
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
    expect(refs).toHaveLength(1);
    expect(refs[0]!.path).toBe(`/api/data/v9.2/xv_projects(${RECORD_ID})/xv_project_tags/$ref`);
    expect(refs[0]!.body).toEqual({ '@odata.id': `${location.origin}/api/data/v9.2/xv_tags(g1)` });
  });

  it('commands_follow_the_privileges', async () => {
    const { unmount } = renderCommands(subgridCell(), ['prvCreatexv_Task', 'prvAppendxv_Task']);
    expect(await screen.findByRole('button', { name: 'New Task' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Add Existing Task' })).toBeNull();
    unmount();
    renderCommands(subgridCell(), ['prvAppendxv_Task', 'prvAppendToxv_Project']);
    expect(await screen.findByRole('button', { name: 'Add Existing Task' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'New Task' })).toBeNull();
  });
});
