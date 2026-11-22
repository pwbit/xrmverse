import { act, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { createElement } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { onPath } from '../admin/security/testSupport';
import { mockApps } from '../apps/testSupport';
import type { AppModel } from '../model/app';
import { Providers } from '../renderers/classic/form/formTestSupport';
import { NotificationsProvider } from '../shell/notify/Notifications';
import { XrmApi } from '../shell/api/client';
import { server } from '../test/server';
import { createDialogHost, DialogHostView, type DialogHostStore } from './DialogHost';
import type { FormContext } from './formContext';
import type { CurrentUser, OrgSettings } from './globalContext';
import { createXrm, type Xrm } from './xrm';

const USER = 'aaaaaaaa-0000-0000-0000-000000000001';
const ROLE = 'cccccccc-0000-0000-0000-000000000001';
const ORG = 'eeeeeeee-0000-0000-0000-000000000001';
const CURRENCY = 'ffffffff-0000-0000-0000-000000000001';
const NODE = '33333333-0000-0000-0000-000000000001';

const app: AppModel = { id: 'aaaaaaaa-0000-0000-0000-0000000000a1', uniqueName: 'op365_Projects', name: 'Projects', clientType: 4, components: { entities: [], views: [], forms: [] } };
const user: CurrentUser = {
  id: USER,
  name: 'Ursula User',
  lcid: 1040,
  roles: [{ id: ROLE, name: 'Project Manager' }],
  timeZone: 'Europe/Rome',
  formats: { dateFormat: 'dd/MM/yyyy', timeFormat: 'HH:mm', dateSeparator: '/', timeSeparator: '.', am: 'AM', pm: 'PM' },
};
const org: OrgSettings = { id: ORG, uniqueName: 'contoso', languageId: 1033, baseCurrencyId: CURRENCY, isAutoSaveEnabled: true };

function makeXrm(currentForm: () => FormContext | null = () => null): { xrm: Xrm; dialogs: DialogHostStore; api: XrmApi } {
  const api = new XrmApi({ getToken: async () => 'tok', onUnauthorized: () => undefined });
  const dialogs = createDialogHost();
  const xrm = createXrm({ api, app, user, org, navigate: () => undefined, dialogs, currentForm });
  return { xrm, dialogs, api };
}

type Rec = Record<string, unknown>;
const utility = (xrm: Xrm) => xrm.Utility as unknown as Record<string, (...a: unknown[]) => unknown>;

afterEach(() => window.history.replaceState(null, '', '/'));

/** Runs a call that opens a dialog inside act(): the dialog host re-renders as the call returns. */
function inAct<T>(call: () => T): T {
  let result!: T;
  act(() => {
    result = call();
  });
  return result;
}


describe('Xrm.Utility', () => {
  it('global_context_user_and_org_settings', async () => {
    const { xrm } = makeXrm();
    const gc = utility(xrm).getGlobalContext!() as Rec & { userSettings: Rec; organizationSettings: Rec };
    expect(utility(xrm).getGlobalContext!()).toBe(gc);
    const us = gc.userSettings as Rec & { roles: { get(): unknown[]; getLength(): number }; getTimeZoneOffsetMinutes(): number };
    expect(us.userId).toBe(`{${USER.toUpperCase()}}`);
    expect(us.userName).toBe('Ursula User');
    expect(us.languageId).toBe(1040);
    expect(us.isRTL).toBe(false);
    expect(us.securityRoles).toEqual([ROLE]);
    expect(us.roles.getLength()).toBe(1);
    expect(us.roles.get()).toEqual([{ id: ROLE, name: 'Project Manager' }]);
    // #131: the user's zone (Rome) now: 60 in winter, 120 in summer.
    expect([60, 120]).toContain(us.getTimeZoneOffsetMinutes());
    vi.useFakeTimers({ toFake: ['Date'] });
    try {
      vi.setSystemTime(new Date('2026-07-01T12:00:00Z'));
      expect(us.getTimeZoneOffsetMinutes()).toBe(120);
      vi.setSystemTime(new Date('2026-01-15T12:00:00Z'));
      expect(us.getTimeZoneOffsetMinutes()).toBe(60);
    } finally {
      vi.useRealTimers();
    }
    const info = us.dateFormattingInfo as Rec;
    expect([info.ShortDatePattern, info.ShortTimePattern, info.DateSeparator, info.TimeSeparator, info.AMDesignator, info.PMDesignator]).toEqual([
      'dd/MM/yyyy',
      'HH:mm',
      '/',
      '.',
      'AM',
      'PM',
    ]);
    expect(info.FirstDayOfWeek).toBe(0);
    expect((info.MonthNames as string[]).length).toBe(13);
    expect((info.MonthNames as string[])[0]).toBe('January');
    expect((info.MonthNames as string[])[12]).toBe('');
    expect((info.AbbreviatedMonthNames as string[])[11]).toBe('Dec');
    expect(info.DayNames).toEqual(['Sunday', 'Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday']);
    expect((info.AbbreviatedDayNames as string[])[0]).toBe('Sun');
    expect(() => info.LongDatePattern).toThrow('Xrm.Utility.getGlobalContext().userSettings.dateFormattingInfo.LongDatePattern is not available in this version');
    const os = gc.organizationSettings;
    expect(os.organizationId).toBe(ORG);
    expect(os.uniqueName).toBe('contoso');
    expect(os.languageId).toBe(1033);
    expect(os.baseCurrencyId).toBe(CURRENCY);
    expect(os.defaultCountryCode).toBe('');
    expect(os.isAutoSaveEnabled).toBe(true);
    // Ruling C9.
    expect((gc.getVersion as () => string)()).toBe('9.2.0.0');
    expect((gc.getCurrentAppUrl as () => string)()).toBe(`${location.origin}/main.aspx?appid=${app.id}`);
    await expect((gc.getCurrentAppName as () => Promise<string>)()).resolves.toBe('Projects');
    await expect((gc.getCurrentAppProperties as () => Promise<Rec>)()).resolves.toMatchObject({ appId: app.id, displayName: 'Projects', uniqueName: 'op365_Projects' });
    expect((gc.prependOrgName as (p: string) => string)('/WebResources/x.js')).toBe('/WebResources/x.js');
    window.history.replaceState(null, '', '/main.aspx?appid=1&pagetype=entityrecord&etn=account');
    expect((gc.getQueryStringParameters as () => Rec)()).toEqual({ appid: '1', pagetype: 'entityrecord', etn: 'account' });
    expect(() => (gc.isOnPremises as () => boolean)()).toThrow('not available in this version');
  });

  it('get_client_url_is_origin', () => {
    const { xrm } = makeXrm();
    const gc = utility(xrm).getGlobalContext!() as { getClientUrl(): string };
    expect(gc.getClientUrl()).toBe(location.origin);
    expect(gc.getClientUrl()).not.toMatch(/\/$/);
  });

  it('progress_indicator_nests_by_count', async () => {
    const { xrm, dialogs } = makeXrm();
    render(createElement(DialogHostView, { host: dialogs }));
    act(() => void utility(xrm).showProgressIndicator!('Saving baseline...'));
    act(() => void utility(xrm).showProgressIndicator!('Working...'));
    expect(screen.getByRole('alertdialog', { name: 'Working...' })).toBeInTheDocument();
    act(() => void utility(xrm).closeProgressIndicator!());
    expect(screen.getByRole('alertdialog')).toBeInTheDocument();
    act(() => void utility(xrm).closeProgressIndicator!());
    expect(screen.queryByRole('alertdialog')).toBeNull();
    // One close too many does nothing; the next show opens it again.
    act(() => void utility(xrm).closeProgressIndicator!());
    act(() => void utility(xrm).showProgressIndicator!('Again'));
    expect(screen.getByRole('alertdialog', { name: 'Again' })).toBeInTheDocument();
  });

  it('lookup_objects_returns_selection', async () => {
    mockApps();
    const searches: URLSearchParams[] = [];
    server.use(
      onPath('get', "EntityDefinitions(LogicalName='op365_wbsnode')", () =>
        HttpResponse.json({ EntitySetName: 'op365_wbsnodes', PrimaryIdAttribute: 'op365_wbsnodeid', PrimaryNameAttribute: 'op365_name' }),
      ),
      http.get('*/api/data/v9.2/op365_wbsnodes', ({ request }) => {
        searches.push(new URL(request.url).searchParams);
        return HttpResponse.json({ value: [{ op365_wbsnodeid: NODE, op365_name: 'Design' }] });
      }),
    );
    const { xrm, dialogs } = makeXrm();
    render(createElement(Providers, null, createElement(NotificationsProvider, null, createElement(DialogHostView, { host: dialogs }))));
    const picked = inAct(() => utility(xrm).lookupObjects!({
      entityTypes: ['op365_wbsnode'],
      filters: [{ entityLogicalName: 'op365_wbsnode', filterXml: '<filter><condition attribute="op365_projectid" operator="eq" value="x"/></filter>' }],
    })) as Promise<unknown>;
    await userEvent.click(await screen.findByRole('option', { name: 'Design' }));
    await userEvent.click(screen.getByRole('button', { name: 'Add' }));
    await expect(picked).resolves.toEqual([{ id: `{${NODE.toUpperCase()}}`, name: 'Design', entityType: 'op365_wbsnode' }]);
    // The filter of the options narrows the search, as a lookup's PreSearch filter does.
    expect(searches[0]!.get('fetchXml')).toContain('op365_projectid');

    // Cancel is not a failure: an empty selection.
    const cancelled = inAct(() => utility(xrm).lookupObjects!({ entityTypes: ['op365_wbsnode'] })) as Promise<unknown>;
    await userEvent.click(await screen.findByRole('button', { name: 'Cancel' }));
    await expect(cancelled).resolves.toEqual([]);
    await expect(utility(xrm).lookupObjects!({ entityTypes: [] }) as Promise<unknown>).rejects.toThrow('entityTypes');
  });

  it('lookup_objects_views_and_search_text', async () => {
    mockApps();
    const VIEW = '44444444-0000-0000-0000-000000000001';
    const JOINED = '44444444-0000-0000-0000-000000000002';
    const searches: URLSearchParams[] = [];
    server.use(
      onPath('get', "EntityDefinitions(LogicalName='op365_wbsnode')", () =>
        HttpResponse.json({ EntitySetName: 'op365_wbsnodes', PrimaryIdAttribute: 'op365_wbsnodeid', PrimaryNameAttribute: 'op365_name' }),
      ),
      onPath('get', `savedqueries(${VIEW})`, () =>
        HttpResponse.json({
          savedqueryid: VIEW,
          name: 'Open nodes',
          returnedtypecode: 'op365_wbsnode',
          fetchxml: '<fetch><entity name="op365_wbsnode"><attribute name="op365_name"/><filter type="and"><condition attribute="statecode" operator="eq" value="0"/></filter></entity></fetch>',
        }),
      ),
      onPath('get', `savedqueries(${JOINED})`, () =>
        HttpResponse.json({
          savedqueryid: JOINED,
          name: 'Joined',
          returnedtypecode: 'op365_wbsnode',
          fetchxml: '<fetch><entity name="op365_wbsnode"><link-entity name="op365_project" from="op365_projectid" to="op365_projectid"><filter><condition attribute="statecode" operator="eq" value="0"/></filter></link-entity></entity></fetch>',
        }),
      ),
      http.get('*/api/data/v9.2/op365_wbsnodes', ({ request }) => {
        searches.push(new URL(request.url).searchParams);
        return HttpResponse.json({ value: [{ op365_wbsnodeid: NODE, op365_name: 'Design' }] });
      }),
    );
    const { xrm, dialogs } = makeXrm();
    render(createElement(Providers, null, createElement(NotificationsProvider, null, createElement(DialogHostView, { host: dialogs }))));
    const picked = inAct(() => utility(xrm).lookupObjects!({ entityTypes: ['op365_wbsnode'], defaultViewId: `{${VIEW.toUpperCase()}}`, viewIds: [VIEW], searchText: 'Des' })) as Promise<unknown>;
    // The search opens on the default view, with the search text, and only the view's rows are offered.
    expect(await screen.findByRole('combobox', { name: 'View' })).toHaveValue(VIEW);
    expect(await screen.findByRole('textbox')).toHaveValue('Des');
    await userEvent.click(await screen.findByRole('option', { name: 'Design' }));
    const fetchXml = searches.at(-1)!.get('fetchXml')!;
    expect(fetchXml).toContain('statecode');
    expect(fetchXml).toContain('%Des%');
    await userEvent.click(screen.getByRole('button', { name: 'Add' }));
    await expect(picked).resolves.toHaveLength(1);
    // A view that filters through related tables cannot be applied to the search: refused, no dialog.
    await expect(utility(xrm).lookupObjects!({ entityTypes: ['op365_wbsnode'], viewIds: [JOINED] }) as Promise<unknown>).rejects.toThrow(
      'Xrm.Utility.lookupObjects with a view that filters on related tables is not available in this version (stage after UI-1).',
    );
    // A view of a table the lookup does not offer: refused.
    await expect(utility(xrm).lookupObjects!({ entityTypes: ['account'], viewIds: [VIEW] }) as Promise<unknown>).rejects.toThrow('not in entityTypes');
  });

  it('get_entity_metadata_with_attributes', async () => {
    mockApps();
    const label = (en: string, it: string) => ({
      LocalizedLabels: [
        { Label: en, LanguageCode: 1033 },
        { Label: it, LanguageCode: 1040 },
      ],
      UserLocalizedLabel: { Label: en, LanguageCode: 1033 },
    });
    const option = (value: number, en: string, extra: Record<string, number> = {}) => ({ Value: value, Label: label(en, en), ...extra });
    server.use(
      onPath('get', "EntityDefinitions(LogicalName='account')", () =>
        HttpResponse.json({
          LogicalName: 'account',
          EntitySetName: 'accounts',
          DisplayName: label('Account', 'Conto'),
          IsCustomizable: { Value: true, CanBeChanged: false },
          ObjectTypeCode: 1,
          OwnershipType: 'UserOwned',
          PrimaryIdAttribute: 'accountid',
          Privileges: [{ Name: 'prvReadAccount', PrivilegeId: 'p1', PrivilegeType: 'Read', CanBeBasic: true }],
          Attributes: [
            { LogicalName: 'name', AttributeType: 'String', DisplayName: label('Account Name', 'Nome conto') },
            { LogicalName: 'telephone1', AttributeType: 'String', DisplayName: label('Phone', 'Telefono') },
            { LogicalName: 'industrycode', AttributeType: 'Picklist', DisplayName: label('Industry', 'Settore'), OptionSet: { Options: [option(1, 'Accounting'), option(2, 'Agriculture')] } },
            { LogicalName: 'statecode', AttributeType: 'State', DisplayName: label('Status', 'Stato'), OptionSet: { Options: [option(0, 'Active', { DefaultStatus: 1 }), option(1, 'Inactive', { DefaultStatus: 2 })] } },
            { LogicalName: 'statuscode', AttributeType: 'Status', DisplayName: label('Status Reason', 'Motivo'), OptionSet: { Options: [option(1, 'Active', { State: 0 }), option(2, 'Inactive', { State: 1 }), option(3, 'Closed', { State: 1 })] } },
            { LogicalName: 'donotemail', AttributeType: 'Boolean', DisplayName: label('Do not email', 'Non inviare'), DefaultValue: false, OptionSet: { FalseOption: option(0, 'Allow'), TrueOption: option(1, 'Do Not Allow') } },
          ],
        }),
      ),
    );
    const { xrm } = makeXrm();
    type Attr = Rec & { getDefaultStatus(s: number): number; getStatusValuesForState(s: number): number[]; getState(s: number): number };
    const meta = (await utility(xrm).getEntityMetadata!('account', ['name', 'industrycode', 'statecode', 'statuscode', 'donotemail'])) as Rec & {
      Attributes: { get(name?: string | number): Attr | Attr[] | null; getLength(): number };
    };
    // Learn's shape: labels as strings in the user's language (1040 here), managed properties as values, a collection of columns.
    expect(meta.EntitySetName).toBe('accounts');
    expect(meta.DisplayName).toBe('Conto');
    expect(meta.IsCustomizable).toBe(true);
    expect(meta.ObjectTypeCode).toBe(1);
    expect(meta.Privileges).toEqual([{ Name: 'prvReadAccount', PrivilegeId: 'p1', PrivilegeType: 2, CanBeBasic: true }]);
    expect(meta.Attributes.getLength()).toBe(5);
    const name = meta.Attributes.get('name') as Attr;
    expect(name).toEqual({ AttributeType: 14, DisplayName: 'Nome conto', EntityLogicalName: 'account', LogicalName: 'name' });
    expect((meta.Attributes.get('industrycode') as Attr).OptionSet).toEqual({ 1: 'Accounting', 2: 'Agriculture' });
    const state = meta.Attributes.get('statecode') as Attr;
    expect(state.getDefaultStatus(1)).toBe(2);
    expect(state.getStatusValuesForState(1)).toEqual([2, 3]);
    expect((meta.Attributes.get('statuscode') as Attr).getState(3)).toBe(1);
    expect((meta.Attributes.get('donotemail') as Attr).DefaultFormValue).toBe(false);
    expect((meta.Attributes.get('donotemail') as Attr).OptionSet).toEqual({ 0: 'Allow', 1: 'Do Not Allow' });
    expect(meta.Attributes.get('telephone1')).toBeNull();
    // A property Learn lists that the server's definitions do not give: "not available", never a value of another type.
    expect(() => meta.EntityColor).toThrow('Xrm.Utility.getEntityMetadata().EntityColor is not available in this version (stage after UI-1).');
    const bare = (await utility(xrm).getEntityMetadata!('account')) as { Attributes: { getLength(): number } };
    expect(bare.Attributes.getLength()).toBe(0);
    expect(() => utility(xrm).refreshParentGrid!({})).toThrow('Xrm.Utility.refreshParentGrid is not available in this version (stage after UI-1).');
  });

  it('xrm_page_is_current_form_context', async () => {
    const fc = { data: { entity: {} }, ui: {}, getAttribute: () => null, getControl: () => null } as unknown as FormContext;
    let current: FormContext | null = fc;
    const { xrm } = makeXrm(() => current);
    const page = xrm.Page as unknown as Rec;
    expect(page.data).toBe(fc.data);
    expect(page.getAttribute).toBe(fc.getAttribute);
    // Xrm.Page.context is the global context.
    expect(page.context).toBe(utility(xrm).getGlobalContext!());
    expect(xrm.Page).toBe(page);
    current = null;
    expect(() => xrm.Page).toThrow('Xrm.Page is available only while a form is open.');
  });
});
