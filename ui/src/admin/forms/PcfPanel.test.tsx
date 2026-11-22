import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { afterEach, describe, expect, it } from 'vitest';
import { server } from '../../test/server';
import { App } from '../../shell/App';
import { en } from '../../shell/i18n/en';
import { parseXml } from '../../parsers/xml';
import { CLASS_ID } from '../../parsers/controlClass';
import { API, onPath, signInAsAdministrator } from '../security/testSupport';
import { installMetadata, rawAttr, rawEntity } from '../tables/tableTestSupport';
import { PAGE_TEST_TIMEOUT } from './controlsTestSupport';

/** The custom (PCF) control panel of the form designer (G5). */

const FORM_ID = '11111111-1111-1111-1111-111111111111';
const ROOT = '/api/data/v9.2';
const TEXT = '{4273EDBD-AC1D-40d3-9FB2-095C621B552D}';
const PHONE_ID = '{b0000000-0000-0000-0000-000000000001}';
const SITE_ID = '{b0000000-0000-0000-0000-000000000002}';
const NOTES_ID = '{b0000000-0000-0000-0000-000000000003}';

const account = rawEntity(
  'account',
  'accounts',
  [
    rawAttr('accountid', 'Uniqueidentifier', { IsPrimaryId: true }),
    rawAttr('name', 'String', { IsPrimaryName: true, display: 'Account Name' }),
    rawAttr('telephone1', 'String', { display: 'Main Phone' }),
    rawAttr('websiteurl', 'String', { display: 'Website' }),
    rawAttr('description', 'Memo', { display: 'Description' }),
    rawAttr('numberofemployees', 'Integer', { display: 'Employees' }),
  ],
  { PrimaryIdAttribute: 'accountid', PrimaryNameAttribute: 'name', display: 'Account' },
);
const contact = rawEntity('contact', 'contacts', [rawAttr('contactid', 'Uniqueidentifier', { IsPrimaryId: true })], { display: 'Contact' });

const manifest = (constructor: string, body: string) =>
  `<manifest><control namespace="Ctl" constructor="${constructor}" version="1.0.0" control-type="standard">${body}</control></manifest>`;

const CONTROLS = [
  {
    name: 'new_Ctl.Box',
    manifest: manifest('Box', `
      <property name="value" of-type="SingleLine.Text" usage="bound" required="true" />
      <property name="placeholder" of-type="SingleLine.Text" usage="input" required="true" />
      <property name="limit" of-type="Whole.None" usage="input" required="false" default-value="100" />`),
  },
  { name: 'new_Ctl.Mask', manifest: manifest('Mask', '<property name="value" of-type="SingleLine.Text" usage="bound" required="true" />') },
  { name: 'new_Ctl.Stars', manifest: manifest('Stars', '<property name="value" of-type="Whole.None" usage="bound" required="true" />') },
  { name: 'new_Ctl.Grid', manifest: manifest('Grid', '<data-set name="records" />') },
  { name: 'new_Ctl.Bad', manifest: '<manifest><control /></manifest>' },
];

const cell = (id: string, label: string, control: string) =>
  `<cell id="{${id}}"><labels><label description="${label}" languagecode="1033" /></labels>${control}</cell>`;
const field = (logical: string, uniqueId?: string) =>
  `<control id="${logical}" classid="${TEXT}" datafieldname="${logical}"${uniqueId ? ` uniqueid="${uniqueId}"` : ''} />`;
const entry = (formFactor: string, name: string, params: string) =>
  `<customControl formFactor="${formFactor}" name="${name}"><parameters>${params}</parameters></customControl>`;
const description = (forControl: string, column: string, name: string, web: string, phone = web) => `
    <controlDescription forControl="${forControl}">
      <customControl id="${TEXT}"><parameters><datafieldname>${column}</datafieldname></parameters></customControl>
      ${entry('0', name, web)}${entry('1', name, phone)}${entry('2', name, web)}
    </controlDescription>`;

