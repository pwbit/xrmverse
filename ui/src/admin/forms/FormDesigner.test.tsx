import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { server } from '../../test/server';
import { App } from '../../shell/App';
import { en } from '../../shell/i18n/en';
import { parseXml } from '../../parsers/xml';
import { CLASS_ID } from '../../parsers/controlClass';
import { API, onPath, signInAsAdministrator } from '../security/testSupport';
import { installMetadata, rawAttr, rawEntity } from '../tables/tableTestSupport';
import { encodeContent } from '../views/overlayApi';
import { PAGE_TEST_TIMEOUT } from './controlsTestSupport';
import { newFormType } from './FormDesigner';

const ID = '11111111-1111-1111-1111-111111111111';
const OTHER = '22222222-2222-2222-2222-222222222222';
const NEW_ID = '99999999-9999-9999-9999-999999999999';
const ROOT = '/api/data/v9.2';
const TEXT = '{4273EDBD-AC1D-40d3-9FB2-095C621B552D}';

const account = rawEntity(
  'account',
  'accounts',
  [
    rawAttr('accountid', 'Uniqueidentifier', { IsPrimaryId: true }),
    rawAttr('name', 'String', { IsPrimaryName: true, display: 'Account Name', RequiredLevel: { Value: 'ApplicationRequired' } }),
    rawAttr('telephone1', 'String', { display: 'Main Phone' }),
    rawAttr('revenue', 'Money', { display: 'Annual Revenue' }),
    rawAttr('primarycontactid', 'Lookup', { display: 'Primary Contact', Targets: ['contact'] }),
    rawAttr('primarycontactidname', 'String', { display: 'Primary Contact Name', AttributeOf: 'primarycontactid' }),
    rawAttr('createdon', 'DateTime', { display: 'Created On' }),
    rawAttr('entityimage', 'Image', { display: 'Entity Image' }),
    rawAttr('secretcol', 'String', { display: 'Secret Column', IsValidForRead: false }),
  ],
  { PrimaryIdAttribute: 'accountid', PrimaryNameAttribute: 'name', display: 'Account' },
);

const cell = (id: string, label: string, control: string) =>
  `<cell id="{${id}}"><labels><label description="${label}" languagecode="1033" /></labels>${control}</cell>`;
const field = (logical: string, prefix = '') => `<control id="${prefix}${logical}" classid="${TEXT}" datafieldname="${logical}" disabled="false" />`;

const FORM = `<form>
  <tabs>
    <tab name="general" id="{a0000000-0000-0000-0000-000000000001}" showlabel="true" expanded="true">
      <labels><label description="Summary" languagecode="1033" /></labels>
      <columns>
        <column width="50%">
          <sections>
            <section name="main" id="{a0000000-0000-0000-0000-000000000002}" showlabel="true" columns="1">
              <labels><label description="Main" languagecode="1033" /></labels>
              <rows>
                <row>${cell('c0000000-0000-0000-0000-000000000001', 'Account Name', field('name'))}</row>
                <row>${cell('c0000000-0000-0000-0000-000000000002', 'Main Phone', field('telephone1'))}</row>
              </rows>
            </section>
          </sections>
        </column>
        <column width="50%">
          <sections>
            <section name="side" id="{a0000000-0000-0000-0000-000000000003}" showlabel="true" columns="1">
              <labels><label description="Side" languagecode="1033" /></labels>
              <rows>
                <row>${cell('c0000000-0000-0000-0000-000000000003', 'Contacts', '<control id="contacts" classid="{E7A81278-8635-4D9E-8D4D-59480B391C5B}" />')}</row>
              </rows>
            </section>
          </sections>
        </column>
      </columns>
    </tab>
  </tabs>
  <header id="{a0000000-0000-0000-0000-000000000004}" columns="1111" celllabelposition="Top">
    <rows>
      <row>${cell('c0000000-0000-0000-0000-000000000004', 'Primary Contact', field('primarycontactid', 'header_'))}</row>
    </rows>
  </header>
  <events>
    <event name="onchange" application="false" active="false" attribute="telephone1">
      <Handlers>
        <Handler functionName="A.one" libraryName="new_a.js" handlerUniqueId="{d0000000-0000-0000-0000-000000000001}" enabled="true" />
        <Handler functionName="A.two" libraryName="new_a.js" handlerUniqueId="{d0000000-0000-0000-0000-000000000002}" enabled="true" />
      </Handlers>
    </event>
  </events>
</form>`;

const record = (over: Record<string, unknown> = {}) => ({
  systemformid: ID,
  name: 'Information',
  description: 'The main form',
  type: 2,
  isdefault: true,
  formactivationstate: 1,
  ismanaged: false,
  objecttypecode: 'account',
  formxml: FORM,
  ...over,
});

interface Call { method: string; path: string; body?: Record<string, unknown> }
let calls: Call[];
let rows: Record<string, ReturnType<typeof record>>;
let reads: Record<string, number>;

const pathOf = (request: Request) => decodeURIComponent(new URL(request.url).pathname).slice(ROOT.length + 1);

function install() {
  server.use(
    onPath('get', `systemforms(${ID})`, () => {
      reads[ID] = (reads[ID] ?? 0) + 1;
      return HttpResponse.json(rows[ID]);
    }),
    onPath('get', `systemforms(${OTHER})`, () => {
      reads[OTHER] = (reads[OTHER] ?? 0) + 1;
      return HttpResponse.json(rows[OTHER]);
    }),
    onPath('get', `systemforms(${NEW_ID})`, () => {
      reads[NEW_ID] = (reads[NEW_ID] ?? 0) + 1;
      return HttpResponse.json(rows[NEW_ID] ?? {});
    }),
    http.post(`${API}/systemforms`, async ({ request }) => {
      calls.push({ method: 'POST', path: 'systemforms', body: (await request.json()) as Record<string, unknown> });
      return new HttpResponse(null, { status: 204, headers: { 'OData-EntityId': `http://x${ROOT}/systemforms(${NEW_ID})` } });
    }),
    http.patch(`${API}/*`, async ({ request }) => {
      const path = pathOf(request);
      const body = (await request.json()) as Record<string, unknown>;
      calls.push({ method: 'PATCH', path, body });
      const id = /^systemforms\((.*)\)$/.exec(path)?.[1];
      if (id && rows[id]) rows[id] = { ...rows[id], ...body } as ReturnType<typeof record>;
      return new HttpResponse(null, { status: 204 });
    }),
    http.post(`${API}/PublishXml`, async ({ request }) => {
      calls.push({ method: 'POST', path: 'PublishXml', body: (await request.json()) as Record<string, unknown> });
      return new HttpResponse(null, { status: 204 });
    }),
    // The JS libraries are read only when the library picker is used (G6).
    onPath('get', 'webresourceset', () => {
      calls.push({ method: 'GET', path: 'webresourceset' });
      return HttpResponse.json({ value: [] });
    }),
     // The custom controls a selected field can use (G5): none installed.
    onPath('get', 'customcontrols', () => HttpResponse.json({ value: [] })),
  );
}

beforeEach(() => {
  signInAsAdministrator();
  calls = [];
  reads = {};
  rows = { [ID]: record(), [OTHER]: record({ systemformid: OTHER, name: 'Other form', isdefault: false }) };
  installMetadata([account], []);
  install();
});
afterEach(() => sessionStorage.clear());

let router: { state: { location: { pathname: string } }; navigate(to: string | number): Promise<void> };

function open(id = ID, search = '') {
  render(
    <App
      initialEntries={[`/manage/tables/account/forms/${id}${search}`]}
      onRouter={(r) => {
        router = r as unknown as typeof router;
      }}
    />,
  );
}

