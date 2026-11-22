import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { describe, expect, it, vi } from 'vitest';
import { installMetadata, lbl, rawAttr, rawEntity } from '../admin/tables/tableTestSupport';
import { App } from '../shell/App';
import { server } from '../test/server';
import { ACTIVE, APP_ID as VIEWS_APP_ID, CUSTOMERS, openAccounts, rows } from '../renderers/classic/viewTestSupport';
import { t } from '../shell/i18n/t';
import { mockApps, ROLE_SALES, SITEMAP_ID, titled } from './testSupport';
import { modeNames, pickMode, queryShowAs } from './views/modeMenuTestSupport';

const APP_ID = '11111111-0000-0000-0000-000000000001';
const SALES = { appmoduleid: APP_ID, name: 'Sales Hub', roles: [ROLE_SALES], siteMapId: SITEMAP_ID };
const SITEMAP = `<SiteMap><Area Id="sales">${titled('Sales')}<Group Id="c">${titled('Customers')}<SubArea Id="accounts" Entity="account">${titled('Accounts')}</SubArea></Group></Area></SiteMap>`;

const account = rawEntity(
  'account',
  'accounts',
  [
    rawAttr('accountid', 'Uniqueidentifier', { IsPrimaryId: true }),
    rawAttr('name', 'String', { IsPrimaryName: true, display: 'Account Name' }),
    rawAttr('statecode', 'State', { OptionSet: { Options: [{ Value: 0, Label: lbl('Active') }], IsGlobal: false } }),
  ],
  { SchemaName: 'Account', PrimaryNameAttribute: 'name', display: 'Account' },
);
const formXml =
  '<form><tabs><tab name="general"><labels><label description="General" languagecode="1033"/></labels><columns><column width="100%"><sections><section name="s" showlabel="false" columns="1"><rows><row><cell id="{n}"><labels><label description="Name" languagecode="1033"/></labels><control id="name" classid="{4273EDBD-AC1D-40d3-9FB2-095C621B552D}" datafieldname="name"/></cell></row></rows></section></sections></column></columns></tab></tabs></form>';

function open(privileges: string[]) {
  mockApps({ apps: [SALES], userRoles: [ROLE_SALES], privileges, siteMapXml: SITEMAP });
  installMetadata([account]);
  server.use(
    http.get('*/api/data/v9.2/systemforms', () =>
      HttpResponse.json({ value: [{ systemformid: '10000000-0000-0000-0000-000000000001', name: 'Main', objecttypecode: 'account', type: 2, isdefault: true, formactivationstate: 1, formxml: formXml }] }),
    ),
  );
  render(<App initialEntries={[`/main.aspx?appid=${APP_ID}&pagetype=entitylist&etn=account`]} />);
}

describe('EntityListPage New', () => {
  it('list_new_button_opens_create_form_with_privilege', async () => {
    open(['prvReadAccount', 'prvCreateAccount']);
    await userEvent.click(await screen.findByRole('button', { name: 'New' }));
    expect(await screen.findByRole('heading', { level: 1, name: 'New Account' })).toBeInTheDocument();
  });

  it('list_new_hidden_without_privilege', async () => {
    open(['prvReadAccount']);
    expect(await screen.findByRole('heading', { level: 1, name: 'Accounts' })).toBeInTheDocument();
    // The privilege check has finished once the list has loaded its views.
    expect(await screen.findByText('This table has no view that you may open.')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'New' })).toBeNull();
  });
});

describe('EntityListPage paging', () => {
  it('paging buttons keep their names and show icons, not « ‹ ›', async () => {
    openAccounts({
      answer: () => ({ value: rows(50), '@Microsoft.Dynamics.CRM.morerecords': true }),
    });
    await screen.findByText('Account 50');
    for (const name of ['First page', 'Previous page', 'Next page']) {
      expect(screen.getByRole('button', { name }).querySelector('svg')).not.toBeNull();
    }
    for (const button of within(screen.getByRole('group', { name: 'Pages' })).getAllByRole('button')) {
      expect(['«', '‹', '›']).not.toContain(button.textContent?.trim());
    }
  });

  it('the New button shows an icon and keeps its name', async () => {
    open(['prvReadAccount', 'prvCreateAccount']);
    const button = await screen.findByRole('button', { name: 'New' });
    expect(button.querySelector('svg[aria-hidden="true"]')).not.toBeNull();
  });
});