const FORM = `<form>
  <tabs>
    <tab name="general" id="{a0000000-0000-0000-0000-000000000001}" showlabel="true" expanded="true">
      <labels><label description="Summary" languagecode="1033" /></labels>
      <columns>
        <column width="100%">
          <sections>
            <section name="main" id="{a0000000-0000-0000-0000-000000000002}" showlabel="true" columns="1">
              <labels><label description="Main" languagecode="1033" /></labels>
              <rows>
                <row>${cell('c0000000-0000-0000-0000-000000000001', 'Account Name', field('name'))}</row>
                <row>${cell('c0000000-0000-0000-0000-000000000002', 'Main Phone', field('telephone1', PHONE_ID))}</row>
                <row>${cell('c0000000-0000-0000-0000-000000000003', 'Website', field('websiteurl', SITE_ID))}</row>
                <row>${cell('c0000000-0000-0000-0000-000000000004', 'Description', field('description', NOTES_ID))}</row>
                <row>${cell('c0000000-0000-0000-0000-000000000005', 'Contacts', `<control id="Contacts" classid="${CLASS_ID.subgrid}" indicationOfSubgrid="true">
                  <parameters>
                    <ViewId>{a1000000-0000-0000-0000-000000000001}</ViewId>
                    <IsUserView>false</IsUserView>
                    <RelationshipName>contact_customer_accounts</RelationshipName>
                    <TargetEntityType>contact</TargetEntityType>
                    <AutoExpand>Fixed</AutoExpand>
                    <EnableViewPicker>false</EnableViewPicker>
                    <RecordsPerPage>4</RecordsPerPage>
                  </parameters>
                </control>`)}</row>
              </rows>
            </section>
          </sections>
        </column>
      </columns>
    </tab>
  </tabs>
  <controlDescriptions>${description(PHONE_ID, 'telephone1', 'new_Ctl.Box', '<value>telephone1</value><placeholder static="true" type="SingleLine.Text">Call</placeholder>', '<value>telephone1</value>')}${description(SITE_ID, 'websiteurl', 'new_Ctl.Gone', '<value>websiteurl</value>')}${description(NOTES_ID, 'description', 'new_Ctl.Bad', '<value>description</value>')}
  </controlDescriptions>
</form>`;

interface Call { path: string; body?: Record<string, unknown> }

const pathOf = (request: Request) => decodeURIComponent(new URL(request.url).pathname).slice(ROOT.length + 1);

function install(failControls?: string, form = FORM): Call[] {
  const calls: Call[] = [];
  signInAsAdministrator();
  installMetadata([account, contact], []);
  server.use(
    onPath('get', `systemforms(${FORM_ID})`, () => HttpResponse.json({
      systemformid: FORM_ID, name: 'Information', description: '', type: 2, isdefault: true, formactivationstate: 1,
      ismanaged: false, objecttypecode: 'account', formxml: form,
    })),
    onPath('get', 'customcontrols', () => (failControls
      ? HttpResponse.json({ error: { code: '0x80040220', message: failControls } }, { status: 403 })
      : HttpResponse.json({ value: CONTROLS }))),
    onPath('get', 'savedqueries', () => HttpResponse.json({ value: [] })),
    http.patch(`${API}/*`, async ({ request }) => {
      calls.push({ path: pathOf(request), body: (await request.json()) as Record<string, unknown> });
      return new HttpResponse(null, { status: 204 });
    }),
    http.post(`${API}/PublishXml`, () => new HttpResponse(null, { status: 204 })),
  );
  return calls;
}

afterEach(() => sessionStorage.clear());

type User = ReturnType<typeof userEvent.setup>;

const structure = () => screen.getByRole('region', { name: en['forms.canvas.title'] });
const propsCard = () => screen.getByRole('region', { name: en['forms.props.title'] });
const saveButton = () => screen.getByRole('button', { name: en['forms.designer.save'] });
const controlPicker = () => within(propsCard()).getByRole('combobox', { name: en['forms.pcf.control'] });
const optionsOf = (select: HTMLElement) => within(select).getAllByRole('option').map((o) => o.textContent);

