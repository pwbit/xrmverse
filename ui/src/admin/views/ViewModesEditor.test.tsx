import { act, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { describe, expect, it } from 'vitest';
import { mockApps } from '../../apps/testSupport';
import { Providers } from '../../renderers/classic/form/formTestSupport';
import { en } from '../../shell/i18n/en';
import { server } from '../../test/server';
import { attr } from '../tables/testAttrs';
import { DesignerContext, type AfterSave, type DesignerHooks } from './designerSlots';
import { decodeContent, encodeContent } from './overlayApi';
import { ViewModesEditor } from './ViewModesEditor';

const ROOT = '/api/data/v9.2';
const DEF = 'dddddddd-0000-0000-0000-000000000001';
const VIEW = '11111111-aaaa-1111-1111-111111111111';
const ENV_WR = 'eeeeeeee-0000-0000-0000-000000000001';
const APP_WR = 'eeeeeeee-0000-0000-0000-000000000002';

const ATTRIBUTES = [
  attr('accountid', { kind: 'unsupported', typeName: 'Uniqueidentifier', isPrimaryId: true }),
  attr('name', { displayName: 'Account Name', isPrimaryName: true }),
  attr('statuscode', { kind: 'choice', typeName: 'Status', displayName: 'Status Reason', options: [{ value: 1, label: 'Active' }] }),
  attr('statecode', { kind: 'choice', typeName: 'State', displayName: 'Status', options: [{ value: 0, label: 'Active' }] }),
  attr('industrycode', { kind: 'choice', typeName: 'Picklist', displayName: 'Industry', options: [{ value: 1, label: 'Retail' }] }),
  attr('donotemail', { kind: 'bool', typeName: 'Boolean', displayName: 'Do not Email' }),
  attr('xv_tags', { kind: 'choices', typeName: 'MultiSelectPicklist', displayName: 'Tags', options: [{ value: 1, label: 'A' }] }),
  attr('revenue', { kind: 'money', typeName: 'Money', displayName: 'Annual Revenue' }),
  attr('createdon', { kind: 'datetime', typeName: 'DateTime', displayName: 'Created On', behavior: 'UserLocal' }),
  attr('modifiedon', { kind: 'datetime', typeName: 'DateTime', displayName: 'Modified On', behavior: 'UserLocal' }),
  attr('xv_start', { kind: 'datetime', typeName: 'DateTime', displayName: 'Start Day', behavior: 'DateOnly', dateOnly: true }),
  attr('xv_end', { kind: 'datetime', typeName: 'DateTime', displayName: 'End Day', behavior: 'DateOnly', dateOnly: true }),
  attr('xv_due', { kind: 'datetime', typeName: 'DateTime', displayName: 'Due', behavior: 'TimeZoneIndependent' }),
];

interface Seen { method: string; path: string; body?: unknown }

/** The overlays in use (environment, then one app) and their web resources; records every write. */
/** `null` for no environment or app overlay. */
function install({ environment = 'new_env.json' as string | null, app = 'new_app.json' as string | null, texts = {} as Record<string, string> } = {}) {
  mockApps();
  const seen: Seen[] = [];
  const contents: Record<string, { id: string; text: string }> = {
    'new_env.json': { id: ENV_WR, text: texts['new_env.json'] ?? JSON.stringify({ version: 1, views: { [`{${VIEW.toUpperCase()}}`]: { kanban: { groupBy: 'statuscode' }, default: 'kanban' } } }) },
    'new_app.json': { id: APP_WR, text: texts['new_app.json'] ?? JSON.stringify({ version: 1 }) },
  };
  server.use(
    http.all(`*${ROOT}/*`, async ({ request }) => {
      const url = new URL(request.url);
      const path = decodeURIComponent(url.pathname).slice(ROOT.length + 1);
      const text = request.method === 'GET' ? '' : await request.text();
      if (request.method !== 'GET') {
        seen.push({ method: request.method, path, body: text ? JSON.parse(text) : undefined });
        return new HttpResponse(null, { status: 204 });
      }
      if (path === 'settingdefinitions') return HttpResponse.json({ value: [{ settingdefinitionid: DEF, uniquename: 'xrmverse_Presentation', defaultvalue: null }] });
      if (path === 'organizationsettings') return HttpResponse.json({ value: environment ? [{ value: environment }] : [] });
      if (path === 'appsettings') return HttpResponse.json({ value: app ? [{ _parentappmoduleid_value: 'app1', value: app }] : [] });
      if (path === 'appmodules') return HttpResponse.json({ value: [{ appmoduleid: 'app1', name: 'Sales Hub' }] });
      if (path === 'webresourceset') {
        const name = /name eq '([^']*)'/.exec(url.searchParams.get('$filter') ?? '')?.[1] ?? '';
        const found = contents[name];
        return HttpResponse.json({ value: found ? [{ webresourceid: found.id, name, content: encodeContent(found.text) }] : [] });
      }
      return undefined;
    }),
  );
  return seen;
}

