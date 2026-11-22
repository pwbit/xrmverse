import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { lbl, rawOneToMany } from '../../../admin/tables/tableTestSupport';
import { attr } from '../../../admin/tables/testAttrs';
import type { RecordForm } from '../../../apps/record/useRecordForm';
import { mockApps, type AppsMock, type MockView } from '../../../apps/testSupport';
import type { Cell, SubgridControl as SubgridModel } from '../../../model/form';
import type { ViewPresentation } from '../../../parsers/presentationJson';
import { server } from '../../../test/server';
import type { ControlEnv } from './controlEnv';
import { contextOf, field, formOf, Providers, RECORD_ID, renderForm, section, tab } from './formTestSupport';
import { SubgridControl } from './SubgridControl';
import { modeNames, pickMode, queryShowAs, showAs } from '../../../apps/views/modeMenuTestSupport';

const name = attr('xv_name', { isPrimaryName: true, displayName: 'Name' });
const RECORD = { xv_projectid: RECORD_ID, xv_name: 'Alpha' };
const APP_ID = '11111111-0000-0000-0000-000000000001';
const VIEW_ID = 'a0000000-0000-0000-0000-0000000000c1';
const ATTRIBUTES = "EntityDefinitions(LogicalName='contact')/Attributes";

const CONTACT_VIEW: MockView = {
  id: VIEW_ID,
  name: 'Active Contacts',
  entity: 'contact',
  isdefault: true,
  fetchxml: `<fetch version="1.0" mapping="logical"><entity name="contact"><attribute name="fullname"/><attribute name="contactid"/><order attribute="fullname" descending="false"/></entity></fetch>`,
  layoutxml: `<grid name="resultset" jump="fullname" select="1" icon="1" preview="1"><row name="result" id="contactid"><cell name="fullname" width="200"/></row></grid>`,
};

const CONTACTS: AppsMock = {
  savedQueries: [CONTACT_VIEW],
  tables: { contact: { entitySetName: 'contacts', primaryId: 'contactid', primaryName: 'fullname', labels: { fullname: { 1033: 'Full Name' } } } },
};

const BOARD: ViewPresentation = { kanban: { groupBy: 'statuscode' }, default: 'kanban' };

function contactsCell(over: Partial<SubgridModel> = {}): Cell {
  return {
    id: 'cell-contacts',
    labels: { 1033: 'Contacts' },
    showLabel: true,
    visible: true,
    colSpan: 1,
    rowSpan: 1,
    control: {
      kind: 'subgrid',
      id: 'Contacts',
      relationship: 'xv_project_contact',
      targetEntity: 'contact',
      viewId: VIEW_ID,
      recordsPerPage: 4,
      enableViewPicker: false,
      ...over,
    },
  };
}

interface Options {
  cell?: Cell;
  entries?: Record<string, ViewPresentation>;
  /** The form id the entries belong to; the form's own ('f1') by default. */
  formId?: string;
  noModes?: boolean;
  records?: (fetch: Element) => Record<string, unknown>;
  /** The user may add existing contacts (prvAppendContact, prvAppendToxv_Project). */
  addExisting?: boolean;
  /** The columns of contact cannot be read: the metadata list answers 500. */
  metadataFails?: boolean;
}