async function select(user: User, name: string) {
  render(<App initialEntries={[`/manage/tables/account/forms/${FORM_ID}`]} />);
  // The CI render race (FormDesigner.test.tsx): wait for the canvas.
  await screen.findByRole('region', { name: en['forms.canvas.title'] });
  await user.click(within(structure()).getByRole('button', { name }));
  return propsCard();
}

/** Waits for the list of custom controls in the panel. */
const listed = () => waitFor(() => expect(controlPicker()).toBeEnabled());

async function saved(user: User, calls: Call[]): Promise<Element> {
  await user.click(saveButton());
  await waitFor(() => expect(calls.some((c) => c.path === `systemforms(${FORM_ID})`)).toBe(true));
  return parseXml(String(calls.find((c) => c.path === `systemforms(${FORM_ID})`)!.body?.formxml)).documentElement;
}

const descriptionFor = (form: Element, uniqueId: string) =>
  Array.from(form.getElementsByTagName('controlDescription')).find((d) => d.getAttribute('forControl')?.toLowerCase() === uniqueId.toLowerCase());
const controlById = (form: Element, id: string) => Array.from(form.getElementsByTagName('control')).find((c) => c.getAttribute('id') === id)!;

describe('PcfPanel', { timeout: PAGE_TEST_TIMEOUT }, () => {
  it('a text field offers only the controls with a bound text property', async () => {
    const user = userEvent.setup();
    install();
    await select(user, 'Account Name');
    await listed();
    expect(optionsOf(controlPicker())).toEqual([en['forms.pcf.standard'], 'new_Ctl.Box', 'new_Ctl.Mask']);
    expect(controlPicker()).toHaveValue('');
  });

  it('choosing a control prefills its bound property with the column and writes the description (G5)', async () => {
    const user = userEvent.setup();
    const calls = install();
    const panel = await select(user, 'Account Name');
    await listed();
    await user.selectOptions(controlPicker(), 'new_Ctl.Mask');
    expect(within(panel).getByRole('combobox', { name: 'value' })).toHaveValue('name');
    expect(within(panel).queryByText(en['forms.pcf.ownFactors'])).not.toBeInTheDocument();

    const form = await saved(user, calls);
    const uniqueId = controlById(form, 'name').getAttribute('uniqueid')!;
    expect(uniqueId).toMatch(/^\{[0-9a-f-]{36}\}$/);
    const d = descriptionFor(form, uniqueId)!;
    const entries = Array.from(d.children);
    expect(entries.map((e) => [e.getAttribute('formFactor'), e.getAttribute('name')])).toEqual([
      [null, null], ['0', 'new_Ctl.Mask'], ['1', 'new_Ctl.Mask'], ['2', 'new_Ctl.Mask'],
    ]);
    expect(entries[1].getElementsByTagName('value')[0].textContent).toBe('name');
  });

  it('a required input left empty blocks Save with a message naming it', async () => {
    const user = userEvent.setup();
    const calls = install();
    const panel = await select(user, 'Account Name');
    await listed();
    await user.selectOptions(controlPicker(), 'new_Ctl.Box');
    expect(within(panel).getByRole('textbox', { name: 'limit' })).toHaveValue('100');
    expect(saveButton()).toBeDisabled();
    const message = en['forms.pcf.saveBlocked.item']
      .replace('{cell}', 'Account Name').replace('{control}', 'new_Ctl.Box').replace('{properties}', 'placeholder');
    expect(screen.getByText(en['forms.pcf.saveBlocked'])).toBeInTheDocument();
    expect(screen.getByText(message)).toBeInTheDocument();
    expect(within(panel).getByText(en['forms.pcf.propertyRequired'])).toBeInTheDocument();

    await user.type(within(panel).getByRole('textbox', { name: 'placeholder' }), 'Name here');
    expect(screen.queryByText(message)).not.toBeInTheDocument();
    expect(saveButton()).toBeEnabled();
    const form = await saved(user, calls);
    const d = descriptionFor(form, controlById(form, 'name').getAttribute('uniqueid')!)!;
    const web = Array.from(d.children).find((e) => e.getAttribute('formFactor') === '0')!;
    const placeholder = web.getElementsByTagName('placeholder')[0];
    expect(placeholder.textContent).toBe('Name here');
    expect(placeholder.getAttribute('static')).toBe('true');
    expect(placeholder.getAttribute('type')).toBe('SingleLine.Text');
    expect(web.getElementsByTagName('limit')[0].getAttribute('type')).toBe('Whole.None');
  });

  it('a binding with own phone and tablet settings says so; Use the standard control removes the description', async () => {
    const user = userEvent.setup();
    const calls = install();
    const panel = await select(user, 'Main Phone');
    await listed();
    expect(controlPicker()).toHaveValue('new_Ctl.Box');
    expect(within(panel).getByText(en['forms.pcf.ownFactors'])).toBeInTheDocument();
    expect(within(panel).getByRole('textbox', { name: 'placeholder' })).toHaveValue('Call');
    await user.click(within(panel).getByRole('button', { name: en['forms.pcf.useStandard'] }));
    expect(controlPicker()).toHaveValue('');
    expect(within(panel).queryByText(en['forms.pcf.ownFactors'])).not.toBeInTheDocument();

    const form = await saved(user, calls);
    expect(descriptionFor(form, PHONE_ID)).toBeUndefined();
    // The other descriptions stay.
    expect(descriptionFor(form, SITE_ID)).toBeDefined();
    expect(descriptionFor(form, NOTES_ID)).toBeDefined();
  });

  it('labels a property with its display name from the resx, and shows the name as the hint (A3)', async () => {
    const user = userEvent.setup();
    install();
    const box = manifest('Box', `
      <property name="value" of-type="SingleLine.Text" usage="bound" required="true" />
      <property name="placeholder" display-name-key="Placeholder_Key" of-type="SingleLine.Text" usage="input" required="true" />
      <property name="limit" display-name-key="Limit_Key" of-type="Whole.None" usage="input" required="false" default-value="100" />
      <resources><resx path="strings/Box.1033.resx" version="1.0.0" /></resources>`);
    server.use(
      onPath('get', 'customcontrols', () => HttpResponse.json({ value: [{ name: 'new_Ctl.Box', manifest: box }, ...CONTROLS.slice(1)] })),
      http.get('*/Controls/new_Ctl.Box/strings/Box.1033.resx', () => new HttpResponse(
        '<root><data name="Placeholder_Key" xml:space="preserve"><value>Placeholder text</value></data></root>',
        { headers: { 'Content-Type': 'text/xml' } },
      )),
    );
    const panel = await select(user, 'Main Phone');
    await listed();
    const placeholder = await within(panel).findByRole('textbox', { name: 'Placeholder text' });
    expect(placeholder).toHaveValue('Call');
    expect(placeholder).toHaveAccessibleDescription('placeholder');
    // Without a resx entry the label is the key, and the name is the hint; without a key the label is the name.
    expect(within(panel).getByRole('textbox', { name: 'Limit_Key' })).toHaveAccessibleDescription('limit');
    const value = within(panel).getByRole('combobox', { name: 'value' });
    // No key, so no hint: the label is the name and nothing describes the field.
    expect(value).not.toHaveAttribute('aria-describedby');
    expect(value).toHaveAccessibleDescription('');
  });

  it('a binding to a control that is not installed, or whose manifest fails, shows read-only with the reason', async () => {
    const user = userEvent.setup();
    install();
    let panel = await select(user, 'Website');
    expect(await within(panel).findByText(
      en['forms.pcf.unavailable'].replace('{name}', 'new_Ctl.Gone').replace('{reason}', en['forms.pcf.notInstalled']),
    )).toBeInTheDocument();
    expect(within(panel).queryByRole('combobox', { name: en['forms.pcf.control'] })).not.toBeInTheDocument();
    expect(within(panel).queryByRole('textbox', { name: 'value' })).not.toBeInTheDocument();
    expect(within(panel).getByText('value: websiteurl')).toBeInTheDocument();

    await user.click(within(structure()).getByRole('button', { name: 'Description' }));
    panel = propsCard();
    expect(await within(panel).findByText(
      en['forms.pcf.unavailable'].replace('{name}', 'new_Ctl.Bad').replace('{reason}', 'the manifest has no control with a namespace and a constructor'),
    )).toBeInTheDocument();
    expect(within(panel).queryByRole('combobox', { name: en['forms.pcf.control'] })).not.toBeInTheDocument();
    // No property input: the only text box is the cell's own label field.
    expect(within(panel).getAllByRole('textbox').map((box) => (box as HTMLInputElement).value)).toEqual(['Description']);
    // Opening them changes nothing.
    expect(saveButton()).toBeEnabled();
  });

  it('a subgrid offers only dataset controls', async () => {
    const user = userEvent.setup();
    const calls = install();
    await select(user, 'Contacts (Subgrid)');
    await listed();
    expect(optionsOf(controlPicker())).toEqual([en['forms.pcf.standard'], 'new_Ctl.Grid']);
    await user.selectOptions(controlPicker(), 'new_Ctl.Grid');
    const form = await saved(user, calls);
    const d = descriptionFor(form, controlById(form, 'Contacts').getAttribute('uniqueid')!)!;
    const web = Array.from(d.children).find((e) => e.getAttribute('formFactor') === '0')!;
    const ds = web.getElementsByTagName('data-set')[0];
    expect(ds.getAttribute('name')).toBe('records');
    expect(ds.getElementsByTagName('TargetEntityType')[0].textContent).toBe('contact');
  });

  it('a failed custom control list shows the server text and disables the control picker', async () => {
    const user = userEvent.setup();
    install('No read on customcontrol');
    const panel = await select(user, 'Account Name');
    const prefix = en['forms.controls.loadFailed'].replace('{detail}', '');
    expect(await within(panel).findByText((text) => text.startsWith(prefix) && text.includes('No read on customcontrol'))).toBeInTheDocument();
    expect(controlPicker()).toBeDisabled();
  });

  it('a binding whose name differs in case from the installed control shows that control (H3)', async () => {
    const user = userEvent.setup();
    const calls = install(undefined, FORM.replaceAll('name="new_Ctl.Box"', 'name="NEW_ctl.box"'));
    const panel = await select(user, 'Main Phone');
    await listed();
    expect(controlPicker()).toHaveValue('new_Ctl.Box');
    expect(within(panel).getByRole('textbox', { name: 'placeholder' })).toBeEnabled();
    expect(within(panel).getByRole('textbox', { name: 'placeholder' })).toHaveValue('Call');
    const form = await saved(user, calls);
    const names = Array.from(descriptionFor(form, PHONE_ID)!.children).map((e) => e.getAttribute('name'));
    expect(names).toEqual([null, 'NEW_ctl.box', 'NEW_ctl.box', 'NEW_ctl.box']);
  });

  it('an installed control that does not fit the cell is read-only with the reason (H4)', async () => {
    const user = userEvent.setup();
    const calls = install(undefined, FORM.replaceAll('telephone1', 'numberofemployees').replace('description="Main Phone"', 'description="Employees"'));
    const panel = await select(user, 'Employees');
    expect(await within(panel).findByText(
      en['forms.pcf.unavailable'].replace('{name}', 'new_Ctl.Box').replace('{reason}', en['forms.pcf.unfit']),
    )).toBeInTheDocument();
    expect(within(panel).queryByRole('combobox', { name: en['forms.pcf.control'] })).not.toBeInTheDocument();
    expect(within(panel).queryByRole('textbox', { name: 'placeholder' })).not.toBeInTheDocument();
    expect(within(panel).queryByRole('textbox', { name: 'limit' })).not.toBeInTheDocument();
    expect(within(panel).queryByRole('combobox', { name: 'value' })).not.toBeInTheDocument();
    expect(saveButton()).toBeEnabled();
    await user.click(within(panel).getByRole('button', { name: en['forms.pcf.useStandard'] }));
    await listed();
    expect(controlPicker()).toHaveValue('');
    expect(saveButton()).toBeEnabled();
    const form = await saved(user, calls);
    expect(descriptionFor(form, PHONE_ID)).toBeUndefined();
  });
});