/** `null`: a new view (no id yet). */
function renderCard(viewId: string | null = VIEW, columns?: { name: string; header: string }[]) {
  const steps = new Map<string, AfterSave>();
  let dirty = 0;
  const hooks: DesignerHooks = {
    registerAfterSave: (key, run) => {
      steps.set(key, run);
      return () => steps.delete(key);
    },
    markDirty: () => {
      dirty += 1;
    },
  };
  render(
    <Providers>
      <DesignerContext.Provider value={hooks}>
        <ViewModesEditor attributes={ATTRIBUTES} viewId={viewId ?? undefined} columns={columns} />
      </DesignerContext.Provider>
    </Providers>,
  );
  return { step: () => steps.get('viewModes'), dirty: () => dirty };
}

const select = (label: string) => screen.getByRole('combobox', { name: label });
const optionsOf = (label: string) => within(select(label)).getAllByRole('option').map((o) => o.textContent);
const boardSelect = () => screen.findByRole('combobox', { name: en['views.modes.board'] });

describe('ViewModesEditor', () => {
  it('preselects the environment overlay and shows the view entry of it', async () => {
    install();
    renderCard();
    await boardSelect();
    // The board column comes with the overlay, which can load after the select shows.
    await waitFor(() => expect(select(en['views.modes.board'])).toHaveValue('statuscode'));
    expect(optionsOf(en['views.modes.overlay'])).toEqual([en['views.modes.environment'], 'Sales Hub']);
    expect(select(en['views.modes.overlay'])).toHaveValue('new_env.json');
    expect(select(en['views.modes.default'])).toHaveValue('kanban');
  });

  it('the board offers only choice, status, state and two-option columns', async () => {
    install();
    renderCard();
    await boardSelect();
    expect(optionsOf(en['views.modes.board'])).toEqual([en['views.modes.off'], 'Do not Email', 'Industry', 'Status', 'Status Reason']);
  });

  it('the calendar offers only date columns, and the end only those with the start behaviour', async () => {
    install();
    const user = userEvent.setup();
    renderCard();
    await boardSelect();
    expect(optionsOf(en['views.modes.calendarStart'])).toEqual([en['views.modes.off'], 'Created On', 'Due', 'End Day', 'Modified On', 'Start Day']);
    expect(screen.queryByRole('combobox', { name: en['views.modes.calendarEnd'] })).not.toBeInTheDocument();
    await user.selectOptions(select(en['views.modes.calendarStart']), 'xv_start');
    expect(optionsOf(en['views.modes.calendarEnd'])).toEqual([en['views.modes.none'], 'End Day']);
    await user.selectOptions(select(en['views.modes.calendarEnd']), 'xv_end');
    // A start of another behaviour drops the end that no longer matches.
    await user.selectOptions(select(en['views.modes.calendarStart']), 'createdon');
    expect(optionsOf(en['views.modes.calendarEnd'])).toEqual([en['views.modes.none'], 'Modified On']);
    expect(select(en['views.modes.calendarEnd'])).toHaveValue('');
  });

  it('the default mode lists grid and the switched-on modes only; switching a mode off resets a default on it', async () => {
    install();
    const user = userEvent.setup();
    const card = renderCard();
    await boardSelect();
    expect(optionsOf(en['views.modes.default'])).toEqual([en['views.modes.grid'], en['views.modes.kanban']]);
    await user.click(screen.getByRole('switch', { name: en['views.modes.cards'] }));
    expect(optionsOf(en['views.modes.default'])).toEqual([en['views.modes.grid'], en['views.modes.kanban'], en['views.modes.cards']]);
    await user.selectOptions(select(en['views.modes.board']), '');
    expect(optionsOf(en['views.modes.default'])).toEqual([en['views.modes.grid'], en['views.modes.cards']]);
    expect(select(en['views.modes.default'])).toHaveValue('grid');
    expect(card.dirty()).toBeGreaterThan(0);
  });

  it('another overlay shows its own entry for the view', async () => {
    install();
    const user = userEvent.setup();
    renderCard();
    await boardSelect();
    await user.selectOptions(select(en['views.modes.overlay']), 'new_app.json');
    await waitFor(() => expect(select(en['views.modes.board'])).toHaveValue(''));
  });

  it('with no overlay in use says so and links to the environment page, with no controls', async () => {
    install({ environment: null, app: null });
    renderCard();
    expect(await screen.findByText(en['views.modes.noneInUse'])).toBeInTheDocument();
    expect(screen.getByRole('link', { name: en['views.modes.environmentLink'] })).toHaveAttribute('href', '/manage/environment');
    expect(screen.queryByRole('combobox')).not.toBeInTheDocument();
  });

  it('an unreadable overlay shows the message and no controls', async () => {
    install({ texts: { 'new_env.json': '{"version":1,"views":"oops"}' } });
    renderCard();
    expect(
      await screen.findByText(en['views.modes.unreadable'].replace('{reason}', en['views.modes.reason.views'])),
    ).toBeInTheDocument();
    expect(screen.queryByRole('combobox', { name: en['views.modes.board'] })).not.toBeInTheDocument();
    expect(screen.queryByRole('switch')).not.toBeInTheDocument();
  });

  it.each<[string, () => Promise<Response> | Response]>([
    ['still loading', () => new Promise<Response>(() => undefined)],
    ['failed', () => HttpResponse.json({ error: { code: '0x1', message: 'List failed' } }, { status: 500 }) as Response],
  ])('a Save as while the overlay list is %s says the view modes were not copied', async (_, answer) => {
    const seen = install();
    server.use(http.get(`*${ROOT}/settingdefinitions`, () => answer()));
    const card = renderCard();
    await waitFor(() => expect(card.step()).toBeDefined());
    await act(() => card.step()!('99999999-0000-0000-0000-000000000000'));
    expect(await screen.findByText(en['views.modes.notCopied'])).toBeInTheDocument();
    expect(seen).toEqual([]);
  });

  it('the calendar layout shows with a calendar, keeps through start and end changes, and month is written as absent', async () => {
    const seen = install();
    const user = userEvent.setup();
    const card = renderCard();
    await boardSelect();
    expect(screen.queryByRole('combobox', { name: en['views.modes.calendarLayout'] })).not.toBeInTheDocument();
    await user.selectOptions(select(en['views.modes.calendarStart']), 'xv_start');
    expect(optionsOf(en['views.modes.calendarLayout'])).toEqual([en['views.modes.layout.month'], en['views.modes.layout.timeline']]);
    expect(select(en['views.modes.calendarLayout'])).toHaveValue('month');
    await user.selectOptions(select(en['views.modes.calendarLayout']), 'timeline');
    await user.selectOptions(select(en['views.modes.calendarEnd']), 'xv_end');
    await user.selectOptions(select(en['views.modes.calendarStart']), 'xv_start');
    expect(select(en['views.modes.calendarLayout'])).toHaveValue('timeline');
    await act(() => card.step()!(VIEW));
    // The environment overlay keeps the view under its own spelling of the id: the entry is its only one.
    const written = () => Object.values((JSON.parse(decodeContent((seen[seen.length - 2].body as { content: string }).content)) as { views: Record<string, { calendar?: unknown }> }).views)[0]!;
    expect(written().calendar).toEqual({ start: 'xv_start', end: 'xv_end', layout: 'timeline' });
    await user.selectOptions(select(en['views.modes.calendarLayout']), 'month');
    await act(() => card.step()!(VIEW));
    expect(written().calendar).toEqual({ start: 'xv_start', end: 'xv_end' });
  });

  it('its after-save step writes nothing when the card did not change', async () => {
    const seen = install();
    const card = renderCard();
    await boardSelect();
    await act(() => card.step()!(VIEW));
    expect(seen).toEqual([]);
  });

  it('its after-save step re-reads the overlay and writes the entry under the saved id', async () => {
    const seen = install();
    const user = userEvent.setup();
    const card = renderCard(null);
    await boardSelect();
    await user.click(screen.getByRole('switch', { name: en['views.modes.cards'] }));
    await act(() => card.step()!('99999999-0000-0000-0000-000000000000'));
    expect(seen.map((s) => `${s.method} ${s.path}`)).toEqual([`PATCH webresourceset(${ENV_WR})`, 'POST PublishXml']);
    const json = JSON.parse(decodeContent((seen[0].body as { content: string }).content)) as { views: Record<string, unknown> };
    expect(json.views['99999999-0000-0000-0000-000000000000']).toEqual({ cards: {} });
    // Saved: a second save writes nothing.
    await act(() => card.step()!('99999999-0000-0000-0000-000000000000'));
    expect(seen).toHaveLength(2);
  });
  it('switching the overlay keeps unsaved mode changes and writes them to the chosen overlay', async () => {
    const seen = install();
    const user = userEvent.setup();
    const card = renderCard();
    await boardSelect();
    await user.click(screen.getByRole('switch', { name: en['views.modes.cards'] }));
    await user.selectOptions(select(en['views.modes.overlay']), 'new_app.json');
    await waitFor(() => expect(seen).toEqual([]));
    await boardSelect();
    // The board column comes with the overlay, which can load after the select shows.
    await waitFor(() => expect(select(en['views.modes.board'])).toHaveValue('statuscode'));
    expect(screen.getByRole('switch', { name: en['views.modes.cards'] })).toBeChecked();
    await act(() => card.step()!(VIEW));
    expect(seen.map((s) => `${s.method} ${s.path}`)).toEqual([`PATCH webresourceset(${APP_WR})`, 'POST PublishXml']);
    const json = JSON.parse(decodeContent((seen[0].body as { content: string }).content)) as { views: Record<string, unknown> };
    expect(json.views[VIEW]).toEqual({ kanban: { groupBy: 'statuscode' }, cards: {}, default: 'kanban' });
  });

  it('a column switched to wrap is written in the view entry, in the view order', async () => {
    const seen = install();
    const user = userEvent.setup();
    const columns = [
      { name: 'name', header: 'Account Name' },
      { name: 'pc.fullname', header: 'Full Name (Primary Contact)' },
    ];
    const card = renderCard(VIEW, columns);
    await boardSelect();
    const fieldset = screen.getByRole('group', { name: en['views.modes.wrap'] });
    expect(within(fieldset).getAllByRole('switch').map((s) => (s as HTMLInputElement).checked)).toEqual([false, false]);
    await user.click(within(fieldset).getByRole('switch', { name: 'Full Name (Primary Contact)' }));
    await user.click(within(fieldset).getByRole('switch', { name: 'Account Name' }));
    await act(() => card.step()!(VIEW));
    const json = JSON.parse(decodeContent((seen[0].body as { content: string }).content)) as { views: Record<string, { wrap?: string[] }> };
    expect(json.views[`{${VIEW.toUpperCase()}}`].wrap).toEqual(['name', 'pc.fullname']);
  });
});