/** The project form with a contacts subgrid and its modes; returns the fetches of the subgrid and the paths read. */
function mount(o: Options = {}) {
  const fetches: Element[] = [];
  const subgridModes: ControlEnv['subgridModes'] = o.noModes ? undefined : { formId: o.formId ?? 'f1', entries: o.entries ?? { contacts: BOARD } };
  const form = formOf([tab('main', [{ width: '100%', sections: [section([[field('xv_name')], [o.cell ?? contactsCell()]])] }])]);
  const base = contextOf([name], RECORD);
  const ctx = o.addExisting ? { ...base, entity: { ...base.entity, schemaName: 'xv_Project' } } : base;
  const { requests } = renderForm(form, ctx, {
    apps: {
      ...CONTACTS,
      ...(o.addExisting ? { privileges: ['prvAppendContact', 'prvAppendToxv_Project'], schemaNames: { contact: 'Contact', xv_project: 'xv_Project' } } : {}),
      records: (_set, fetch) => {
        fetches.push(fetch);
        return o.records ? o.records(fetch) : { value: [] };
      },
    },
    setup: () =>
      server.use(
        http.get('*/api/data/v9.2/RelationshipDefinitions', () =>
          HttpResponse.json({ value: [rawOneToMany('xv_project_contact', 'contact', 'xv_projectid', 'xv_project', 'xv_projectid')] }),
        ),
        http.get('*/api/data/v9.2/*', ({ request }) => {
          const url = new URL(request.url);
          const path = decodeURIComponent(url.pathname.replace(/^.*\/api\/data\/v9\.2\//, ''));
          if (path === ATTRIBUTES && url.searchParams.has('$filter')) {
            if (o.metadataFails) return HttpResponse.json({ error: { code: '0x80040217', message: 'Metadata is down.' } }, { status: 500 });
            return HttpResponse.json({
              value: [
                { LogicalName: 'statuscode', AttributeType: 'Status' },
                { LogicalName: 'createdon', AttributeType: 'DateTime' },
              ],
            });
          }
          if (path === `${ATTRIBUTES}(LogicalName='createdon')/Microsoft.Dynamics.CRM.DateTimeAttributeMetadata`) {
            return HttpResponse.json({ DateTimeBehavior: { Value: 'UserLocal' }, Format: 'DateAndTime' });
          }
          if (path === `${ATTRIBUTES}(LogicalName='statuscode')/Microsoft.Dynamics.CRM.StatusAttributeMetadata`) {
            return HttpResponse.json({
              OptionSet: {
                Options: [
                  { Value: 1, Label: lbl('Active'), State: 0 },
                  { Value: 2, Label: lbl('Inactive'), State: 1 },
                ],
              },
            });
          }
          return undefined;
        }),
      ),
    formView: { appId: APP_ID, ...(subgridModes ? { subgridModes } : {}) },
  });
  return { fetches, requests };
}

const board = () => document.querySelector('[data-mode="kanban"]');
const metadataRead = (requests: string[]) => requests.some((r) => r.startsWith(ATTRIBUTES) && r.includes('LogicalName eq'));

const boardRow = (statuscode: number) => ({
  contactid: 'c1',
  fullname: 'Ada Lovelace',
  statuscode,
  statecode: statuscode === 1 ? 0 : 1,
  'statuscode@OData.Community.Display.V1.FormattedValue': statuscode === 1 ? 'Active' : 'Inactive',
});
const gridRows = (fetch: Element) =>
  fetch.getAttribute('count') === '250' ? { value: [boardRow(1)] } : { value: [{ contactid: 'c1', fullname: 'Ada Lovelace' }] };

afterEach(() => vi.restoreAllMocks());

describe('SubgridControl view modes', () => {
  it('board_by_status_reads_the_related_rows_up_to_the_board_limit', async () => {
    const { fetches } = mount({ records: gridRows });
    expect(await screen.findByRole('list', { name: 'Active' })).toBeInTheDocument();
    expect(screen.getByRole('list', { name: 'Inactive' })).toBeInTheDocument();
    expect(board()).not.toBeNull();
    expect(screen.getByTestId('subgrid')).toBeInTheDocument();
    expect(screen.queryByRole('grid')).toBeNull();
    expect(fetches).toHaveLength(1);
    const fetch = fetches[0]!;
    expect(fetch.getAttribute('page')).toBe('1');
    expect(fetch.getAttribute('count')).toBe('250');
    // The relationship's filter: the contacts of this record only.
    const condition = fetch.querySelector('condition[attribute="xv_projectid"]')!;
    expect(condition.getAttribute('value')).toBe(RECORD_ID);
    expect(fetch.querySelector('entity > attribute[name="statuscode"]')).not.toBeNull();
  });

  it('switcher_lists_grid_and_board_and_grid_shows_the_paged_grid', async () => {
    const { fetches } = mount({ records: gridRows });
    expect(await modeNames()).toEqual(['Grid', 'Board']);
    await waitFor(() => expect(board()).not.toBeNull());
    await pickMode('Grid');
    expect(await screen.findByRole('grid', { name: 'Active Contacts' })).toBeInTheDocument();
    expect(board()).toBeNull();
    const paged = fetches.at(-1)!;
    expect(paged.getAttribute('count')).toBe('4');
    expect(paged.querySelector('condition[attribute="xv_projectid"]')).not.toBeNull();
    // The switcher stays, to go back to the board.
    expect(await showAs()).toBeInTheDocument();
  });

  it('move_patches_the_contact_with_its_state', async () => {
    let status = 1;
    const patches: { path: string; body: unknown; ifMatch: string | null }[] = [];
    mount({ records: () => ({ value: [boardRow(status)] }) });
    server.use(
      http.patch(/\/api\/data\/v9\.2\/contacts\(c1\)/, async ({ request }) => {
        patches.push({ path: new URL(request.url).pathname, body: await request.json(), ifMatch: request.headers.get('If-Match') });
        status = 2;
        return new HttpResponse(null, { status: 204 });
      }),
    );
    const active = await screen.findByRole('list', { name: 'Active' });
    await userEvent.click(within(active).getByRole('button', { name: 'Move to…' }));
    await userEvent.click(await screen.findByRole('menuitem', { name: 'Inactive' }));
    await waitFor(() => expect(patches).toHaveLength(1));
    expect(patches[0]!.path).toMatch(/contacts\(c1\)$/);
    expect(patches[0]!.body).toEqual({ statuscode: 2, statecode: 1 });
    expect(patches[0]!.ifMatch).toBe('*');
    await waitFor(() => expect(within(screen.getByRole('list', { name: 'Inactive' })).getByText('Ada Lovelace')).toBeInTheDocument());
  });

  it('refused_move_shows_the_server_message_and_puts_the_card_back', async () => {
    mount({ records: () => ({ value: [boardRow(1)] }) });
    server.use(
      http.patch(/\/api\/data\/v9\.2\/contacts\(c1\)/, () =>
        HttpResponse.json({ error: { code: '0x80040220', message: 'You do not hold prvWriteContact.' } }, { status: 403 }),
      ),
    );
    const active = await screen.findByRole('list', { name: 'Active' });
    await userEvent.click(within(active).getByRole('button', { name: 'Move to…' }));
    await userEvent.click(await screen.findByRole('menuitem', { name: 'Inactive' }));
    expect(await screen.findByText(/You do not hold prvWriteContact\./)).toBeInTheDocument();
    expect(within(screen.getByRole('list', { name: 'Active' })).getByText('Ada Lovelace')).toBeInTheDocument();
    expect(within(screen.getByRole('list', { name: 'Inactive' })).queryByText('Ada Lovelace')).toBeNull();
  });

  it('cards_page_like_the_grid_without_metadata_read', async () => {
    const { fetches, requests } = mount({ entries: { contacts: { cards: {}, default: 'cards' } }, records: gridRows });
    expect(await modeNames()).toEqual(['Grid', 'Cards']);
    expect(await screen.findByRole('list', { name: 'Active Contacts' })).toBeInTheDocument();
    expect(screen.queryByRole('grid')).toBeNull();
    expect(fetches.at(-1)!.getAttribute('count')).toBe('4');
    expect(metadataRead(requests)).toBe(false);
  });

  it('add_existing_on_the_board_reads_the_board_again', async () => {
    const { fetches } = mount({ addExisting: true, records: gridRows });
    server.use(
      http.get('*/api/data/v9.2/contacts', ({ request }) =>
        new URL(request.url).searchParams.has('fetchXml') ? undefined : HttpResponse.json({ value: [{ contactid: 'c9', fullname: 'Grace Hopper' }] }),
      ),
      http.patch(/\/api\/data\/v9\.2\/contacts\(c9\)/, () => new HttpResponse(null, { status: 204 })),
    );
    await screen.findByRole('list', { name: 'Active' });
    expect(fetches.filter((f) => f.getAttribute('count') === '250')).toHaveLength(1);
    await userEvent.click(await screen.findByRole('button', { name: /Add Existing/ }));
    const dialog = await screen.findByRole('dialog');
    await userEvent.click(await within(dialog).findByRole('option', { name: 'Grace Hopper' }));
    await userEvent.click(within(dialog).getByRole('button', { name: 'Add' }));
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
    await waitFor(() => expect(fetches.filter((f) => f.getAttribute('count') === '250')).toHaveLength(2));
    expect(board()).not.toBeNull();
  });

  it('no_entry_shows_the_grid_without_switcher_or_metadata_read', async () => {
    const { requests } = mount({ entries: { other: BOARD }, records: gridRows });
    expect(await screen.findByRole('grid', { name: 'Active Contacts' })).toBeInTheDocument();
    expect(queryShowAs()).toBeNull();
    expect(metadataRead(requests)).toBe(false);
  });

  it('no_subgrid_modes_shows_the_grid', async () => {
    const { requests } = mount({ noModes: true, records: gridRows });
    expect(await screen.findByRole('grid', { name: 'Active Contacts' })).toBeInTheDocument();
    expect(queryShowAs()).toBeNull();
    expect(metadataRead(requests)).toBe(false);
  });

  it('entries_of_another_form_are_not_used', async () => {
    const { requests } = mount({ formId: '{F2000000-0000-0000-0000-000000000002}', records: gridRows });
    expect(await screen.findByRole('grid', { name: 'Active Contacts' })).toBeInTheDocument();
    expect(queryShowAs()).toBeNull();
    expect(board()).toBeNull();
    expect(metadataRead(requests)).toBe(false);
  });

  it('dataset_control_keeps_its_path_and_warns', async () => {
    const warn = vi.spyOn(console, 'warn').mockImplementation(() => {});
    const cell = contactsCell({
      customControl: { name: 'xv_NoSuchGrid', parameters: {}, dataSet: { name: 'records', parameters: { ViewId: `{${VIEW_ID}}`, TargetEntityType: 'contact', RelationshipName: 'xv_project_contact', RecordsPerPage: '4' } } },
    });
    mount({ cell, records: gridRows });
    expect(await screen.findByRole('grid', { name: 'Active Contacts' })).toBeInTheDocument();
    expect(queryShowAs()).toBeNull();
    expect(board()).toBeNull();
    const messages = warn.mock.calls.filter((c) => c[0] === '[xrmverse presentation]').map((c) => String(c[1]));
    expect(messages).toHaveLength(1);
    expect(messages[0]).toContain('"contacts"');
  });

  it('related_items_stay_grids', async () => {
    const { requests } = mount({ cell: contactsCell({ related: true }), records: gridRows });
    expect(await screen.findByRole('grid', { name: 'Active Contacts' })).toBeInTheDocument();
    expect(queryShowAs()).toBeNull();
    expect(metadataRead(requests)).toBe(false);
  });

  it('calendar_shows_the_month_and_reads_the_related_rows_of_its_grid', async () => {
    const now = new Date();
    const { fetches } = mount({
      entries: { contacts: { calendar: { start: 'createdon' }, default: 'calendar' } },
      records: (fetch) =>
        fetch.querySelector('condition[operator="null"]')
          ? { value: [], '@Microsoft.Dynamics.CRM.totalrecordcount': 0 }
          : { value: [{ contactid: 'c1', fullname: 'Ada Lovelace', createdon: now.toISOString() }] },
    });
    expect(await modeNames()).toEqual(['Grid', 'Calendar']);
    // The month's title and the event on its day.
    const title = new Intl.DateTimeFormat('en-US', { month: 'long', year: 'numeric', timeZone: 'UTC' }).format(now);
    expect(await screen.findByRole('heading', { name: title })).toBeInTheDocument();
    const event = await screen.findByRole('button', { name: /^Ada Lovelace,/ });
    expect(event.closest('[role="gridcell"]')).not.toBeNull();
    const ranged = fetches.find((f) => f.getAttribute('count') === '250')!;
    const conditions = [...ranged.querySelectorAll('condition[attribute="createdon"]')].map((c) => [c.getAttribute('operator'), c.getAttribute('value')]);
    expect(conditions.map(([op]) => op)).toEqual(['ge', 'le']);
    // The 6-week grid of this month holds now; the relationship's filter stays.
    expect(new Date(conditions[0]![1]!).getTime()).toBeLessThanOrEqual(now.getTime());
    expect(new Date(conditions[1]![1]!).getTime()).toBeGreaterThanOrEqual(now.getTime());
    expect(ranged.querySelector('condition[attribute="xv_projectid"]')?.getAttribute('value')).toBe(RECORD_ID);
  });

  it('calendar_opens_on_the_entry_layout_and_the_toggle_switches_it', async () => {
    const now = new Date();
    mount({
      entries: { contacts: { calendar: { start: 'createdon', layout: 'timeline' }, default: 'calendar' } },
      records: (fetch) =>
        fetch.querySelector('condition[operator="null"]')
          ? { value: [], '@Microsoft.Dynamics.CRM.totalrecordcount': 0 }
          : { value: [{ contactid: 'c1', fullname: 'Ada Lovelace', createdon: now.toISOString() }] },
    });
    await screen.findByRole('button', { name: /^Ada Lovelace,/ });
    const group = screen.getByRole('group', { name: 'Calendar layout' });
    expect(within(group).getByRole('button', { name: 'Timeline' })).toHaveAttribute('aria-pressed', 'true');
    await userEvent.click(within(group).getByRole('button', { name: 'Month' }));
    // The layouts draw their own toolbar: the toggle is read again.
    expect(within(screen.getByRole('group', { name: 'Calendar layout' })).getByRole('button', { name: 'Month' })).toHaveAttribute('aria-pressed', 'true');
    await screen.findByRole('button', { name: /^Ada Lovelace,/ });
  });

  it('more_rows_than_the_board_limit_shows_the_subgrid_notice', async () => {
    mount({ records: (fetch) => (fetch.getAttribute('count') === '250' ? { value: [boardRow(1)], '@Microsoft.Dynamics.CRM.morerecords': true } : { value: [] }) });
    expect(await screen.findByText('Showing the first 250 records. Use the grid to see the rest.')).toBeInTheDocument();
    expect(screen.queryByText(/quick find/)).toBeNull();
  });

  it('unreadable_columns_warn_about_the_subgrid_table_and_show_the_grid', async () => {
    const warn = vi.spyOn(console, 'warn').mockImplementation(() => {});
    mount({ metadataFails: true, records: gridRows });
    expect(await screen.findByRole('grid', { name: 'Active Contacts' })).toBeInTheDocument();
    const messages = warn.mock.calls.filter((c) => c[0] === '[xrmverse presentation]').map((c) => String(c[1]));
    expect(messages.some((m) => m.startsWith('The columns of the table contact of the subgrid could not be read'))).toBe(true);
  });

  it('dashboard_subgrid_ignores_an_entry', async () => {
    const fetches: Element[] = [];
    const requests = mockApps({
      ...CONTACTS,
      records: (_set, fetch) => {
        fetches.push(fetch);
        return gridRows(fetch);
      },
    });
    const ctx = contextOf([name], RECORD);
    const env: ControlEnv = {
      ctx,
      state: { record: RECORD } as unknown as RecordForm,
      lcid: 1033,
      formId: 'f1',
      subgridModes: { formId: 'f1', entries: { contacts: BOARD } },
    };
    const control = contactsCell().control as SubgridModel;
    render(
      <Providers>
        <SubgridControl control={control} env={env} parent={null} />
      </Providers>,
    );
    expect(await screen.findByRole('grid', { name: 'Active Contacts' })).toBeInTheDocument();
    expect(queryShowAs()).toBeNull();
    expect(board()).toBeNull();
    expect(metadataRead(requests)).toBe(false);
    // The view's own page, without the relationship's filter or the board's limit.
    expect(fetches.every((f) => f.getAttribute('count') === '4' && !f.querySelector('condition[attribute="xv_projectid"]'))).toBe(true);
  });
});