const nameInput = () => screen.findByRole('textbox', { name: en['forms.designer.name'] });
// The toolbar buttons by their text: a `getByRole` over the whole designer costs about 250 ms locally (more on CI), and
// the Save tests poll these inside `waitFor`.
const toolbarButton = (label: string) => () => screen.getByText(label, { selector: 'button' });
const saveButton = toolbarButton(en['forms.designer.save']);
const saveAsButton = toolbarButton(en['forms.designer.saveAs']);
const backButton = toolbarButton(en['forms.designer.back']);
const structure = () => screen.getByRole('region', { name: en['forms.canvas.title'] });
// Race seen on CI only: a new form can show its name field a render before the canvas region is found, so wait for it.
const canvasReady = () => screen.findByRole('region', { name: en['forms.canvas.title'] });
const headerCard = () => screen.getByRole('region', { name: en['forms.header.title'] });
const propsCard = () => screen.getByRole('region', { name: en['forms.props.title'] });
const section = (label: string) => within(structure()).getByRole('region', { name: `Section ${label}` });
const stored = (method: string) => calls.filter((c) => c.method === method && c.path.startsWith('systemforms'));
const publishes = () => calls.filter((c) => c.path === 'PublishXml');
const xmlOf = (call: Call) => parseXml(String(call.body?.formxml)).documentElement;
const names = (form: Element, tag: string) => Array.from(form.getElementsByTagName(tag)).map((e) => e.getAttribute('name'));
const control = (form: Element, id: string) => Array.from(form.getElementsByTagName('control')).find((c) => c.getAttribute('id') === id);
const loadPrefix = en['forms.designer.loadFailed'].replace('{detail}', '');
const loadFailed = (text: string) => text.startsWith(loadPrefix) && text.length > loadPrefix.length;

async function pick(user: ReturnType<typeof userEvent.setup>, label: string) {
  const dialog = await screen.findByRole('dialog');
  await user.click(within(dialog).getByRole('button', { name: label }));
  await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
}

/** For the tests that render the App twice (two forms, or the table page and a form): under a full run they need more than 5 s. */
const TWO_PAGES = 15_000;