describe('EntityListPage view modes (UI-2b)', () => {
  const LOOK = 'x_/look.json';
  const BASE = "EntityDefinitions(LogicalName='account')/Attributes";
  const OVERLAY = {
    version: 1,
    views: {
      [`{${ACTIVE.id.toUpperCase()}}`]: { default: 'kanban', kanban: { groupBy: 'statecode' } },
      [CUSTOMERS.id]: { cards: {} },
    },
  };
  const board = () => document.querySelector('[data-mode="kanban"]');
  const selector = (name: string) => screen.findByRole('button', { name: `Select a view: ${name}` });

  /** The accounts list in the modern look with `OVERLAY`; statecode is a State column. */
  function openModern(answer?: (fetch: Element) => Record<string, unknown>, overlay: Record<string, unknown> = OVERLAY) {
    const opened = openAccounts({
      apps: [{ appmoduleid: VIEWS_APP_ID, name: 'Sales Hub', roles: [ROLE_SALES], siteMapId: SITEMAP_ID, settings: { xrmverse_Renderer: 'modern', xrmverse_Presentation: LOOK } }],
      ...(answer ? { answer } : {}),
    });
    server.use(
      http.get(`*/WebResources/${LOOK}`, () => HttpResponse.json(overlay)),
      http.get('*/api/data/v9.2/*', ({ request }) => {
        const url = new URL(request.url);
        const path = decodeURIComponent(url.pathname.replace(/^.*\/api\/data\/v9\.2\//, ''));
        if (path === BASE && url.searchParams.has('$filter')) {
          return HttpResponse.json({ value: [{ LogicalName: 'statecode', AttributeType: 'State' }, { LogicalName: 'name', AttributeType: 'String' }, { LogicalName: 'due', AttributeType: 'DateTime' }] });
        }
        if (path === `${BASE}(LogicalName='due')/Microsoft.Dynamics.CRM.DateTimeAttributeMetadata`) {
          return HttpResponse.json({ DateTimeBehavior: { Value: 'UserLocal' } });
        }
        if (path === `${BASE}(LogicalName='statecode')/Microsoft.Dynamics.CRM.StateAttributeMetadata`) {
          return HttpResponse.json({ OptionSet: { Options: [{ Value: 0, Label: lbl('Active') }, { Value: 1, Label: lbl('Inactive') }] } });
        }
        return undefined;
      }),
    );
    return opened;
  }

  it('classic_has_no_switcher_and_shows_the_grid', async () => {
    openAccounts();
    await screen.findByRole('grid', { name: 'Active Accounts' });
    expect(queryShowAs()).toBeNull();
    expect(board()).toBeNull();
  });

  it('modern_with_default_kanban_shows_the_board_and_grid_on_choice', async () => {
    const { fetches } = openModern();
    expect(await modeNames()).toEqual(['Grid', 'Board']);
    await waitFor(() => expect(board()).not.toBeNull());
    expect(screen.queryByRole('grid')).toBeNull();
    // One board request: the view's query with the groupBy column, 250 records.
    expect(fetches).toHaveLength(1);
    expect(fetches[0].getAttribute('count')).toBe('250');
    expect(fetches[0].querySelector('entity > attribute[name="statecode"]')).not.toBeNull();
    await pickMode('Grid');
    await screen.findByRole('grid', { name: 'Active Accounts' });
    expect(board()).toBeNull();
  }, 15_000);

  it('selecting_another_view_resets_the_mode_to_its_default', async () => {
    openModern();
    await waitFor(() => expect(board()).not.toBeNull());
    await pickMode('Grid');
    await screen.findByRole('grid', { name: 'Active Accounts' });
    await userEvent.click(await selector('Active Accounts'));
    await userEvent.click(await screen.findByRole('menuitemradio', { name: 'Customers with Projects' }));
    await screen.findByRole('grid', { name: 'Customers with Projects' });
    expect(await modeNames()).toEqual(['Grid', 'Cards']);
    await userEvent.click(await selector('Customers with Projects'));
    await userEvent.click(await screen.findByRole('menuitemradio', { name: 'Active Accounts' }));
    await waitFor(() => expect(board()).not.toBeNull());
    // Four view loads in the modern look (each grid also reads its choice colours): above the default 5 s on a busy runner.
  }, 15_000);

  it('quick_find_on_the_board_sends_the_groupby_column_and_stays_on_the_board', async () => {
    const { fetches } = openModern();
    await waitFor(() => expect(board()).not.toBeNull());
    await userEvent.type(screen.getByRole('searchbox', { name: 'Search this view' }), 'Cont{Enter}');
    await waitFor(() => expect(fetches.length).toBe(2));
    const fetch = fetches[1];
    expect(fetch.querySelector('filter[isquickfindfields="1"]')).not.toBeNull();
    expect(fetch.querySelector('entity > attribute[name="statecode"]')).not.toBeNull();
    expect(fetch.getAttribute('count')).toBe('250');
    await waitFor(() => expect(board()).not.toBeNull());
    expect(screen.queryByRole('grid')).toBeNull();
  });

  it('moving_a_card_patches_the_record_and_reloads_the_board', async () => {
    let state = 0;
    const patches: { path: string; body: unknown; ifMatch: string | null }[] = [];
    const { fetches } = openModern(() => ({
      value: [{ accountid: 'a1', name: 'Account 1', statecode: state, 'statecode@OData.Community.Display.V1.FormattedValue': state ? 'Inactive' : 'Active' }],
    }));
    server.use(
      http.patch(/\/api\/data\/v9\.2\/accounts\(a1\)/, async ({ request }) => {
        patches.push({ path: new URL(request.url).pathname, body: await request.json(), ifMatch: request.headers.get('If-Match') });
        state = 1;
        return new HttpResponse(null, { status: 204 });
      }),
    );
    const active = await screen.findByRole('list', { name: 'Active' });
    await userEvent.click(within(active).getByRole('button', { name: 'Move to…' }));
    await userEvent.click(await screen.findByRole('menuitem', { name: 'Inactive' }));
    await waitFor(() => expect(patches).toHaveLength(1));
    expect(patches[0].body).toEqual({ statecode: 1 });
    // An update only: a record deleted meanwhile must answer 404, not be created again.
    expect(patches[0].ifMatch).toBe('*');
    await waitFor(() => expect(fetches.length).toBe(2));
    expect(within(screen.getByRole('list', { name: 'Inactive' })).getByText('Account 1')).toBeInTheDocument();
  });

  it('unreadable_column_kinds_drop_the_modes_with_one_warning_only', async () => {
    const warn = vi.spyOn(console, 'warn').mockImplementation(() => {});
    openModern();
    server.use(
      http.get('*/api/data/v9.2/*', ({ request }) => {
        const url = new URL(request.url);
        return decodeURIComponent(url.pathname).endsWith(`${BASE}`) && url.searchParams.has('$filter') ? HttpResponse.json({ error: { message: 'boom' } }, { status: 500 }) : undefined;
      }),
    );
    await screen.findByRole('grid', { name: 'Active Accounts' });
    const messages = warn.mock.calls.filter((c) => c[0] === '[xrmverse presentation]').map((c) => String(c[1]));
    expect(messages).toHaveLength(1);
    expect(messages[0]).toContain('could not be read');
    warn.mockRestore();
  });

  it('more_records_than_the_board_limit_shows_the_limit_notice', async () => {
    openModern(() => ({ value: rows(3), '@Microsoft.Dynamics.CRM.morerecords': true }));
    expect(await screen.findByText(t('apps.views.modes.limit'))).toBeInTheDocument();
    expect(board()).not.toBeNull();
  });

  describe('calendar', () => {
    const CALENDAR = { version: 1, views: { [`{${ACTIVE.id.toUpperCase()}}`]: { default: 'calendar', calendar: { start: 'due' } } } };
    const conditions = (fetch: Element) => [...fetch.querySelectorAll('entity > filter > condition[attribute="due"]')].map((c) => [c.getAttribute('operator'), c.getAttribute('value')]);

    it('requests_the_visible_grid_and_shows_the_event_on_its_day_and_the_undated_count', async () => {
      const now = new Date();
      const { fetches } = openModern((fetch) => {
        if (fetch.querySelector('condition[operator="null"]')) return { value: [], '@Microsoft.Dynamics.CRM.totalrecordcount': 3 };
        return { value: [{ accountid: 'a1', name: 'Kick-off', due: now.toISOString() }] };
      }, CALENDAR);
      const day = await screen.findByRole('button', { name: /^Kick-off, / }, { timeout: 10_000 });
      expect(day.closest('[role="gridcell"]')).not.toBeNull();
      expect(await screen.findByText('Without a date, not shown: 3')).toBeInTheDocument();
      const ranged = fetches.find((f) => !f.querySelector('condition[operator="null"]'))!;
      const [[geOp, from], [leOp, to]] = conditions(ranged);
      expect([geOp, leOp]).toEqual(['ge', 'le']);
      // The 6-week grid of the current month holds now.
      expect(new Date(from!).getTime()).toBeLessThanOrEqual(now.getTime());
      expect(new Date(to!).getTime()).toBeGreaterThanOrEqual(now.getTime());
      expect(ranged.getAttribute('count')).toBe('250');
      const counting = fetches.find((f) => f.querySelector('condition[operator="null"]'))!;
      expect(counting.getAttribute('count')).toBe('1');
      expect(counting.getAttribute('returntotalrecordcount')).toBe('true');
    }, 15_000);
  });
});

describe('EntityListPage command bar (W8)', () => {
  const stateAccount = rawEntity(
    'account',
    'accounts',
    [
      rawAttr('accountid', 'Uniqueidentifier', { IsPrimaryId: true }),
      rawAttr('name', 'String', { IsPrimaryName: true }),
      rawAttr('statecode', 'State', {
        OptionSet: { Options: [{ Value: 0, Label: lbl('Active'), DefaultStatus: 1 }, { Value: 1, Label: lbl('Inactive'), DefaultStatus: 2 }], IsGlobal: false },
      }),
      rawAttr('statuscode', 'Status', {
        OptionSet: { Options: [{ Value: 1, Label: lbl('Active'), State: 0 }, { Value: 2, Label: lbl('Inactive'), State: 1 }], IsGlobal: false },
      }),
    ],
    { SchemaName: 'Account', PrimaryNameAttribute: 'name', display: 'Account' },
  );

  /** The accounts list with the state metadata; `states` answers the selection's statecode read. */
  function openWithStates(answer: () => Record<string, unknown>, privileges = ['prvReadAccount', 'prvCreateAccount']) {
    const patches: { path: string; body: unknown }[] = [];
    const stateReads: string[] = [];
    const opened = openAccounts({ answer, privileges });
    installMetadata([stateAccount]);
    server.use(
      http.get('*/api/data/v9.2/accounts', ({ request }) => {
        const url = new URL(request.url);
        if (url.searchParams.has('fetchXml')) return undefined;
        stateReads.push(url.searchParams.get('$filter') ?? '');
        const ids = [...(url.searchParams.get('$filter') ?? '').matchAll(/accountid eq ([\w-]+)/g)].map((m) => m[1]);
        return HttpResponse.json({ value: ids.map((accountid) => ({ accountid, statecode: 0 })) });
      }),
      http.patch(/\/api\/data\/v9\.2\/accounts\(/, async ({ request }) => {
        patches.push({ path: decodeURIComponent(new URL(request.url).pathname).replace(/^.*\/v9\.2\//, ''), body: await request.json() });
        return new HttpResponse(null, { status: 204 });
      }),
    );
    return { ...opened, patches, stateReads };
  }

  const toolbar = () => screen.findByRole('toolbar', { name: 'View commands' });

  it('New is a button of the toolbar', async () => {
    openWithStates(() => ({ value: rows(2) }));
    expect(await within(await toolbar()).findByRole('button', { name: 'New' })).toBeInTheDocument();
  });

  it('one header card holds the table name, the view selector, the toolbar and the search box', async () => {
    openWithStates(() => ({ value: rows(2) }));
    const selector = await screen.findByRole('button', { name: /^Select a view: / });
    const bar = await toolbar();
    const header = screen.getByRole('region', { name: 'View header' });
    expect(header).toContainElement(bar);
    expect(header).toContainElement(await screen.findByRole('heading', { level: 1, name: 'Accounts' }));
    expect(header).toContainElement(selector);
    expect(header).toContainElement(await screen.findByRole('searchbox', { name: 'Search this view' }));
    // The view actions and Refresh are in the toolbar.
    expect(within(bar).getByRole('button', { name: 'View actions' })).toBeInTheDocument();
    expect(within(bar).getByRole('button', { name: 'Refresh' })).toBeInTheDocument();
  });

  it('selecting two rows and Deactivate patches both and reports 2 of 2 done', async () => {
    const { patches, stateReads, fetches } = openWithStates(() => ({ value: rows(2) }));
    await screen.findByText('Account 2');
    await userEvent.click(screen.getByRole('checkbox', { name: 'Select Account 1' }));
    await userEvent.click(screen.getByRole('checkbox', { name: 'Select Account 2' }));
    const deactivate = within(await toolbar()).getByRole('button', { name: 'Deactivate' });
    await waitFor(() => expect(deactivate).not.toHaveAttribute('aria-disabled', 'true'));
    // One read of the states of the selection (ruling W8).
    expect(stateReads.at(-1)).toBe(`accountid eq ${rows(2)[0]!.accountid} or accountid eq ${rows(2)[1]!.accountid}`);
    expect(within(await toolbar()).getByRole('button', { name: 'Activate' })).toHaveAttribute('aria-disabled', 'true');
    const before = fetches.length;
    await userEvent.click(deactivate);
    const dialog = await screen.findByRole('dialog');
    await userEvent.click(within(dialog).getByRole('button', { name: 'Deactivate' }));
    expect(await screen.findByText('2 of 2 done')).toBeInTheDocument();
    expect(patches).toEqual(rows(2).map((r) => ({ path: `accounts(${r.accountid})`, body: { statecode: 1, statuscode: 2 } })));
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
    // The grid reloads.
    await waitFor(() => expect(fetches.length).toBeGreaterThan(before));
   }, 15_000);

  it('a failed record is listed by name with its message', async () => {
    const { patches } = openWithStates(() => ({ value: rows(2) }));
    server.use(
      http.patch(/\/api\/data\/v9\.2\/accounts\(f0000000-0000-0000-0000-000000000002\)/, () =>
        HttpResponse.json({ error: { code: '0x80040220', message: 'denied' } }, { status: 403 }),
      ),
    );
    await screen.findByText('Account 2');
    await userEvent.click(screen.getByRole('checkbox', { name: 'Select all records on this page' }));
    const deactivate = within(await toolbar()).getByRole('button', { name: 'Deactivate' });
    await waitFor(() => expect(deactivate).not.toHaveAttribute('aria-disabled', 'true'));
    await userEvent.click(deactivate);
    await userEvent.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Deactivate' }));
    expect(await screen.findByText('1 of 2 done')).toBeInTheDocument();
    expect(screen.getByText(/^Account 2: denied$/)).toBeInTheDocument();
    expect(patches).toHaveLength(1);
   }, 15_000);

  it('the selection clears on page change', async () => {
    openWithStates(() => ({ value: rows(3), '@Microsoft.Dynamics.CRM.morerecords': true }));
    await screen.findByText('Account 3');
    await userEvent.click(screen.getByRole('checkbox', { name: 'Select Account 1' }));
    expect(screen.getByRole('checkbox', { name: 'Select Account 1' })).toBeChecked();
    await userEvent.click(screen.getByRole('button', { name: 'Next page' }));
    await userEvent.click(await screen.findByRole('button', { name: 'Previous page' }));
    await waitFor(() => expect(screen.getByRole('checkbox', { name: 'Select Account 1' })).not.toBeChecked());
    expect(within(await toolbar()).getByRole('button', { name: 'Assign' })).toHaveAttribute('aria-disabled', 'true');
    // Three page loads: above the default 5 s on a busy runner.
  }, 15_000);
});
