import { render, screen, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { server } from '../../test/server';
import { App } from '../../shell/App';
import { en } from '../../shell/i18n/en';
import { parseXml } from '../../parsers/xml';
import { CLASS_ID } from '../../parsers/controlClass';
import { API, onPath, signInAsAdministrator } from '../security/testSupport';
import { installMetadata, rawAttr, rawEntity, rawOneToMany } from '../tables/tableTestSupport';

/** Shared setup of the subgrid, quick view and web resource UI tests (G2–G4): an account form and its lists. */

export const FORM_ID = '11111111-1111-1111-1111-111111111111';
export const CONTACT_VIEW = 'a1000000-0000-0000-0000-000000000001';
export const CONTACT_DEFAULT_VIEW = 'a1000000-0000-0000-0000-000000000002';
export const ACCOUNT_VIEW = 'a1000000-0000-0000-0000-000000000003';
export const CONTACT_CARD = 'f1000000-0000-0000-0000-000000000001';
export const ACCOUNT_CARD = 'f1000000-0000-0000-0000-000000000002';
export const MAP_WR = 'e1000000-0000-0000-0000-000000000001';
export const LOGO_WR = 'e1000000-0000-0000-0000-000000000002';
/**
 * The timeout of the tests that render the whole designer and take over 2 s locally (drags, Save and Save as, the Modern
 * look): CI runners are about twice as slow, so the 5 s default is too close.
 */
export const PAGE_TEST_TIMEOUT = 15_000;
const ROOT = '/api/data/v9.2';
const TEXT = '{4273EDBD-AC1D-40d3-9FB2-095C621B552D}';

const account = rawEntity(
  'account',
  'accounts',
  [
    rawAttr('accountid', 'Uniqueidentifier', { IsPrimaryId: true }),
    rawAttr('name', 'String', { IsPrimaryName: true, display: 'Account Name' }),
    rawAttr('primarycontactid', 'Lookup', { display: 'Primary Contact', Targets: ['contact'] }),
    rawAttr('parentid', 'Lookup', { display: 'Parent', Targets: ['account', 'contact'] }),
  ],
  { PrimaryIdAttribute: 'accountid', PrimaryNameAttribute: 'name', display: 'Account' },
);
const contact = rawEntity('contact', 'contacts', [rawAttr('contactid', 'Uniqueidentifier', { IsPrimaryId: true })], { display: 'Contact' });

const cell = (id: string, label: string, control: string) =>
  `<cell id="{${id}}"><labels><label description="${label}" languagecode="1033" /></labels>${control}</cell>`;

/** The form the tests open: a field section, and a section with one cell of each control kind. */
export const FORM = `<form>
  <tabs>
    <tab name="general" id="{a0000000-0000-0000-0000-000000000001}" showlabel="true" expanded="true">
      <labels><label description="Summary" languagecode="1033" /></labels>
      <columns>
        <column width="100%">
          <sections>
            <section name="main" id="{a0000000-0000-0000-0000-000000000002}" showlabel="true" columns="2">
              <labels><label description="Main" languagecode="1033" /></labels>
              <rows>
                <row>${cell('c0000000-0000-0000-0000-000000000001', 'Account Name', `<control id="name" classid="${TEXT}" datafieldname="name" />`)}</row>
              </rows>
            </section>
            <section name="controls" id="{a0000000-0000-0000-0000-000000000003}" showlabel="true" columns="1">
              <labels><label description="Controls" languagecode="1033" /></labels>
              <rows>
                <row>${cell('c0000000-0000-0000-0000-000000000002', 'Contacts', `<control id="Contacts" classid="${CLASS_ID.subgrid}" indicationOfSubgrid="true">
                  <parameters>
                    <ViewId>{${CONTACT_VIEW.toUpperCase()}}</ViewId>
                    <IsUserView>false</IsUserView>
                    <RelationshipName>contact_customer_accounts</RelationshipName>
                    <TargetEntityType>contact</TargetEntityType>
                    <AutoExpand>Fixed</AutoExpand>
                    <EnableViewPicker>false</EnableViewPicker>
                    <RecordsPerPage>4</RecordsPerPage>
                  </parameters>
                </control>`)}</row>
                <row>${cell('c0000000-0000-0000-0000-000000000003', 'Parent card', `<control id="parent_qv" classid="${CLASS_ID.quickView}" datafieldname="parentid" uniqueid="{b0000000-0000-0000-0000-000000000001}">
                  <parameters>
                    <QuickForms>&lt;QuickForms&gt;&lt;QuickFormIds&gt;&lt;QuickFormId entityname="account"&gt;{${ACCOUNT_CARD}}&lt;/QuickFormId&gt;&lt;/QuickFormIds&gt;&lt;/QuickForms&gt;</QuickForms>
                  </parameters>
                </control>`)}</row>
                <row>${cell('c0000000-0000-0000-0000-000000000004', 'Map', `<control id="WebResource_map" classid="${CLASS_ID.webResource}">
                  <parameters>
                    <Url>new_/pages/map.html</Url>
                    <PassParameters>false</PassParameters>
                    <Security>true</Security>
                    <Scrolling>auto</Scrolling>
                    <Border>true</Border>
                    <WebResourceId>{${MAP_WR}}</WebResourceId>
                    <Data></Data>
                  </parameters>
                </control>`)}</row>
                <row>${cell('c0000000-0000-0000-0000-000000000005', 'Site', `<control id="IFRAME_site" classid="${CLASS_ID.iframe}"><parameters><Url>https://example.com</Url></parameters></control>`)}</row>
                <row>${cell('c0000000-0000-0000-0000-000000000006', 'Timeline', `<control id="notescontrol" classid="${CLASS_ID.timeline}" />`)}</row>
              </rows>
            </section>
          </sections>
        </column>
      </columns>
    </tab>
  </tabs>
</form>`;

export interface Call { method: string; path: string; body?: Record<string, unknown> }

const pathOf = (request: Request) => decodeURIComponent(new URL(request.url).pathname).slice(ROOT.length + 1);
const filterOf = (request: Request) => new URL(request.url).searchParams.get('$filter') ?? '';
const refused = (message: string) => HttpResponse.json({ error: { code: '0x80040220', message } }, { status: 403 });

export interface Failures { views?: string; forms?: string; webResources?: string; relationships?: string; entities?: string }

/**
 * Signs in, answers the metadata, the form, its lists (views, quick view forms, web resources) and records every write.
 * A list named in `fail` answers 403 with that text. The form has the type `formType` (2, Main, by default).
 */
export function installControls(fail: Failures = {}, formType = 2): Call[] {
  const calls: Call[] = [];
  signInAsAdministrator();
  installMetadata([account, contact], [
    rawOneToMany('contact_customer_accounts', 'contact', 'parentcustomerid', 'account'),
    rawOneToMany('account_primary_contact', 'account', 'primarycontactid', 'contact'),
  ]);
  if (fail.entities) server.use(http.get(`${API}/EntityDefinitions`, () => refused(fail.entities!)));
  if (fail.relationships) server.use(http.get(`${API}/RelationshipDefinitions`, () => refused(fail.relationships!)));
  server.use(
    onPath('get', `systemforms(${FORM_ID})`, () => HttpResponse.json({
      systemformid: FORM_ID, name: 'Information', description: '', type: formType, isdefault: true, formactivationstate: 1,
      ismanaged: false, objecttypecode: 'account', formxml: FORM,
    })),
    onPath('get', 'systemforms', ({ request }) => {
      if (fail.forms) return refused(fail.forms);
      const filter = filterOf(request);
      if (filter.includes("'contact'")) {
        return HttpResponse.json({ value: [
          { systemformid: 'f1000000-0000-0000-0000-0000000000ff', name: 'Contact main', type: 2, formactivationstate: 1 },
          { systemformid: CONTACT_CARD, name: 'Contact card', type: 6, formactivationstate: 1 },
        ] });
      }
      return HttpResponse.json({ value: [{ systemformid: ACCOUNT_CARD, name: 'Account card', type: 6, formactivationstate: 1 }] });
    }),
    onPath('get', 'savedqueries', ({ request }) => {
      if (fail.views) return refused(fail.views);
      const filter = filterOf(request);
      if (filter.includes("'contact'")) {
        return HttpResponse.json({ value: [
          { savedqueryid: CONTACT_VIEW, name: 'Active Contacts', isdefault: false },
          { savedqueryid: CONTACT_DEFAULT_VIEW, name: 'My Contacts', isdefault: true },
        ] });
      }
      return HttpResponse.json({ value: [{ savedqueryid: ACCOUNT_VIEW, name: 'Active Accounts', isdefault: true }] });
    }),
    onPath('get', 'webresourceset', ({ request }) => {
      if (!filterOf(request).includes('webresourcetype')) return HttpResponse.json({ value: [] });
      if (fail.webResources) return refused(fail.webResources);
      return HttpResponse.json({ value: [
        { webresourceid: LOGO_WR, name: 'new_logo.png', displayname: 'Logo', webresourcetype: 5 },
        { webresourceid: MAP_WR, name: 'new_/pages/map.html', displayname: 'Map', webresourcetype: 1 },
      ] });
    }),
    http.patch(`${API}/*`, async ({ request }) => {
      calls.push({ method: 'PATCH', path: pathOf(request), body: (await request.json()) as Record<string, unknown> });
      return new HttpResponse(null, { status: 204 });
    }),
    http.post(`${API}/PublishXml`, () => new HttpResponse(null, { status: 204 })),
    // The custom controls a selected field or subgrid can use (G5): none installed.
    onPath('get', 'customcontrols', () => HttpResponse.json({ value: [] })),
  );
  return calls;
}

export function openForm() {
  render(<App initialEntries={[`/manage/tables/account/forms/${FORM_ID}`]} />);
}

/** Waits for the canvas (the CI render race, see FormDesigner.test.tsx) and returns it. */
export const canvasReady = () => screen.findByRole('region', { name: en['forms.canvas.title'] });
export const structure = () => screen.getByRole('region', { name: en['forms.canvas.title'] });
export const propsCard = () => screen.getByRole('region', { name: en['forms.props.title'] });
export const section = (label: string) => within(structure()).getByRole('region', { name: `Section ${label}` });
export const saveButton = () => screen.getByRole('button', { name: en['forms.designer.save'] });

/** The FormXML of the last PATCH of the form. */
export function savedForm(calls: Call[]): Element {
  const patch = calls.filter((c) => c.path === `systemforms(${FORM_ID})`).pop();
  if (!patch) throw new Error('no PATCH of the form');
  return parseXml(String(patch.body?.formxml)).documentElement;
}

export const controlById = (form: Element, id: string) =>
  Array.from(form.getElementsByTagName('control')).find((c) => c.getAttribute('id') === id);

/** The `<parameters>` children of a control as [name, text] pairs, in order. */
export const paramsOf = (control: Element | undefined) =>
  Array.from(control?.getElementsByTagName('parameters')[0]?.children ?? []).map((p) => [p.tagName, p.textContent ?? '']);
