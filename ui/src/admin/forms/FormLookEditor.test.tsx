import { act, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { useState } from 'react';
import { describe, expect, it } from 'vitest';
import { mockApps } from '../../apps/testSupport';
import { CLASS_ID } from '../../parsers/controlClass';
import { Providers } from '../../renderers/classic/form/formTestSupport';
import { en } from '../../shell/i18n/en';
import { server } from '../../test/server';
import { DesignerContext, type AfterSave, type DesignerHooks } from '../common/designerHooks';
import { rawAttr, rawEntity } from '../tables/tableTestSupport';
import { decodeContent, encodeContent } from '../views/overlayApi';
import { readForm, removeSection, type FormDesign } from './formDesign';
import { formRow } from './formDesignTestSupport';
import { FormLookEditor } from './FormLookEditor';

const ROOT = '/api/data/v9.2';
const DEF = 'dddddddd-0000-0000-0000-000000000001';
const FORM = '11111111-aaaa-1111-1111-111111111111';
const COPY = '99999999-0000-0000-0000-000000000000';
const ENV_WR = 'eeeeeeee-0000-0000-0000-000000000001';
const APP_WR = 'eeeeeeee-0000-0000-0000-000000000002';

const section = (name: string, label: string, control = '') =>
  `<section name="${name}" id="{b0000000-0000-0000-0000-0000000000${name.length}${label.length}}" showlabel="true" columns="1">
     <labels><label description="${label}" languagecode="1033" /></labels>
     <rows><row>${control}</row></rows>
   </section>`;
const tab = (name: string, label: string, sections: string) =>
  `<tab name="${name}" id="{a0000000-0000-0000-0000-0000000000${name.length}${label.length}}" showlabel="true" expanded="true">
     <labels><label description="${label}" languagecode="1033" /></labels>
     <columns><column width="100%"><sections>${sections}</sections></column></columns>
   </tab>`;
const TIMELINE = `<cell id="{c0000000-0000-0000-0000-000000000001}"><control id="notes" classid="${CLASS_ID.timeline}" /></cell>`;
const xml = (withTimeline: boolean) =>
  `<form><tabs>${tab('general', 'Summary', section('main', 'Main') + section('side', 'Side', withTimeline ? TIMELINE : ''))}${tab('more', 'More', section('extra', 'Extra'))}</tabs></form>`;
const designOf = (withTimeline = false) => readForm(formRow(xml(withTimeline)), 1033);
const FIELD = `<cell id="{c0000000-0000-0000-0000-000000000002}"><labels><label description="Account name" languagecode="1033" /></labels><control id="name" classid="${CLASS_ID.text}" datafieldname="name" /></cell>`;
const fieldDesign = () => readForm(formRow(`<form><tabs>${tab('general', 'Summary', section('main', 'Main', FIELD) + section('side', 'Side'))}</tabs></form>`), 1033);

interface Seen { method: string; path: string; body?: unknown }

/** The overlays in use (environment, then one app) and their web resources; records every write. `null`: no overlay. */
function install({
  environment = 'new_env.json' as string | null,
  app = 'new_app.json' as string | null,
  texts = {} as Record<string, string>,
  failPatch = false,
  entities = {} as Record<string, unknown>,
} = {}) {
  mockApps();
  const seen: Seen[] = [];
  const contents: Record<string, { id: string; text: string }> = {
    'new_env.json': {
      id: ENV_WR,
      text:
        texts['new_env.json'] ??
        JSON.stringify({ version: 1, forms: { [`{${FORM.toUpperCase()}}`]: { layout: 'wizard', grid: { general: { main: { col: 1, span: 6, row: 1 } } } } } }),
    },
    'new_app.json': { id: APP_WR, text: texts['new_app.json'] ?? JSON.stringify({ version: 1 }) },
  };
  server.use(
    http.all(`*${ROOT}/*`, async ({ request }) => {
      const url = new URL(request.url);
      const path = decodeURIComponent(url.pathname).slice(ROOT.length + 1);
      const text = request.method === 'GET' ? '' : await request.text();
      if (request.method !== 'GET') {
        seen.push({ method: request.method, path, body: text ? JSON.parse(text) : undefined });
        if (failPatch && request.method === 'PATCH') return HttpResponse.json({ error: { code: '0x1', message: 'No way' } }, { status: 403 });
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
      const table = /^EntityDefinitions\(LogicalName='([^']*)'\)$/.exec(path)?.[1];
      if (table !== undefined) {
        const entity = entities[table];
        return entity ? HttpResponse.json(entity) : HttpResponse.json({ error: { code: '0x2', message: 'No table' } }, { status: 404 });
      }
      return undefined;
    }),
  );
  return seen;
}

/** Renders the card with a design the test can replace (`setDesign`), as the page does when the user edits the structure. */
function renderCard(formId: string | null = FORM, design: FormDesign = designOf()) {
  const steps = new Map<string, AfterSave>();
  let dirty = 0;
  let setDesign: (d: FormDesign) => void = () => undefined;
  const hooks: DesignerHooks = {
    registerAfterSave: (key, run) => {
      steps.set(key, run);
      return () => steps.delete(key);
    },
    markDirty: () => {
      dirty += 1;
    },
  };
  function Host() {
    const [d, set] = useState(design);
    setDesign = set;
    return <FormLookEditor design={d} formId={formId ?? undefined} />;
  }
  render(
    <Providers>
      <DesignerContext.Provider value={hooks}>
        <Host />
      </DesignerContext.Provider>
    </Providers>,
  );
  return { step: () => steps.get('formLook'), dirty: () => dirty, setDesign: (d: FormDesign) => act(() => setDesign(d)) };
}

const select = (label: string) => screen.getByRole('combobox', { name: label });
const layoutSelect = () => screen.findByRole('combobox', { name: en['forms.look.layout'] });
/** The layout select exists as soon as the overlay is read, but the entry is applied to it one render later: wait for the value. */
const layoutIs = (value: string) => waitFor(() => expect(select(en['forms.look.layout'])).toHaveValue(value));
const group = (name: string) => screen.getByRole('group', { name });
const input = (name: string, label: string) => within(group(name)).getByRole('spinbutton', { name: label });
const written = (call: Seen) => JSON.parse(decodeContent((call.body as { content: string }).content)) as { forms: Record<string, unknown> };
const writes = (seen: Seen[]) => seen.map((s) => `${s.method} ${s.path}`);

describe('FormLookEditor', () => {
  it('preselects the environment overlay and shows the form entry of it', async () => {
    install();
    renderCard();
    await layoutSelect();
    await layoutIs('wizard');
    expect(within(select(en['views.modes.overlay'])).getAllByRole('option').map((o) => o.textContent)).toEqual([en['views.modes.environment'], 'Sales Hub']);
    expect(select(en['views.modes.overlay'])).toHaveValue('new_env.json');
    expect(within(group('Main')).getByRole('switch', { name: en['forms.look.grid.default'] })).not.toBeChecked();
    expect(input('Main', en['forms.look.grid.col'])).toHaveValue(1);
    expect(input('Main', en['forms.look.grid.span'])).toHaveValue(6);
    expect(within(group('Side')).getByRole('switch', { name: en['forms.look.grid.default'] })).toBeChecked();
    expect(within(group('Side')).queryByRole('spinbutton')).not.toBeInTheDocument();
  });

  it('shows the sections of the selected tab only', async () => {
    install();
    const user = userEvent.setup();
    renderCard();
    await layoutSelect();
    expect(screen.queryByRole('group', { name: 'Extra' })).not.toBeInTheDocument();
    await user.selectOptions(select(en['forms.look.grid.tab']), 'More');
    expect(group('Extra')).toBeInTheDocument();
    expect(screen.queryByRole('group', { name: 'Main' })).not.toBeInTheDocument();
  });

  it('the timeline fields show only when the form has a timeline', async () => {
    install();
    const card = renderCard();
    await layoutSelect();
    expect(screen.queryByRole('combobox', { name: en['forms.look.timeline.appearance'] })).not.toBeInTheDocument();
    expect(screen.queryByRole('combobox', { name: en['forms.look.timeline.density'] })).not.toBeInTheDocument();
    card.setDesign(designOf(true));
    expect(select(en['forms.look.timeline.appearance'])).toHaveValue('list');
    expect(select(en['forms.look.timeline.density'])).toHaveValue('comfortable');
  });

  it('switching to wizard and saving PATCHes the overlay, then publishes the web resource', async () => {
    const seen = install({ texts: { 'new_env.json': JSON.stringify({ version: 1 }) } });
    const user = userEvent.setup();
    const card = renderCard();
    await layoutSelect();
    await layoutIs('tabs');
    await user.selectOptions(select(en['forms.look.layout']), 'wizard');
    expect(card.dirty()).toBeGreaterThan(0);
    await act(() => card.step()!(FORM));
    expect(writes(seen)).toEqual([`PATCH webresourceset(${ENV_WR})`, 'POST PublishXml']);
    expect(written(seen[0]).forms[FORM]).toEqual({ layout: 'wizard' });
    // Saved: a second save writes nothing.
    await act(() => card.step()!(FORM));
    expect(seen).toHaveLength(2);
  });

  it('writes timeline and grid changes under the existing key, keeping what it does not model', async () => {
    const seen = install({
      texts: { 'new_env.json': JSON.stringify({ version: 1, forms: { [`{${FORM.toUpperCase()}}`]: { layout: 'wizard', future: 1, grid: { general: { main: { col: 1, span: 6, row: 1, note: 'x' } } } } } }) },
    });
    const user = userEvent.setup();
    const card = renderCard(FORM, designOf(true));
    await layoutSelect();
    await layoutIs('wizard');
    await user.selectOptions(select(en['forms.look.timeline.appearance']), 'cards');
    await user.selectOptions(select(en['forms.look.timeline.density']), 'compact');
    await user.clear(input('Main', en['forms.look.grid.span']));
    await user.type(input('Main', en['forms.look.grid.span']), '4');
    await act(() => card.step()!(FORM));
    expect(written(seen[0]).forms).toEqual({
      [`{${FORM.toUpperCase()}}`]: {
        layout: 'wizard',
        future: 1,
        grid: { general: { main: { col: 1, span: 4, row: 1, rowSpan: 1, note: 'x' } } },
        timeline: { appearance: 'cards', density: 'compact' },
      },
    });
  });

  it('a grid placement with col 7, span 8 is shown as invalid and not saved; the stored placement stays', async () => {
    // The default environment overlay stores main: col 1, span 6, row 1 (and layout wizard).
    const seen = install();
    const user = userEvent.setup();
    const card = renderCard();
    await layoutSelect();
    await layoutIs('wizard');
    await user.clear(input('Main', en['forms.look.grid.col']));
    await user.type(input('Main', en['forms.look.grid.col']), '7');
    await user.clear(input('Main', en['forms.look.grid.span']));
    await user.type(input('Main', en['forms.look.grid.span']), '8');
    expect(within(group('Main')).getByRole('alert')).toHaveTextContent(en['forms.look.grid.invalid']);
    // Nothing else changed: nothing is written.
    await act(() => card.step()!(FORM));
    expect(seen).toEqual([]);
    // With another change, the stored placement is written back as it was.
    await user.selectOptions(select(en['forms.look.layout']), 'tabs');
    await act(() => card.step()!(FORM));
    expect(writes(seen)).toEqual([`PATCH webresourceset(${ENV_WR})`, 'POST PublishXml']);
    expect(written(seen[0]).forms[`{${FORM.toUpperCase()}}`]).toEqual({ grid: { general: { main: { col: 1, span: 6, row: 1 } } } });
  });

  it('a new invalid placement is not written, and a valid one of another section is', async () => {
    const seen = install({ texts: { 'new_env.json': JSON.stringify({ version: 1 }) } });
    const user = userEvent.setup();
    const card = renderCard();
    await layoutSelect();
    await user.click(within(group('Main')).getByRole('switch', { name: en['forms.look.grid.default'] }));
    await user.clear(input('Main', en['forms.look.grid.col']));
    await user.type(input('Main', en['forms.look.grid.col']), '7');
    await user.clear(input('Main', en['forms.look.grid.span']));
    await user.type(input('Main', en['forms.look.grid.span']), '8');
    await user.click(within(group('Side')).getByRole('switch', { name: en['forms.look.grid.default'] }));
    await act(() => card.step()!(FORM));
    expect(written(seen[0]).forms[FORM]).toEqual({ grid: { general: { side: { col: 1, span: 1, row: 1, rowSpan: 1 } } } });
  });

  it('a save with no change in the card writes nothing, though the stored grid is in another order than the design', async () => {
    const stored = { version: 1, forms: { [FORM]: { grid: { more: { extra: { col: 1, row: 1 } }, general: { side: { col: 7, span: 6, row: 1 }, main: { col: 1, span: 6, row: 1 } } } } } };
    const seen = install({ texts: { 'new_env.json': JSON.stringify(stored) } });
    const card = renderCard();
    await layoutSelect();
    await act(() => card.step()!(FORM));
    expect(seen).toEqual([]);
  });

  it('a change keeps the stored order of tabs and sections, and appends new ones', async () => {
    const stored = { version: 1, forms: { [FORM]: { grid: { general: { side: { col: 7, span: 6, row: 1 } } } } } };
    const seen = install({ texts: { 'new_env.json': JSON.stringify(stored) } });
    const user = userEvent.setup();
    const card = renderCard();
    await layoutSelect();
    await user.click(within(group('Main')).getByRole('switch', { name: en['forms.look.grid.default'] }));
    await act(() => card.step()!(FORM));
    const grid = (written(seen[0]).forms[FORM] as { grid: { general: object } }).grid.general;
    expect(Object.keys(grid)).toEqual(['side', 'main']);
  });

  it('a placement of a section deleted in the designer is removed on save', async () => {
    const seen = install({
      texts: { 'new_env.json': JSON.stringify({ version: 1, forms: { [FORM]: { grid: { general: { main: { col: 1, span: 6, row: 1 }, side: { col: 7, span: 6, row: 1 } } } } } }) },
    });
    const design = designOf();
    const card = renderCard(FORM, design);
    await layoutSelect();
    const main = design.tabs[0].columns[0].sections.find((s) => s.name === 'main')!;
    card.setDesign(removeSection(design, main.key));
    await act(() => card.step()!(FORM));
    expect(writes(seen)).toEqual([`PATCH webresourceset(${ENV_WR})`, 'POST PublishXml']);
    expect(written(seen[0]).forms[FORM]).toEqual({ grid: { general: { side: { col: 7, span: 6, row: 1 } } } });
  });

  it('its after-save step writes nothing when the card did not change', async () => {
    const seen = install();
    const card = renderCard();
    await layoutSelect();
    await act(() => card.step()!(FORM));
    expect(seen).toEqual([]);
  });

  it('a new form gets its entry under the id the create returns', async () => {
    const seen = install({ texts: { 'new_env.json': JSON.stringify({ version: 1, views: { v: {} } }) } });
    const user = userEvent.setup();
    const card = renderCard(null);
    await layoutSelect();
    await user.selectOptions(select(en['forms.look.layout']), 'wizard');
    await act(() => card.step()!(COPY));
    const json = written(seen[0]) as unknown as { views: unknown; forms: Record<string, unknown> };
    expect(json.views).toEqual({ v: {} });
    expect(json.forms[COPY]).toEqual({ layout: 'wizard' });
  });

  it('Save as copies the entry to the new id', async () => {
    const seen = install();
    const card = renderCard();
    await layoutSelect();
    await layoutIs('wizard');
    await act(() => card.step()!(COPY));
    expect(writes(seen)).toEqual([`PATCH webresourceset(${ENV_WR})`, 'POST PublishXml']);
    const forms = written(seen[0]).forms;
    expect(forms[COPY]).toEqual({ layout: 'wizard', grid: { general: { main: { col: 1, span: 6, row: 1, rowSpan: 1 } } } });
    // The source keeps its entry.
    expect(forms[`{${FORM.toUpperCase()}}`]).toBeDefined();
  });

  it('another overlay shows its own entry, and unsaved changes are written to the chosen overlay', async () => {
    const seen = install();
    const user = userEvent.setup();
    const card = renderCard();
    await layoutSelect();
    await layoutIs('wizard');
    await user.selectOptions(select(en['forms.look.layout']), 'tabs');
    await user.selectOptions(select(en['views.modes.overlay']), 'new_app.json');
    await waitFor(() => expect(seen).toEqual([]));
    await waitFor(() => expect(select(en['forms.look.layout'])).toHaveValue('tabs'));
    await act(() => card.step()!(FORM));
    expect(writes(seen)).toEqual([`PATCH webresourceset(${APP_WR})`, 'POST PublishXml']);
  });

  it('with no overlay in use says so and links to the environment page, with no controls', async () => {
    install({ environment: null, app: null });
    renderCard();
    expect(await screen.findByText(en['forms.look.noneInUse'])).toBeInTheDocument();
    expect(screen.getByRole('link', { name: en['views.modes.environmentLink'] })).toHaveAttribute('href', '/manage/environment');
    expect(screen.queryByRole('combobox')).not.toBeInTheDocument();
  });

  it('an unreadable overlay makes the card read-only: the message and no controls', async () => {
    install({ texts: { 'new_env.json': '{"version":1,"forms":"oops"}' } });
    renderCard();
    expect(await screen.findByText(en['forms.look.unreadable'].replace('{reason}', en['forms.look.reason.forms']))).toBeInTheDocument();
    expect(screen.queryByRole('combobox', { name: en['forms.look.layout'] })).not.toBeInTheDocument();
    expect(screen.queryByRole('switch')).not.toBeInTheDocument();
  });

  it('an overlay without forms, and one without views, are readable', async () => {
    install({ texts: { 'new_env.json': JSON.stringify({ version: 1, views: {} }), 'new_app.json': JSON.stringify({ version: 1, forms: {} }) } });
    const user = userEvent.setup();
    renderCard();
    await layoutSelect();
    await layoutIs('tabs');
    await user.selectOptions(select(en['views.modes.overlay']), 'new_app.json');
    await waitFor(() => expect(select(en['forms.look.layout'])).toBeInTheDocument());
    expect(screen.queryByText(/cannot be read/)).not.toBeInTheDocument();
  });

  it('a failed overlay PATCH shows the error, marks the page changed and does not publish; the next save retries', async () => {
    const seen = install({ failPatch: true, texts: { 'new_env.json': JSON.stringify({ version: 1 }) } });
    const user = userEvent.setup();
    const card = renderCard();
    await layoutSelect();
    await user.selectOptions(select(en['forms.look.layout']), 'wizard');
    const before = card.dirty();
    await act(() => card.step()!(FORM));
    expect(await screen.findByText(en['forms.look.saveFailed'].replace('{detail}', '0x1: No way'))).toBeInTheDocument();
    expect(card.dirty()).toBeGreaterThan(before);
    expect(writes(seen)).toEqual([`PATCH webresourceset(${ENV_WR})`]);
    await act(() => card.step()!(FORM));
    expect(writes(seen)).toEqual([`PATCH webresourceset(${ENV_WR})`, `PATCH webresourceset(${ENV_WR})`]);
  });

  describe('background colours', () => {
    it('a section colour and a field colour picked in the card are written on save', async () => {
      const seen = install({ texts: { 'new_env.json': JSON.stringify({ version: 1 }) } });
      const user = userEvent.setup();
      const card = renderCard(FORM, fieldDesign());
      await layoutSelect();
      const sectionPicker = screen.getByRole('group', { name: `${en['forms.look.background.section']}: Main` });
      await user.click(within(sectionPicker).getByRole('radio', { name: en['forms.look.background.yellow'] }));
      const fieldPicker = screen.getByRole('group', { name: 'Account name' });
      await user.click(within(fieldPicker).getByRole('radio', { name: en['forms.look.background.green'] }));
      expect(card.dirty()).toBeGreaterThan(0);
      await act(() => card.step()!(FORM));
      expect(writes(seen)).toEqual([`PATCH webresourceset(${ENV_WR})`, 'POST PublishXml']);
      expect(written(seen[0]).forms[FORM]).toEqual({ background: { sections: { main: 'yellow' }, fields: { name: 'green' } } });
    });

    it('shows the stored colours, a free colour as a custom swatch, and No colour clears one', async () => {
      const seen = install({ texts: { 'new_env.json': JSON.stringify({ version: 1, forms: { [FORM]: { background: { sections: { main: '#a1b2c3' }, fields: { name: 'blue' } } } } }) } });
      const user = userEvent.setup();
      const card = renderCard(FORM, fieldDesign());
      await layoutSelect();
      const sectionPicker = screen.getByRole('group', { name: `${en['forms.look.background.section']}: Main` });
      expect(within(sectionPicker).getByRole('radio', { name: en['forms.look.background.custom'] })).toBeChecked();
      const fieldPicker = screen.getByRole('group', { name: 'Account name' });
      expect(within(fieldPicker).getByRole('radio', { name: en['forms.look.background.blue'] })).toBeChecked();
      await user.click(within(fieldPicker).getByRole('button', { name: `${en['forms.look.background.clear']}: Account name` }));
      await act(() => card.step()!(FORM));
      expect(written(seen[0]).forms[FORM]).toEqual({ background: { sections: { main: '#a1b2c3' } } });
    });

    it('the colour of a section deleted in the designer is removed on save', async () => {
      const seen = install({ texts: { 'new_env.json': JSON.stringify({ version: 1, forms: { [FORM]: { background: { sections: { main: 'yellow', side: 'red' } } } } }) } });
      const design = fieldDesign();
      const card = renderCard(FORM, design);
      await layoutSelect();
      const main = design.tabs[0].columns[0].sections.find((s) => s.name === 'main')!;
      card.setDesign(removeSection(design, main.key));
      await act(() => card.step()!(FORM));
      expect(written(seen[0]).forms[FORM]).toEqual({ background: { sections: { side: 'red' } } });
    });
  });

  describe('subgrids', () => {
    const SUB_VIEW = 'a1000000-0000-0000-0000-000000000001';
    const PCF_ID = 'b0000000-0000-0000-0000-0000000000f1';
    const CONTACT = rawEntity('contact', 'contacts', [
      rawAttr('fullname', 'String', { display: 'Full Name', IsPrimaryName: true }),
      rawAttr('statuscode', 'Status', { display: 'Status', OptionSet: { Options: [{ Value: 1, Label: { UserLocalizedLabel: { Label: 'Active' } } }] } }),
      rawAttr('createdon', 'DateTime', { display: 'Created On', DateTimeBehavior: { Value: 'UserLocal' } }),
    ]);
    const subgrid = (id: string, label: string, target: string, uniqueId = '') => `<cell id="{c0000000-0000-0000-0000-00000000${label.length}${id.length}a}">
        <labels><label description="${label}" languagecode="1033" /></labels>
        <control id="${id}" classid="${CLASS_ID.subgrid}" indicationOfSubgrid="true"${uniqueId ? ` uniqueid="{${uniqueId}}"` : ''}>
          <parameters>
            <ViewId>{${SUB_VIEW}}</ViewId>
            <RelationshipName>rel_${id}</RelationshipName>
            <TargetEntityType>${target}</TargetEntityType>
            <RecordsPerPage>4</RecordsPerPage>
          </parameters>
        </control>
      </cell>`;
    const NAVIGATION = `<Navigation><NavBar><NavBarByRelationshipItem RelationshipName="contact_customer_accounts" Id="nav_contacts" Sequence="10" Area="Details">
        <Titles><Title LCID="1033" Text="Related people" /></Titles></NavBarByRelationshipItem></NavBar></Navigation>`;
    const PCF = `<controlDescriptions><controlDescription forControl="{${PCF_ID}}">
        <customControl formFactor="0" name="new_Ctl.Gallery"><parameters /></customControl>
      </controlDescription></controlDescriptions>`;
    const subgridDesign = (cells: string, tail = '') =>
      readForm(formRow(`<form><tabs>${tab('general', 'Summary', section('main', 'Main', cells) + section('side', 'Side'))}</tabs>${tail}</form>`), 1033);
    const contacts = () => subgridDesign(subgrid('Contacts', 'Contacts', 'contact'));
    const boardOf = (name: string) => within(group(name)).findByRole('combobox', { name: en['views.modes.board'] });

    it('a form without subgrid says so', async () => {
      install();
      renderCard();
      await layoutSelect();
      expect(screen.getByText(en['forms.look.subgridsNone'])).toBeInTheDocument();
    });

    it('lists a subgrid with its label and table; a board picked is written under its lowercase control id', async () => {
      const seen = install({ texts: { 'new_env.json': JSON.stringify({ version: 1 }) }, entities: { contact: CONTACT } });
      const user = userEvent.setup();
      const card = renderCard(FORM, contacts());
      await layoutSelect();
      expect(screen.getByText(en['forms.look.subgrids'])).toBeInTheDocument();
      expect(within(group('Contacts')).getByText('contact')).toBeInTheDocument();
      await user.selectOptions(await boardOf('Contacts'), 'statuscode');
      expect(card.dirty()).toBeGreaterThan(0);
      await act(() => card.step()!(FORM));
      expect(writes(seen)).toEqual([`PATCH webresourceset(${ENV_WR})`, 'POST PublishXml']);
      expect(written(seen[0]).forms[FORM]).toEqual({ subgrids: { contacts: { kanban: { groupBy: 'statuscode' } } } });
    });

    it("shows the stored entry of a subgrid, and Use the view's modes copies the view's entry (S5)", async () => {
      const seen = install({
        texts: { 'new_env.json': JSON.stringify({ version: 1, views: { [SUB_VIEW]: { calendar: { start: 'createdon' }, default: 'calendar' } }, forms: { [FORM]: { subgrids: { CONTACTS: { kanban: { groupBy: 'statuscode' } } } } } }) },
        entities: { contact: CONTACT },
      });
      const user = userEvent.setup();
      const card = renderCard(FORM, contacts());
      await layoutSelect();
      expect(await boardOf('Contacts')).toHaveValue('statuscode');
      await user.click(within(group('Contacts')).getByRole('button', { name: en['forms.look.useViewModes'] }));
      expect(within(group('Contacts')).getByRole('combobox', { name: en['views.modes.calendarStart'] })).toHaveValue('createdon');
      expect(within(group('Contacts')).getByRole('combobox', { name: en['views.modes.board'] })).toHaveValue('');
      await act(() => card.step()!(FORM));
      expect(written(seen[0]).forms[FORM]).toEqual({ subgrids: { CONTACTS: { calendar: { start: 'createdon' }, default: 'calendar' } } });
    });

    it("without an entry of the subgrid's view there is no Use the view's modes", async () => {
      install({ texts: { 'new_env.json': JSON.stringify({ version: 1, views: { other: { cards: {} } } }) }, entities: { contact: CONTACT } });
      renderCard(FORM, contacts());
      await layoutSelect();
      await boardOf('Contacts');
      expect(screen.queryByRole('button', { name: en['forms.look.useViewModes'] })).not.toBeInTheDocument();
    });

    it('the entry of a subgrid the form no longer has is dropped on save; the background stays (S6)', async () => {
      const stored = { version: 1, forms: { [FORM]: { background: { sections: { main: 'yellow' } }, subgrids: { gone: { kanban: { groupBy: 'statuscode' } }, contacts: { cards: {} } } } } };
      const seen = install({ texts: { 'new_env.json': JSON.stringify(stored) }, entities: { contact: CONTACT } });
      const card = renderCard(FORM, contacts());
      await layoutSelect();
      await act(() => card.step()!(FORM));
      expect(writes(seen)).toEqual([`PATCH webresourceset(${ENV_WR})`, 'POST PublishXml']);
      expect(written(seen[0]).forms[FORM]).toEqual({ background: { sections: { main: 'yellow' } }, subgrids: { contacts: { cards: {} } } });
    });

    it('a subgrid replaced by a custom control says so and has no fields; related items are not listed', async () => {
      install({ entities: { contact: CONTACT } });
      renderCard(FORM, subgridDesign(subgrid('Gallery', 'Gallery', 'contact', PCF_ID), NAVIGATION + PCF));
      await layoutSelect();
      expect(within(group('Gallery')).getByText(en['forms.look.subgridPcf'])).toBeInTheDocument();
      expect(within(group('Gallery')).queryByRole('combobox')).not.toBeInTheDocument();
      expect(screen.queryByRole('group', { name: 'Related people' })).not.toBeInTheDocument();
      expect(screen.queryByText('Related people')).not.toBeInTheDocument();
    });

    it('the entry of a subgrid a custom control replaces is dropped on save (S6)', async () => {
      const stored = { version: 1, forms: { [FORM]: { subgrids: { gallery: { kanban: { groupBy: 'statuscode' } }, contacts: { cards: {} } } } } };
      const seen = install({ texts: { 'new_env.json': JSON.stringify(stored) }, entities: { contact: CONTACT } });
      const card = renderCard(FORM, subgridDesign(subgrid('Gallery', 'Gallery', 'contact', PCF_ID) + subgrid('Contacts', 'Contacts', 'contact'), PCF));
      await layoutSelect();
      await act(() => card.step()!(FORM));
      expect(writes(seen)).toEqual([`PATCH webresourceset(${ENV_WR})`, 'POST PublishXml']);
      expect(written(seen[0]).forms[FORM]).toEqual({ subgrids: { contacts: { cards: {} } } });
    });

    it('a table whose columns cannot be read shows the error for its subgrid only', async () => {
      install({ entities: { contact: CONTACT } });
      renderCard(FORM, subgridDesign(subgrid('Contacts', 'Contacts', 'contact') + subgrid('Orders', 'Orders', 'salesorder')));
      await layoutSelect();
      expect(await within(group('Orders')).findByText(en['forms.look.subgridTableFailed'].replace('{table}', 'salesorder').replace('{detail}', '0x2: No table'))).toBeInTheDocument();
      expect(within(group('Orders')).queryByRole('combobox')).not.toBeInTheDocument();
      expect(await boardOf('Contacts')).toBeInTheDocument();
    });
  });
});