describe('FormDesigner', () => {
  it('opening a form sends no webresourceset request', async () => {
    open();
    await canvasReady();
    expect(await screen.findByRole('region', { name: en['forms.events.title'] })).toBeInTheDocument();
    expect(calls.filter((c) => c.path === 'webresourceset')).toEqual([]);
    await userEvent.setup().click(screen.getByRole('combobox', { name: en['forms.events.library.pick'] }));
    await waitFor(() => expect(calls.filter((c) => c.path === 'webresourceset')).toHaveLength(1));
  });

  it('a new form shows tab General, section General and the primary name', async () => {
    open('new');
    expect(await nameInput()).toHaveValue('');
    await canvasReady();
    expect(within(structure()).getByRole('tab', { name: 'General' })).toBeInTheDocument();
    expect(within(section('General')).getByRole('button', { name: 'Account Name' })).toBeInTheDocument();
    expect(saveButton()).toBeDisabled();
    expect(screen.queryByRole('button', { name: en['forms.designer.saveAs'] })).not.toBeInTheDocument();
    expect(within(headerCard()).getByText(en['forms.header.empty'])).toBeInTheDocument();
  });

  describe('a new form of the address type', { timeout: PAGE_TEST_TIMEOUT }, () => {
    const noMainCards = () => {
      expect(screen.queryByRole('region', { name: en['forms.header.title'] })).not.toBeInTheDocument();
      expect(screen.queryByRole('region', { name: en['forms.look.title'] })).not.toBeInTheDocument();
    };

    it('new?type=6 opens a new quick view form', async () => {
      const user = userEvent.setup();
      open('new', '?type=6');
      expect(await screen.findByText(en['forms.designer.newTitle.quickView'])).toBeInTheDocument();
      await user.type(await nameInput(), 'QV');
      await canvasReady();
      expect(within(structure()).getByRole('tab', { name: 'General' })).toBeInTheDocument();
      expect(within(section('General')).getByRole('button', { name: 'Account Name' })).toBeInTheDocument();
      noMainCards();
      expect(screen.queryByRole('region', { name: en['forms.events.title'] })).not.toBeInTheDocument();
      await user.click(saveButton());
      await waitFor(() => expect(publishes()).toHaveLength(1));
      expect(calls.map((c) => `${c.method} ${c.path}`)).toEqual(['POST systemforms', 'POST PublishXml']);
      expect(calls[0].body).toMatchObject({ type: 6, formactivationstate: 1, isdefault: false, objecttypecode: 'account' });
    });

    it('new?type=7 opens a new quick create form', async () => {
      const user = userEvent.setup();
      open('new', '?type=7');
      expect(await screen.findByText(en['forms.designer.newTitle.quickCreate'])).toBeInTheDocument();
      await user.type(await nameInput(), 'QC');
      await canvasReady();
      noMainCards();
      expect(screen.getByRole('region', { name: en['forms.events.title'] })).toBeInTheDocument();
      await user.click(saveButton());
      await waitFor(() => expect(publishes()).toHaveLength(1));
      expect(calls[0].body).toMatchObject({ type: 7, formactivationstate: 1, isdefault: false, objecttypecode: 'account' });
    });

    it.each(['?type=9', '?type=abc'])('an unknown type (%s) opens a new Main form', async (search) => {
      open('new', search);
      await nameInput();
      await canvasReady();
      expect(screen.getByText(en['forms.designer.newTitle'])).toBeInTheDocument();
      expect(screen.getByRole('region', { name: en['forms.header.title'] })).toBeInTheDocument();
      expect(screen.queryByText(en['forms.designer.notDesigned'])).not.toBeInTheDocument();
    });

    it('moving from new?type=6 to new?type=7 builds a fresh design', async () => {
      const user = userEvent.setup();
      open('new', '?type=6');
      await user.type(await nameInput(), 'First');
      await act(() => router.navigate('/manage/tables/account/forms/new?type=7'));
      await waitFor(() => expect(screen.getByText(en['forms.designer.newTitle.quickCreate'])).toBeInTheDocument());
      await waitFor(() => expect(screen.getByRole('textbox', { name: en['forms.designer.name'] })).toHaveValue(''));
      // Quick create (7) has the Events card that Quick view (6) does not: the design itself is type 7.
      expect(await screen.findByRole('region', { name: en['forms.events.title'] })).toBeInTheDocument();
    });

    it.each([
      ['', 2],
      ['2', 2],
      ['6', 6],
      ['7', 7],
      ['11', 2],
    ])('newFormType(%j) is %i', (type, expected) => {
      expect(newFormType(new URLSearchParams(type ? { type } : {}))).toBe(expected);
    });
  });

  it('Add tab, Add section and Add field, then Save, posts the FormXML and publishes', async () => {
    const user = userEvent.setup();
    open('new');
    await user.type(await nameInput(), 'My form');
    await canvasReady();
    await user.click(within(structure()).getByRole('button', { name: en['forms.canvas.addTab'] }));
    expect(within(structure()).getByRole('tab', { name: 'tab_2' })).toHaveAttribute('aria-selected', 'true');
    await user.click(within(structure()).getByRole('button', { name: 'Add section to column 1' }));
    await user.click(within(section('section_3')).getByRole('button', { name: 'Add field to section_3' }));
    const dialog = await screen.findByRole('dialog');
    const offered = within(dialog).getAllByRole('button').map((b) => b.textContent);
    expect(offered).toContain('Main Phone');
    expect(offered).toContain('Primary Contact');
    // In the body already, a companion, system-only, not designable, not readable:
    for (const hidden of ['Account Name', 'Primary Contact Name', 'Created On', 'Entity Image', 'Secret Column']) expect(offered).not.toContain(hidden);
    await user.type(within(dialog).getByRole('textbox', { name: en['forms.picker.search'] }), 'telephone');
    expect(within(dialog).getAllByRole('button').map((b) => b.textContent)).toEqual(['Main Phone', en['common.cancel']]);
    await pick(user, 'Main Phone');
    expect(within(section('section_3')).getByRole('button', { name: 'Main Phone' })).toBeInTheDocument();

    await user.click(saveButton());
    await waitFor(() => expect(router.state.location.pathname).toBe(`/manage/tables/account/forms/${NEW_ID}`));
    await waitFor(() => expect(publishes()).toHaveLength(1));
    expect(calls.map((c) => `${c.method} ${c.path}`)).toEqual(['POST systemforms', 'POST PublishXml']);
    expect(calls[0].body).toMatchObject({ name: 'My form', objecttypecode: 'account', type: 2, isdefault: false, formactivationstate: 1 });
    expect(calls[1].body?.ParameterXml).toBe('<importexportxml><entities><entity>account</entity></entities></importexportxml>');
    const form = xmlOf(calls[0]);
    expect(names(form, 'tab')).toEqual(['tab_1', 'tab_2']);
    expect(names(form, 'section')).toEqual(['section_1', 'section_2', 'section_3']);
    const phone = control(form, 'telephone1')!;
    expect(phone.getAttribute('classid')).toBe(CLASS_ID.text);
    expect((phone.parentNode!.parentNode!.parentNode!.parentNode as Element).getAttribute('name')).toBe('section_3');
  });

  it('selecting a field shows its properties; Read-only marks the page changed and is written', async () => {
    const user = userEvent.setup();
    open();
    await nameInput();
    expect(within(propsCard()).getByText(en['forms.props.none'])).toBeInTheDocument();
    await user.click(within(section('Main')).getByRole('button', { name: 'Main Phone' }));
    expect(within(section('Main')).getByRole('button', { name: 'Main Phone' })).toHaveAttribute('aria-pressed', 'true');
    expect(within(propsCard()).getByRole('textbox', { name: en['forms.props.label'] })).toHaveValue('Main Phone');
    expect(within(propsCard()).getByText('Requirement: Optional')).toBeInTheDocument();
    const readOnly = within(propsCard()).getByRole('switch', { name: en['forms.props.readOnly'] });
    expect(readOnly).not.toBeChecked();
    await user.click(readOnly);
    expect(readOnly).toBeChecked();
    await user.click(backButton());
    expect(await screen.findByRole('dialog')).toHaveTextContent(en['forms.designer.leave.title']);
    await user.click(within(screen.getByRole('dialog')).getByRole('button', { name: en['common.cancel'] }));
    await user.click(saveButton());
    await waitFor(() => expect(stored('PATCH')).toHaveLength(1));
    expect(stored('PATCH')[0].path).toBe(`systemforms(${ID})`);
    expect(Object.keys(stored('PATCH')[0].body!).sort()).toEqual(['description', 'formxml', 'name']);
    expect(control(xmlOf(stored('PATCH')[0]), 'telephone1')!.getAttribute('disabled')).toBe('true');
    expect(control(xmlOf(stored('PATCH')[0]), 'name')!.getAttribute('disabled')).toBe('false');
  });

  it('Remove on a field with handlers names them, and on a required field says so (F7)', async () => {
    const user = userEvent.setup();
    open();
    await nameInput();
    await user.click(within(section('Main')).getByRole('button', { name: 'Remove Main Phone' }));
    let dialog = await screen.findByRole('dialog');
    expect(dialog).toHaveTextContent('Main Phone has 2 change handler(s) that will be removed with it.');
    await user.click(within(dialog).getByRole('button', { name: en['common.cancel'] }));
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    expect(within(section('Main')).getByRole('button', { name: 'Main Phone' })).toBeInTheDocument();

    await user.click(within(section('Main')).getByRole('button', { name: 'Remove Account Name' }));
    dialog = await screen.findByRole('dialog');
    expect(dialog).toHaveTextContent("Account Name is required; records can't be saved from this form without it unless it is elsewhere on the form.");
    await user.click(within(dialog).getByRole('button', { name: en['common.cancel'] }));
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());

    await user.click(within(section('Main')).getByRole('button', { name: 'Remove Main Phone' }));
    await user.click(within(await screen.findByRole('dialog')).getByRole('button', { name: en['forms.removeField.confirm'] }));
    await waitFor(() => expect(within(section('Main')).queryByRole('button', { name: 'Main Phone' })).not.toBeInTheDocument());
    await user.click(saveButton());
    await waitFor(() => expect(stored('PATCH')).toHaveLength(1));
    const form = xmlOf(stored('PATCH')[0]);
    expect(control(form, 'telephone1')).toBeUndefined();
    expect(form.getElementsByTagName('event')).toHaveLength(0);
  });

  it('Delete section asks first and counts the change handlers removed with it', async () => {
    const user = userEvent.setup();
    open();
    await nameInput();
    await user.click(within(section('Main')).getByRole('button', { name: 'Delete section Main' }));
    const dialog = await screen.findByRole('dialog');
    expect(dialog).toHaveTextContent('The section Main and the 2 item(s) in it will be removed.');
    expect(dialog).toHaveTextContent('2 change handler(s) of columns that are on the form only there will be removed with it.');
    await user.click(within(dialog).getByRole('button', { name: en['forms.delete'] }));
    await waitFor(() => expect(within(structure()).queryByRole('region', { name: 'Section Main' })).not.toBeInTheDocument());
    // The only section left cannot be deleted; neither can the only tab.
    expect(within(section('Side')).getByRole('button', { name: 'Delete section Side' })).toBeDisabled();
    expect(within(structure()).getByRole('button', { name: 'Delete tab Summary' })).toBeDisabled();
  });

  it('moves keep the focus on the moved item, across sections too', async () => {
    const user = userEvent.setup();
    open();
    await nameInput();
    await user.click(within(section('Main')).getByRole('button', { name: 'Move Main Phone up' }));
    const up = within(section('Main')).getByRole('button', { name: 'Move Main Phone up' });
    await waitFor(() => expect(up).toHaveFocus());
    await user.click(within(section('Main')).getByRole('button', { name: 'Move Main Phone to the next section' }));
    const next = within(section('Side')).getByRole('button', { name: 'Move Main Phone to the next section' });
    await waitFor(() => expect(next).toHaveFocus());
    expect(next).toHaveAttribute('aria-disabled', 'true');
    await user.click(within(section('Side')).getByRole('button', { name: 'Move section Side to the previous column' }));
    await waitFor(() => expect(within(section('Side')).getByRole('button', { name: 'Move section Side to the previous column' })).toHaveFocus());
    await user.click(saveButton());
    await waitFor(() => expect(stored('PATCH')).toHaveLength(1));
    expect(names(xmlOf(stored('PATCH')[0]), 'section')).toEqual(['main', 'side']);
    const columns = Array.from(xmlOf(stored('PATCH')[0]).getElementsByTagName('column'));
    expect(columns.map((c) => c.getElementsByTagName('section').length)).toEqual([2, 0]);
  });

  it('switching the tab layout from 2 columns to 1 keeps the sections', async () => {
    const user = userEvent.setup();
    open();
    await nameInput();
    await user.click(within(structure()).getByRole('tab', { name: 'Summary' }));
    const layout = within(propsCard()).getByRole('combobox', { name: en['forms.props.layout'] });
    expect(layout).toHaveValue('2-50');
    await user.selectOptions(layout, '1');
    expect(within(structure()).getAllByRole('region', { name: /^Section / }).map((r) => r.getAttribute('aria-label'))).toEqual(['Section Main', 'Section Side']);
    await user.click(saveButton());
    await waitFor(() => expect(stored('PATCH')).toHaveLength(1));
    const form = xmlOf(stored('PATCH')[0]);
    expect(Array.from(form.getElementsByTagName('column')).map((c) => c.getAttribute('width'))).toEqual(['100%']);
    expect(names(form, 'section')).toEqual(['main', 'side']);
  });

  it('the header takes 4 fields, then Add is disabled with the reason', async () => {
    const user = userEvent.setup();
    open();
    await nameInput();
    const add = () => within(headerCard()).getByRole('button', { name: en['forms.header.add'] });
    for (const label of ['Main Phone', 'Annual Revenue', 'Account Name']) {
      await user.click(add());
      const dialog = await screen.findByRole('dialog');
      expect(within(dialog).queryByRole('button', { name: 'Primary Contact' })).not.toBeInTheDocument();
      await pick(user, label);
    }
    expect(add()).toBeDisabled();
    expect(within(headerCard()).getByText('The header holds up to 4 fields. Remove one to add another.')).toBeInTheDocument();
    await user.click(within(headerCard()).getByRole('button', { name: 'Move header field Account Name left' }));
    await waitFor(() => expect(within(headerCard()).getByRole('button', { name: 'Move header field Account Name left' })).toHaveFocus());
    await user.click(saveButton());
    await waitFor(() => expect(stored('PATCH')).toHaveLength(1));
    const header = xmlOf(stored('PATCH')[0]).getElementsByTagName('header')[0];
    expect(Array.from(header.getElementsByTagName('control')).map((c) => c.getAttribute('id')))
      .toEqual(['header_primarycontactid', 'header_telephone1', 'header_name', 'header_revenue']);
  });

  it('a type 11 form opens read-only with the notice and no Save', async () => {
    rows[ID] = record({ type: 11 });
    open();
    expect(await screen.findByText(en['forms.designer.notDesigned'])).toBeInTheDocument();
    expect(await nameInput()).toBeDisabled();
    expect(screen.queryByRole('button', { name: en['forms.designer.save'] })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: en['forms.designer.saveAs'] })).not.toBeInTheDocument();
    expect(within(structure()).queryByRole('button', { name: en['forms.canvas.addTab'] })).not.toBeInTheDocument();
    expect(within(section('Main')).queryByRole('button', { name: 'Remove Main Phone' })).not.toBeInTheDocument();
    expect(within(headerCard()).queryByRole('button', { name: en['forms.header.add'] })).not.toBeInTheDocument();
  });

  it.each([6, 7])('a type %i form opens editable, without the header and Modern look cards', async (type) => {
    rows[ID] = record({ type });
    open();
    expect(await nameInput()).toBeEnabled();
    expect(saveButton()).toBeInTheDocument();
    expect(within(structure()).getByRole('button', { name: en['forms.canvas.addTab'] })).toBeInTheDocument();
    expect(screen.queryByRole('region', { name: en['forms.header.title'] })).not.toBeInTheDocument();
    expect(screen.queryByRole('region', { name: en['forms.look.title'] })).not.toBeInTheDocument();
  });

  it('a Main form shows the header and Modern look cards', async () => {
    open();
    await nameInput();
    expect(headerCard()).toBeInTheDocument();
    expect(await screen.findByRole('region', { name: en['forms.look.title'] })).toBeInTheDocument();
  });

  it('the canvas shows the rows as stored: a short row stays short', async () => {
    // Section Main with 2 columns and one field per row: auto-flow would pack both fields into the first row.
    const formxml = FORM.replace('columns="1">\n              <labels><label description="Main"', 'columns="2">\n              <labels><label description="Main"');
    expect(formxml).not.toBe(FORM);
    rows[ID] = record({ formxml });
    open();
    await nameInput();
    const cellBox = (label: string) => within(section('Main')).getByRole('button', { name: label }).parentElement!;
    expect(cellBox('Account Name').style.gridRow).toMatch(/^1( \/ span 1)?$/);
    expect(cellBox('Main Phone').style.gridRow).toMatch(/^2( \/ span 1)?$/);
  });

  it('a failed publish after a create keeps the new id, and the next Save updates it', async () => {
    let count = 0;
    server.use(
      http.post(`${API}/PublishXml`, () => {
        count += 1;
        calls.push({ method: 'POST', path: 'PublishXml' });
        return count === 1
          ? HttpResponse.json({ error: { code: '0x80040216', message: 'Publish went wrong' } }, { status: 500 })
          : new HttpResponse(null, { status: 204 });
      }),
    );
    const user = userEvent.setup();
    open('new');
    await user.type(await nameInput(), 'Draft');
    await canvasReady();
    await user.click(saveButton());
    expect(await screen.findByText(/Publish went wrong/)).toBeInTheDocument();
    await waitFor(() => expect(router.state.location.pathname).toBe(`/manage/tables/account/forms/${NEW_ID}`));
    await waitFor(() => expect(saveButton()).toBeEnabled());
    await user.click(saveButton());
    await waitFor(() => expect(stored('PATCH')).toHaveLength(1));
    expect(stored('PATCH')[0].path).toBe(`systemforms(${NEW_ID})`);
    expect(stored('POST')).toHaveLength(1);
    expect(reads[NEW_ID]).toBeUndefined();
    await waitFor(() => expect(screen.queryByText(/Publish went wrong/)).not.toBeInTheDocument());
  });

  it('Save as posts "{name} (copy)" and moves to the new id with its state kept', async () => {
    const user = userEvent.setup();
    open();
    await nameInput();
    await user.click(within(section('Side')).getByRole('button', { name: 'Remove Contacts (Subgrid)' }));
    await user.click(within(await screen.findByRole('dialog')).getByRole('button', { name: en['forms.removeField.confirm'] }));
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    await user.click(saveAsButton());
    await waitFor(() => expect(router.state.location.pathname).toBe(`/manage/tables/account/forms/${NEW_ID}`));
    expect(stored('POST')[0].body).toMatchObject({ name: 'Information (copy)', type: 2, isdefault: false, formactivationstate: 1 });
    expect(control(xmlOf(stored('POST')[0]), 'contacts')).toBeUndefined();
    expect(await nameInput()).toHaveValue('Information (copy)');
    expect(within(section('Side')).queryByRole('button', { name: 'Contacts (Subgrid)' })).not.toBeInTheDocument();
    expect(stored('PATCH')).toHaveLength(0);
    expect(reads[NEW_ID]).toBeUndefined();
    await waitFor(() => expect(saveButton()).toBeEnabled());
    // Saved: leaving does not ask.
    await user.click(backButton());
    await waitFor(() => expect(router.state.location.pathname).toBe('/manage/tables/account'));
  });

  it.each([6, 7])('Save as of a type %i form posts that type and moves to the new id with its state kept', async (type) => {
    rows[ID] = record({ type, isdefault: false });
    const user = userEvent.setup();
    open();
    await nameInput();
    await user.click(within(section('Main')).getByRole('button', { name: 'Remove Main Phone' }));
    await user.click(within(await screen.findByRole('dialog')).getByRole('button', { name: en['forms.removeField.confirm'] }));
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    await user.click(saveAsButton());
    await waitFor(() => expect(router.state.location.pathname).toBe(`/manage/tables/account/forms/${NEW_ID}`));
    expect(stored('POST')[0].body).toMatchObject({ name: 'Information (copy)', type, isdefault: false, formactivationstate: 1 });
    expect(control(xmlOf(stored('POST')[0]), 'telephone1')).toBeUndefined();
    expect(await nameInput()).toHaveValue('Information (copy)');
    expect(within(section('Main')).queryByRole('button', { name: 'Main Phone' })).not.toBeInTheDocument();
    expect(stored('PATCH')).toHaveLength(0);
    expect(reads[NEW_ID]).toBeUndefined();
  }, PAGE_TEST_TIMEOUT);

  it('a field moved past the last section of a tab shows the next tab and keeps the focus', async () => {
    rows[ID] = record({
      formxml: FORM.replace('  </tabs>', `    <tab name="more" id="{a0000000-0000-0000-0000-000000000005}" showlabel="true">
      <labels><label description="More" languagecode="1033" /></labels>
      <columns><column width="100%"><sections>
        <section name="extra" id="{a0000000-0000-0000-0000-000000000006}" showlabel="true" columns="1">
          <labels><label description="Extra" languagecode="1033" /></labels>
          <rows />
        </section>
      </sections></column></columns>
    </tab>
  </tabs>`),
    });
    const user = userEvent.setup();
    open();
    await nameInput();
    await user.click(within(section('Main')).getByRole('button', { name: 'Move Main Phone to the next section' }));
    await user.click(within(section('Side')).getByRole('button', { name: 'Move Main Phone to the next section' }));
    await waitFor(() => expect(within(structure()).getByRole('tab', { name: 'More' })).toHaveAttribute('aria-selected', 'true'));
    const moved = within(section('Extra')).getByRole('button', { name: 'Move Main Phone to the next section' });
    await waitFor(() => expect(moved).toHaveFocus());
    expect(moved).toHaveAttribute('aria-disabled', 'true');
  }, PAGE_TEST_TIMEOUT);

  it('Save as of a name near the limit cuts the base so the copy fits 100 characters', async () => {
    rows[ID] = record({ name: 'N'.repeat(100) });
    const user = userEvent.setup();
    open();
    await nameInput();
    await user.click(saveAsButton());
    await waitFor(() => expect(stored('POST')).toHaveLength(1));
    expect(stored('POST')[0].body?.name).toBe(`${'N'.repeat(93)} (copy)`);
  });

  it('Back after Save and Save as reads the source again and shows the saved edits', async () => {
    const user = userEvent.setup();
    open();
    await user.type(await nameInput(), ' X');
    await user.click(saveButton());
    await waitFor(() => expect(stored('PATCH')).toHaveLength(1));
    await waitFor(() => expect(saveButton()).toBeEnabled());
    await user.click(saveAsButton());
    await waitFor(() => expect(router.state.location.pathname).toBe(`/manage/tables/account/forms/${NEW_ID}`));
    await waitFor(() => expect(saveButton()).toBeEnabled());
    await act(() => router.navigate(-1));
    await waitFor(() => expect(router.state.location.pathname).toBe(`/manage/tables/account/forms/${ID}`));
    await waitFor(() => expect(reads[ID]).toBe(2));
    await waitFor(() => expect(screen.getByRole('textbox', { name: en['forms.designer.name'] })).toHaveValue('Information X'));
    await user.click(saveButton());
    await waitFor(() => expect(stored('PATCH')).toHaveLength(2));
    expect(stored('PATCH')[1]).toMatchObject({ path: `systemforms(${ID})`, body: { name: 'Information X' } });
    // The last Save publishes after its PATCH: wait for it, so no request of this test reaches the next one's handlers.
    await waitFor(() => expect(publishes()).toHaveLength(3));
    await waitFor(() => expect(saveButton()).toBeEnabled());
  }, TWO_PAGES);

  it('router navigation from form A to form B shows B, and B\'s Save does not touch A', async () => {
    const user = userEvent.setup();
    open();
    await user.type(await nameInput(), ' A');
    await act(() => router.navigate(`/manage/tables/account/forms/${OTHER}`));
    await waitFor(() => expect(screen.getByRole('textbox', { name: en['forms.designer.name'] })).toHaveValue('Other form'));
    await user.type(screen.getByRole('textbox', { name: en['forms.designer.name'] }), ' B');
    await user.click(saveButton());
    await waitFor(() => expect(publishes()).toHaveLength(1));
    expect(calls.map((c) => `${c.method} ${c.path}`)).toEqual([`PATCH systemforms(${OTHER})`, 'POST PublishXml']);
    expect(stored('PATCH')[0].body?.name).toBe('Other form B');
  }, TWO_PAGES);

  it.each([
    ['an empty FormXML', ''],
    ['a null FormXML', null],
    ['a malformed FormXML', '<form><tabs>'],
  ])('a row with %s shows the load error instead of crashing', async (_, formxml) => {
    rows[ID] = record({ formxml });
    open();
    expect(await screen.findByText(loadFailed)).toBeInTheDocument();
    expect(screen.queryByRole('textbox', { name: en['forms.designer.name'] })).not.toBeInTheDocument();
    expect(backButton()).toBeInTheDocument();
  });

  it('the load error of an unreadable row goes when another form opens', async () => {
    rows[ID] = record({ formxml: '' });
    open();
    expect(await screen.findByText(loadFailed)).toBeInTheDocument();
    await act(() => router.navigate(`/manage/tables/account/forms/${OTHER}`));
    expect(await screen.findByDisplayValue('Other form')).toBeInTheDocument();
    expect(screen.queryByText(loadFailed)).not.toBeInTheDocument();
  });

  it('a managed form shows the info bar; a refused save shows the server message verbatim', async () => {
    rows[ID] = record({ ismanaged: true });
    server.use(
      http.patch(`${API}/*`, () =>
        HttpResponse.json({ error: { code: '0x80048405', message: 'The component cannot be customized & stays as it is.' } }, { status: 400 }),
      ),
    );
    const user = userEvent.setup();
    open();
    await nameInput();
    expect(screen.getByText(en['forms.designer.managed'])).toBeInTheDocument();
    await user.click(saveButton());
    expect(await screen.findByText(/The component cannot be customized & stays as it is\./)).toBeInTheDocument();
    expect(publishes()).toHaveLength(0);
    expect(saveButton()).toBeEnabled();
  }, PAGE_TEST_TIMEOUT);

  it('Back without changes leaves at once; with changes asks, and Leave goes back to the table', async () => {
    const user = userEvent.setup();
    open();
    const name = await nameInput();
    await user.click(backButton());
    await waitFor(() => expect(router.state.location.pathname).toBe('/manage/tables/account'));
    await act(() => router.navigate(`/manage/tables/account/forms/${ID}`));
    await user.type(await nameInput(), '!');
    expect(name).not.toBeInTheDocument();
    await user.click(backButton());
    await user.click(within(await screen.findByRole('dialog')).getByRole('button', { name: en['forms.designer.leave.confirm'] }));
    await waitFor(() => expect(router.state.location.pathname).toBe('/manage/tables/account'));
  }, TWO_PAGES);

  describe('drag and drop', { timeout: PAGE_TEST_TIMEOUT }, () => {
    const MORE_TAB = `    <tab name="more" id="{a0000000-0000-0000-0000-000000000005}" showlabel="true">
      <labels><label description="More" languagecode="1033" /></labels>
      <columns><column width="100%"><sections>
        <section name="extra" id="{a0000000-0000-0000-0000-000000000006}" showlabel="true" columns="1">
          <labels><label description="Extra" languagecode="1033" /></labels>
          <rows />
        </section>
      </sections></column></columns>
    </tab>
  </tabs>`;
    const TWO_HEADER = FORM.replace(
      '</row>\n    </rows>\n  </header>',
      `${cell('c0000000-0000-0000-0000-000000000005', 'Account Name', field('name', 'header_'))}</row>\n    </rows>\n  </header>`,
    );
    const stub = () => {
      const data: Record<string, string> = {};
      return { setData: (k: string, v: string) => (data[k] = v), getData: (k: string) => data[k] ?? '', effectAllowed: '', dropEffect: '' };
    };
    /** Drags `from` onto `to`; returns whether the drag over was accepted (default prevented). */
    const dragTo = (from: Element, to: Element) => {
      const dataTransfer = stub();
      fireEvent.dragStart(from, { dataTransfer });
      const accepted = !fireEvent.dragOver(to, { dataTransfer });
      fireEvent.drop(to, { dataTransfer });
      fireEvent.dragEnd(from, { dataTransfer });
      return accepted;
    };
    const cellBox = (within_: HTMLElement, label: string) => within(within_).getByRole('button', { name: label }).closest('[draggable="true"]') as HTMLElement;
    /** The page is clean when Back leaves at once, without the unsaved-changes question. */
    const expectClean = async () => {
      await userEvent.setup().click(backButton());
      await waitFor(() => expect(router.state.location.pathname).toBe('/manage/tables/account'));
      expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    };
    /** The drop target before a cell: the cell's own box, found from its button (A1: it has no role or name). */
    const dropBefore = (within_: HTMLElement, label: string) =>
      within(within_).getByRole('button', { name: label }).closest('[data-drop^="before:"]') as HTMLElement;
    /** The drop strip at the end of a section or a column (A1: pointer-only, aria-hidden). */
    const dropEnd = (container: HTMLElement) => container.querySelector(':scope > [data-drop^="end:"]') as HTMLElement;

    it('a cell dropped before a cell of another section moves there, selected and focused', async () => {
      open();
      await nameInput();
      await canvasReady();
      expect(dragTo(cellBox(section('Main'), 'Main Phone'), dropBefore(section('Side'), 'Contacts (Subgrid)'))).toBe(true);
      const moved = within(section('Side')).getByRole('button', { name: 'Main Phone' });
      expect(within(section('Main')).queryByRole('button', { name: 'Main Phone' })).not.toBeInTheDocument();
      expect(within(section('Side')).getAllByRole('button', { name: /^(Main Phone|Contacts)/ })[0]).toBe(moved);
      await waitFor(() => expect(moved).toHaveFocus());
      expect(moved).toHaveAttribute('aria-pressed', 'true');
      expect(saveButton()).toBeEnabled();
    });

    it('a cell dropped at the end of a section goes last', async () => {
      open();
      await nameInput();
      await canvasReady();
      dragTo(cellBox(section('Main'), 'Account Name'), dropEnd(section('Side')));
      expect(within(section('Side')).getAllByRole('button', { name: /^(Account Name|Contacts)/ }).map((b) => b.textContent)).toEqual([
        'Contacts (Subgrid)',
        'Account Name',
      ]);
    });

    it('a section dropped at the end of the other column moves', async () => {
      const user = userEvent.setup();
      open();
      await nameInput();
      await canvasReady();
      const column = (n: number) => within(structure()).getByRole('group', { name: `Column ${n}` });
      expect(dragTo(section('Side'), dropEnd(column(1)))).toBe(true);
      expect(within(column(1)).getAllByRole('region').map((r) => r.getAttribute('aria-label'))).toEqual(['Section Main', 'Section Side']);
      expect(within(column(2)).queryAllByRole('region')).toHaveLength(0);
      const label = within(section('Side')).getByRole('button', { name: 'Side' });
      await waitFor(() => expect(label).toHaveFocus());
      await user.click(saveButton());
      await waitFor(() => expect(stored('PATCH')).toHaveLength(1));
      expect(names(xmlOf(stored('PATCH')[0]), 'section')).toEqual(['main', 'side']);
    });

    it('a tab dropped before the first tab moves', async () => {
      rows[ID] = record({ formxml: FORM.replace('  </tabs>', MORE_TAB) });
      const user = userEvent.setup();
      open();
      await nameInput();
      await canvasReady();
      const tabs = () => within(structure()).getAllByRole('tab').map((x) => x.querySelector('.fui-Tab__content')?.textContent);
      expect(tabs()).toEqual(['Summary', 'More']);
      dragTo(within(structure()).getByRole('tab', { name: 'More' }), within(structure()).getByRole('tab', { name: 'Summary' }));
      expect(tabs()).toEqual(['More', 'Summary']);
      await waitFor(() => expect(within(structure()).getByRole('tab', { name: 'More' })).toHaveFocus());
      await user.click(saveButton());
      await waitFor(() => expect(stored('PATCH')).toHaveLength(1));
      expect(names(xmlOf(stored('PATCH')[0]), 'tab')).toEqual(['more', 'general']);
    });

    it('a header field dropped before another is reordered', async () => {
      rows[ID] = record({ formxml: TWO_HEADER });
      const user = userEvent.setup();
      open();
      await nameInput();
      await canvasReady();
      const chip = (label: string) => within(headerCard()).getByRole('button', { name: label }).closest('li') as HTMLElement;
      expect(dragTo(chip('Account Name'), chip('Primary Contact'))).toBe(true);
      const moved = within(headerCard()).getByRole('button', { name: 'Account Name' });
      await waitFor(() => expect(moved).toHaveFocus());
      expect(moved).toHaveAttribute('aria-pressed', 'true');
      await user.click(saveButton());
      await waitFor(() => expect(stored('PATCH')).toHaveLength(1));
      const header = xmlOf(stored('PATCH')[0]).getElementsByTagName('header')[0];
      expect(Array.from(header.getElementsByTagName('control')).map((c) => c.getAttribute('id'))).toEqual(['header_name', 'header_primarycontactid']);
    });

    it('a header field dropped across a non-field header cell keeps that cell in place', async () => {
      const own = cell('c0000000-0000-0000-0000-000000000004', 'Primary Contact', field('primarycontactid', 'header_'));
      const a = cell('c0000000-0000-0000-0000-000000000005', 'Account Name', field('name', 'header_'));
      const x = cell('c0000000-0000-0000-0000-000000000006', 'Timeline', '<control id="header_notes" classid="{06375649-C143-495E-A496-C962E5B4488E}" />');
      const source = FORM.replace(`<row>${own}</row>`, `<row>${a}${x}${own}</row>`);
      expect(source).not.toBe(FORM);
      rows[ID] = record({ formxml: source });
      const user = userEvent.setup();
      open();
      await nameInput();
      await canvasReady();
      const chip = (label: string) => within(headerCard()).getByRole('button', { name: label }).closest('li') as HTMLElement;
      expect(dragTo(chip('Account Name'), chip('Primary Contact'))).toBe(true);
      await user.click(saveButton());
      await waitFor(() => expect(stored('PATCH')).toHaveLength(1));
      const header = xmlOf(stored('PATCH')[0]).getElementsByTagName('header')[0];
      const cells = (h: Element) => Array.from(h.getElementsByTagName('cell'));
      expect(cells(header).map((c) => c.getAttribute('id'))).toEqual([
        '{c0000000-0000-0000-0000-000000000006}', '{c0000000-0000-0000-0000-000000000005}', '{c0000000-0000-0000-0000-000000000004}',
      ]);
      // The non-field cell is written exactly as it was read.
      const serialize = (n: Node) => new XMLSerializer().serializeToString(n);
      const original = cells(parseXml(source).documentElement.getElementsByTagName('header')[0])[1];
      expect(serialize(cells(header)[0])).toBe(serialize(original));
    });

    // Split in three: one render each keeps every test well inside the 5 s limit under a loaded CI runner.
    it('a drop of a cell on its own position leaves the page clean', async () => {
      rows[ID] = record({ formxml: TWO_HEADER.replace('  </tabs>', MORE_TAB) });
      open();
      await nameInput();
      await canvasReady();
      // On itself, and before the cell that follows it (both are where the item already is).
      dragTo(cellBox(section('Main'), 'Main Phone'), dropBefore(section('Main'), 'Main Phone'));
      dragTo(cellBox(section('Main'), 'Account Name'), dropBefore(section('Main'), 'Main Phone'));
      expect(within(section('Main')).getAllByRole('button', { name: /^(Account Name|Main Phone)$/ }).map((b) => b.textContent)).toEqual(['Account Name', 'Main Phone']);
      await expectClean();
    });

    it('a drop of a section or a tab on its own position leaves the page clean', async () => {
      rows[ID] = record({ formxml: TWO_HEADER.replace('  </tabs>', MORE_TAB) });
      open();
      await nameInput();
      await canvasReady();
      dragTo(section('Main'), section('Main'));
      dragTo(within(structure()).getByRole('tab', { name: 'Summary' }), within(structure()).getByRole('tab', { name: 'Summary' }));
      dragTo(within(structure()).getByRole('tab', { name: 'Summary' }), within(structure()).getByRole('tab', { name: 'More' }));
      await expectClean();
    });

    it('a drop of a header field on its own position leaves the page clean', async () => {
      rows[ID] = record({ formxml: TWO_HEADER });
      open();
      await nameInput();
      await canvasReady();
      const chip = (label: string) => within(headerCard()).getByRole('button', { name: label }).closest('li') as HTMLElement;
      dragTo(chip('Primary Contact'), chip('Primary Contact'));
      dragTo(chip('Primary Contact'), chip('Account Name'));
      await expectClean();
    });

    it('a tab and a header field dropped on the end zones go last (H7)', async () => {
      rows[ID] = record({ formxml: TWO_HEADER.replace('  </tabs>', MORE_TAB) });
      const user = userEvent.setup();
      open();
      await nameInput();
      await canvasReady();
      const zone = (id: string) => document.querySelector(`[data-drop="${id}"]`) as HTMLElement;
      expect(dragTo(within(structure()).getByRole('tab', { name: 'Summary' }), zone('end:tabs'))).toBe(true);
      const tabs = () => within(structure()).getAllByRole('tab').map((x) => x.querySelector('.fui-Tab__content')?.textContent);
      expect(tabs()).toEqual(['More', 'Summary']);
      const chip = (label: string) => within(headerCard()).getByRole('button', { name: label }).closest('li') as HTMLElement;
      expect(dragTo(chip('Primary Contact'), zone('end:header'))).toBe(true);
      await waitFor(() => expect(within(headerCard()).getByRole('button', { name: 'Primary Contact' })).toHaveFocus());
      await user.click(saveButton());
      await waitFor(() => expect(stored('PATCH')).toHaveLength(1));
      const form = xmlOf(stored('PATCH')[0]);
      expect(names(form, 'tab')).toEqual(['more', 'general']);
      const header = form.getElementsByTagName('header')[0];
      expect(Array.from(header.getElementsByTagName('control')).map((c) => c.getAttribute('id'))).toEqual(['header_name', 'header_primarycontactid']);
    });

    it('offers no target for a section of another tab, or across body and header', async () => {
      rows[ID] = record({ formxml: FORM.replace('  </tabs>', MORE_TAB) });
      open();
      await nameInput();
      await canvasReady();
      // Only the shown tab has sections to drop on.
      expect(within(structure()).queryByRole('region', { name: 'Section Extra' })).not.toBeInTheDocument();
      const chip = within(headerCard()).getByRole('button', { name: 'Primary Contact' }).closest('li') as HTMLElement;
      // A body cell over the header, a header field over the body: not a target, and nothing changes.
      expect(dragTo(cellBox(section('Main'), 'Main Phone'), chip)).toBe(false);
      expect(dragTo(chip, dropBefore(section('Main'), 'Account Name'))).toBe(false);
      expect(dragTo(chip, dropEnd(section('Main')))).toBe(false);
      // A cell is not dropped on a section.
      expect(dragTo(cellBox(section('Main'), 'Main Phone'), section('Side'))).toBe(false);
      await expectClean();
    });

    it('is not offered on a read-only form', async () => {
      rows[ID] = record({ type: 11 });
      open();
      await nameInput();
      await canvasReady();
      expect(structure().querySelector('[draggable="true"]')).toBeNull();
      expect(structure().querySelector('[data-drop]')).toBeNull();
    });

    it('canvas cells and drop strips are not announced (A1)', async () => {
      rows[ID] = record({ formxml: TWO_HEADER.replace('  </tabs>', MORE_TAB) });
      open();
      await nameInput();
      await canvasReady();
      expect(screen.queryAllByRole('group', { name: /^Drop / })).toHaveLength(0);
      const ends = Array.from(document.querySelectorAll('[data-drop^="end:"]'));
      // Both sections, both columns, the tab list and the header.
      expect(ends.length).toBe(6);
      for (const end of ends) expect(end).toHaveAttribute('aria-hidden', 'true');
      for (const name of ['Account Name', 'Main Phone']) expect(within(section('Main')).getByRole('button', { name })).toBeInTheDocument();
      expect(within(section('Side')).getByRole('button', { name: 'Contacts (Subgrid)' })).toBeInTheDocument();
      const cellTarget = dropBefore(section('Main'), 'Main Phone');
      expect(cellTarget).not.toHaveAttribute('role');
      expect(cellTarget).not.toHaveAttribute('aria-label');
    });
  });

  describe('required-property check (H5)', { timeout: PAGE_TEST_TIMEOUT }, () => {
    const PHONE_ID = '{b0000000-0000-0000-0000-000000000001}';
    const entry = (formFactor: string) =>
      `<customControl formFactor="${formFactor}" name="new_Ctl.Mask"><parameters><value>telephone1</value></parameters></customControl>`;
    const BOUND = FORM.replace(field('telephone1'), `<control id="telephone1" classid="${TEXT}" datafieldname="telephone1" uniqueid="${PHONE_ID}" />`)
      .replace('  <events>', `  <controlDescriptions>
    <controlDescription forControl="${PHONE_ID}">
      <customControl id="${TEXT}"><parameters><datafieldname>telephone1</datafieldname></parameters></customControl>
      ${entry('0')}${entry('1')}${entry('2')}
    </controlDescription>
  </controlDescriptions>
  <events>`);
    const failedPrefix = en['forms.designer.checkFailed'].replace('{detail}', '');

    it('Save and Save as wait while the custom controls load', async () => {
      rows[ID] = record({ formxml: BOUND });
      let release!: () => void;
      const gate = new Promise<void>((r) => {
        release = r;
      });
      server.use(onPath('get', 'customcontrols', async () => {
        await gate;
        return HttpResponse.json({ value: [] });
      }));
      open();
      await nameInput();
      await canvasReady();
      expect(screen.getByText(en['forms.designer.checkPending'])).toBeInTheDocument();
      expect(saveButton()).toBeDisabled();
      expect(saveAsButton()).toBeDisabled();
      release();
      await waitFor(() => expect(saveButton()).toBeEnabled());
      expect(saveAsButton()).toBeEnabled();
      expect(screen.queryByText(en['forms.designer.checkPending'])).not.toBeInTheDocument();
    });

    it('Save and Save as stay disabled after navigating to a form of another table, until that table\'s metadata arrives', async () => {
      const contact = rawEntity('contact', 'contacts', [
        rawAttr('contactid', 'Uniqueidentifier', { IsPrimaryId: true }),
        rawAttr('fullname', 'String', { IsPrimaryName: true, display: 'Full Name' }),
      ], { PrimaryIdAttribute: 'contactid', PrimaryNameAttribute: 'fullname', display: 'Contact' });
      let release!: () => void;
      const gate = new Promise<void>((r) => {
        release = r;
      });
      server.use(onPath('get', "EntityDefinitions(LogicalName='contact')", async () => {
        await gate;
        return HttpResponse.json(contact);
      }));
      rows[ID] = record({ formxml: BOUND });
      open();
      await nameInput();
      await canvasReady();
      await waitFor(() => expect(saveButton()).toBeEnabled());
      // Another table's form: nothing is saved until that table's columns arrive (the required check needs them).
      await act(() => router.navigate(`/manage/tables/contact/forms/${OTHER}`));
      expect(saveButton()).toBeDisabled();
      expect(screen.queryByText(en['forms.designer.saveAs'], { selector: 'button:not([disabled])' })).not.toBeInTheDocument();
      release();
      // The held metadata arrives: the new table's form is ready and Save and Save as are enabled again.
      await waitFor(() => expect(saveButton()).toBeEnabled());
      expect(saveAsButton()).toBeEnabled();
      expect(router.state.location.pathname).toBe(`/manage/tables/contact/forms/${OTHER}`);
    });

    it('Save as is blocked by a missing PCF required property', { timeout: PAGE_TEST_TIMEOUT }, async () => {
      rows[ID] = record({ formxml: BOUND });
      const box = '<manifest><control namespace="Ctl" constructor="Mask" version="1.0.0" control-type="standard">'
        + '<property name="value" of-type="SingleLine.Text" usage="bound" required="true" />'
        + '<property name="placeholder" of-type="SingleLine.Text" usage="input" required="true" /></control></manifest>';
      server.use(onPath('get', 'customcontrols', () => HttpResponse.json({ value: [{ name: 'new_Ctl.Mask', manifest: box }] })));
      const user = userEvent.setup();
      open();
      await nameInput();
      await canvasReady();
      await waitFor(() => expect(document.getElementById('form-save-pcf')).not.toBeNull());
      expect(saveAsButton()).toBeDisabled();
      expect(saveButton()).toBeDisabled();
      await user.click(saveAsButton());
      expect(calls.filter((c) => c.method === 'POST')).toEqual([]);
      expect(router.state.location.pathname).toBe(`/manage/tables/account/forms/${ID}`);
    });

    it('a form without bindings makes no customcontrols request', { timeout: PAGE_TEST_TIMEOUT }, async () => {
      const web = cell('c0000000-0000-0000-0000-000000000007', 'Map', `<control id="WebResource_map" classid="${CLASS_ID.webResource}"><parameters><Url>new_map.html</Url><Scrolling>auto</Scrolling></parameters></control>`);
      rows[ID] = record({ formxml: FORM.replace(`<row>${cell('c0000000-0000-0000-0000-000000000003', 'Contacts', '<control id="contacts" classid="{E7A81278-8635-4D9E-8D4D-59480B391C5B}" />')}</row>`, `$&<row>${web}</row>`) });
      expect(String(rows[ID].formxml)).toContain('WebResource_map');
      const paths: string[] = [];
      server.use(http.all(`${API}/customcontrols*`, ({ request }) => {
        paths.push(pathOf(request));
        return HttpResponse.json({ value: [] });
      }));
      const user = userEvent.setup();
      open();
      await nameInput();
      await canvasReady();
      await user.click(within(section('Side')).getByRole('button', { name: 'Side' }));
      await user.click(within(section('Side')).getByRole('button', { name: 'Map (Web resource)' }));
      await waitFor(() => expect(saveButton()).toBeEnabled());
      await user.click(saveButton());
      await waitFor(() => expect(stored('PATCH')).toHaveLength(1));
      expect(paths).toEqual([]);
    });

    it('a failed custom control list blocks Save with the server text and Retry', async () => {
      rows[ID] = record({ formxml: BOUND });
      let requests = 0;
      server.use(onPath('get', 'customcontrols', () => {
        requests += 1;
        return requests === 1
          ? HttpResponse.json({ error: { code: '0x80040220', message: 'No read on customcontrol' } }, { status: 403 })
          : HttpResponse.json({ value: [] });
      }));
      const user = userEvent.setup();
      open();
      await nameInput();
      await canvasReady();
      expect(await screen.findByText((text) => text.startsWith(failedPrefix) && text.includes('No read on customcontrol'))).toBeInTheDocument();
      expect(saveButton()).toBeDisabled();
      expect(saveAsButton()).toBeDisabled();
      await user.click(screen.getByRole('button', { name: en['forms.designer.retry'] }));
      await waitFor(() => expect(saveButton()).toBeEnabled());
      expect(requests).toBe(2);
      expect(saveAsButton()).toBeEnabled();
      expect(screen.queryByText((text) => text.startsWith(failedPrefix))).not.toBeInTheDocument();
    });

    it('Save and Save as are described by the messages that block them (A2)', async () => {
      const describedBy = (button: HTMLElement) => (button.getAttribute('aria-describedby') ?? '').split(' ');
      // The list loading: the H5 message.
      rows[ID] = record({ formxml: BOUND });
      let release!: () => void;
      const gate = new Promise<void>((r) => {
        release = r;
      });
      const box = '<manifest><control namespace="Ctl" constructor="Mask" version="1.0.0" control-type="standard">'
        + '<property name="value" of-type="SingleLine.Text" usage="bound" required="true" />'
        + '<property name="placeholder" of-type="SingleLine.Text" usage="input" required="true" /></control></manifest>';
      server.use(onPath('get', 'customcontrols', async () => {
        await gate;
        return HttpResponse.json({ value: [{ name: 'new_Ctl.Mask', manifest: box }] });
      }));
      const user = userEvent.setup();
      open();
      const name = await nameInput();
      await canvasReady();
      expect(document.getElementById('form-save-check')).not.toBeNull();
      for (const button of [saveButton(), saveAsButton()]) expect(describedBy(button)).toContain('form-save-check');
      // A required property unset: the PCF warning.
      release();
      await waitFor(() => expect(document.getElementById('form-save-check')).toBeNull());
      expect(document.getElementById('form-save-pcf')).not.toBeNull();
      for (const button of [saveButton(), saveAsButton()]) {
        expect(describedBy(button)).toContain('form-save-pcf');
        expect(describedBy(button)).not.toContain('form-save-check');
      }
      // A blank name: the name's validation message.
      await user.clear(name);
      const message = screen.getByText(en['forms.designer.nameRequired']).closest('[id]') as HTMLElement;
      expect(message.id).not.toBe('');
      for (const button of [saveButton(), saveAsButton()]) {
        expect(describedBy(button)).toContain(message.id);
        expect(describedBy(button)).toContain('form-save-pcf');
      }
    });

    it('a form without custom controls never requests the list and is not blocked', async () => {
      let requests = 0;
      server.use(onPath('get', 'customcontrols', () => {
        requests += 1;
        return HttpResponse.json({ value: [] });
      }));
      open();
      await nameInput();
      await canvasReady();
      expect(saveButton()).toBeEnabled();
      expect(screen.queryByText(en['forms.designer.checkPending'])).not.toBeInTheDocument();
      expect(requests).toBe(0);
    });
  });

  describe('the Modern look card', { timeout: PAGE_TEST_TIMEOUT }, () => {
    const WR = 'eeeeeeee-0000-0000-0000-000000000001';
    const look = () => screen.getByRole('region', { name: en['forms.look.title'] });
    const webResources = () => calls.filter((c) => c.path === `webresourceset(${WR})`);
    const publishedKinds = () => publishes().map((c) => (String(c.body?.ParameterXml).includes('<webresources>') ? 'webresource' : 'table'));

    /** An environment overlay `new_env.json` without entries; `failPatch` refuses its PATCH. */
    function overlay(failPatch = false) {
      server.use(
        onPath('get', 'settingdefinitions', () => HttpResponse.json({ value: [{ settingdefinitionid: 'd1', uniquename: 'xrmverse_Presentation', defaultvalue: null }] })),
        onPath('get', 'organizationsettings', () => HttpResponse.json({ value: [{ value: 'new_env.json' }] })),
        onPath('get', 'appsettings', () => HttpResponse.json({ value: [] })),
        onPath('get', 'webresourceset', () => HttpResponse.json({ value: [{ webresourceid: WR, name: 'new_env.json', content: encodeContent('{"version":1}') }] })),
      );
      if (failPatch)
        server.use(
          http.patch(`${API}/webresourceset*`, async ({ request }) => {
            calls.push({ method: 'PATCH', path: pathOf(request) });
            return HttpResponse.json({ error: { code: '0x1', message: 'Overlay refused' } }, { status: 403 });
          }),
        );
    }

    it('is shown for a type 2 form', async () => {
      overlay();
      rows[ID] = record({ type: 2, isdefault: false });
      open();
      await nameInput();
      expect(look()).toBeInTheDocument();
    });

    it.each([6, 7, 11])('is not shown for a type %i form', async (type) => {
      overlay();
      rows[ID] = record({ type });
      open();
      await nameInput();
      expect(screen.queryByRole('region', { name: en['forms.look.title'] })).not.toBeInTheDocument();
    });

    it('after-save steps are skipped on a failed publish and run on the retry', async () => {
      overlay();
      let count = 0;
      server.use(
        http.post(`${API}/PublishXml`, async ({ request }) => {
          const body = (await request.json()) as Record<string, unknown>;
          calls.push({ method: 'POST', path: 'PublishXml', body });
          if (String(body.ParameterXml).includes('<entities>') && (count += 1) === 1)
            return HttpResponse.json({ error: { code: '0x80040216', message: 'Publish went wrong' } }, { status: 500 });
          return new HttpResponse(null, { status: 204 });
        }),
      );
      const user = userEvent.setup();
      open();
      await nameInput();
      await user.selectOptions(await within(look()).findByRole('combobox', { name: en['forms.look.layout'] }), 'wizard');
      await user.click(saveButton());
      expect(await screen.findByText(/Publish went wrong/)).toBeInTheDocument();
      await waitFor(() => expect(saveButton()).toBeEnabled());
      expect(webResources()).toHaveLength(0);
      await user.click(saveButton());
      await waitFor(() => expect(webResources()).toHaveLength(1));
      await waitFor(() => expect(publishedKinds()).toEqual(['table', 'table', 'webresource']));
      expect(JSON.parse(atob(String(webResources()[0].body?.content)))).toEqual({ version: 1, forms: { [ID]: { layout: 'wizard' } } });
    });

    it('a failed overlay PATCH shows the error and leaves the page changed, so Back still asks', async () => {
      overlay(true);
      const user = userEvent.setup();
      open();
      await nameInput();
      await user.selectOptions(await within(look()).findByRole('combobox', { name: en['forms.look.layout'] }), 'wizard');
      await user.click(saveButton());
      expect(await within(look()).findByText(/Overlay refused/)).toBeInTheDocument();
      await waitFor(() => expect(saveButton()).toBeEnabled());
      expect(publishedKinds()).toEqual(['table']);
      await user.click(backButton());
      expect(await screen.findByRole('dialog')).toHaveTextContent(en['forms.designer.leave.title']);
    });
  });
});
