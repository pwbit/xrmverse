import { http, HttpResponse } from 'msw';
import { describe, expect, it, vi } from 'vitest';
import { XrmApi } from '../../shell/api/client';
import { server } from '../../test/server';
import { loadColumnKinds } from './columnKinds';

const api = () => new XrmApi({ getToken: async () => 'tok', onUnauthorized: () => {} });
const label = (text: string, code = 1033) => ({ LocalizedLabels: [{ Label: text, LanguageCode: code }], UserLocalizedLabel: { Label: text, LanguageCode: code } });
const labels = (...l: [string, number][]) => ({ LocalizedLabels: l.map(([Label, LanguageCode]) => ({ Label, LanguageCode })), UserLocalizedLabel: null });
const TYPE = 'Microsoft.Dynamics.CRM.';

function mockMetadata(): string[] {
  const seen: string[] = [];
  server.use(
    http.get('*/api/data/v9.2/*', ({ request }) => {
      const url = new URL(request.url);
      const path = decodeURIComponent(url.pathname.replace(/^.*\/api\/data\/v9\.2\//, ''));
      const search = decodeURIComponent(url.search.slice(1));
      seen.push(`${path}?${search}`);
      const base = "EntityDefinitions(LogicalName='account')/Attributes";
      if (path === base) {
        // Like the server, the list ignores $filter and returns every attribute.
        const all = [
          { LogicalName: 'statuscode', AttributeType: 'Status' },
          { LogicalName: 'statecode', AttributeType: 'State' },
          { LogicalName: 'industrycode', AttributeType: 'Picklist' },
          { LogicalName: 'donotemail', AttributeType: 'Boolean' },
          { LogicalName: 'createdon', AttributeType: 'DateTime' },
          { LogicalName: 'birthday', AttributeType: 'DateTime' },
          { LogicalName: 'wallclock', AttributeType: 'DateTime' },
          { LogicalName: 'description', AttributeType: 'Memo' },
          { LogicalName: 'tags', AttributeType: 'Virtual', AttributeTypeName: { Value: 'MultiSelectPicklistType' } },
        ];
        return HttpResponse.json({ value: all });
      }
      if (path === `${base}(LogicalName='statuscode')/${TYPE}StatusAttributeMetadata`) {
        return HttpResponse.json({
          LogicalName: 'statuscode',
          OptionSet: { Options: [{ Value: 1, Label: label('Active'), State: 0 }, { Value: 2, Label: label('Inactive'), State: 1 }] },
        });
      }
      if (path === `${base}(LogicalName='statecode')/${TYPE}StateAttributeMetadata`) {
        return HttpResponse.json({ OptionSet: { Options: [{ Value: 0, Label: label('Active') }, { Value: 1, Label: label('Inactive') }] } });
      }
      if (path === `${base}(LogicalName='industrycode')/${TYPE}PicklistAttributeMetadata`) {
        return HttpResponse.json({
          OptionSet: null,
          GlobalOptionSet: { Name: 'xv_industry', Options: [{ Value: 10, Label: labels(['Hardware', 1033], ['Hardware IT', 1040]), Color: '#0078D4' }, { Value: 11, Label: label('Software') }] },
        });
      }
      if (path === `${base}(LogicalName='tags')/${TYPE}MultiSelectPicklistAttributeMetadata`) {
        return HttpResponse.json({
          OptionSet: { Options: [{ Value: 5, Label: label('Red'), Color: '#FF0000' }, { Value: 6, Label: label('Plain'), Color: null }] },
          GlobalOptionSet: null,
        });
      }
      if (path === `${base}(LogicalName='donotemail')/${TYPE}BooleanAttributeMetadata`) {
        return HttpResponse.json({ OptionSet: { TrueOption: { Value: 1, Label: label('Yes') }, FalseOption: { Value: 0, Label: label('No') } } });
      }
      if (path === `${base}(LogicalName='createdon')/${TYPE}DateTimeAttributeMetadata`) {
        return HttpResponse.json({ DateTimeBehavior: { Value: 'UserLocal' }, Format: 'DateAndTime' });
      }
      if (path === `${base}(LogicalName='birthday')/${TYPE}DateTimeAttributeMetadata`) {
        return HttpResponse.json({ DateTimeBehavior: { Value: 'DateOnly' }, Format: 'DateOnly' });
      }
      if (path === `${base}(LogicalName='wallclock')/${TYPE}DateTimeAttributeMetadata`) {
        return HttpResponse.json({ DateTimeBehavior: { Value: 'TimeZoneIndependent' }, Format: 'DateAndTime' });
      }
      return HttpResponse.json({ error: { message: `unexpected ${path}` } }, { status: 404 });
    }),
  );
  return seen;
}

describe('loadColumnKinds: colours', () => {
  it('carries colour and the global name, and asks for Color', async () => {
    const seen = mockMetadata();
    const kinds = await loadColumnKinds(api(), 'account', ['industrycode'], 1033);
    expect(kinds.industrycode).toEqual({
      kind: 'choice',
      type: 'Picklist',
      globalName: 'xv_industry',
      options: [{ value: 10, label: 'Hardware', color: '#0078D4' }, { value: 11, label: 'Software' }],
    });
    const request = seen.find((u) => u.includes('PicklistAttributeMetadata'))!;
    expect(request).toContain('$expand=OptionSet,GlobalOptionSet');
  });

  it('reads a multi-select column as a MultiSelectPicklist choice', async () => {
    mockMetadata();
    const kinds = await loadColumnKinds(api(), 'account', ['tags'], 1033);
    expect(kinds.tags).toEqual({
      kind: 'choice',
      type: 'MultiSelectPicklist',
      options: [{ value: 5, label: 'Red', color: '#FF0000' }, { value: 6, label: 'Plain' }],
    });
  });
});

describe('loadColumnKinds', () => {
  const names = ['statuscode', 'statecode', 'industrycode', 'donotemail', 'createdon', 'birthday', 'description', 'nope'];

  it('classifies_each_column_by_its_type', async () => {
    mockMetadata();
    const kinds = await loadColumnKinds(api(), 'account', names, 1033);
    expect(kinds.statuscode).toEqual({
      kind: 'choice',
      type: 'Status',
      options: [{ value: 1, label: 'Active', state: 0 }, { value: 2, label: 'Inactive', state: 1 }],
    });
    expect(kinds.statecode).toEqual({
      kind: 'choice',
      type: 'State',
      options: [{ value: 0, label: 'Active' }, { value: 1, label: 'Inactive' }],
    });
    expect(kinds.industrycode).toMatchObject({ kind: 'choice', type: 'Picklist' });
    expect(kinds.createdon).toEqual({ kind: 'date', dateOnly: false, zoneIndependent: false });
    expect(kinds.birthday).toEqual({ kind: 'date', dateOnly: true, zoneIndependent: false });
    expect(kinds.description).toEqual({ kind: 'other' });
  });

  it('a_time_zone_independent_date_is_flagged', async () => {
    mockMetadata();
    const kinds = await loadColumnKinds(api(), 'account', ['wallclock'], 1033);
    expect(kinds.wallclock).toEqual({ kind: 'date', dateOnly: false, zoneIndependent: true });
  });

  it('a_boolean_gives_false_then_true', async () => {
    mockMetadata();
    const kinds = await loadColumnKinds(api(), 'account', names, 1033);
    expect(kinds.donotemail).toEqual({
      kind: 'choice',
      type: 'Boolean',
      options: [{ value: false, label: 'No' }, { value: true, label: 'Yes' }],
    });
  });

  it('a_name_the_table_lacks_is_absent', async () => {
    mockMetadata();
    const kinds = await loadColumnKinds(api(), 'account', names, 1033);
    expect('nope' in kinds).toBe(false);
  });

  it('labels_pick_the_lcid_else_1033', async () => {
    mockMetadata();
    const italian = await loadColumnKinds(api(), 'account', ['industrycode'], 1040);
    const other = await loadColumnKinds(api(), 'account', ['industrycode'], 1031);
    const options = (k: Awaited<ReturnType<typeof loadColumnKinds>>) => (k.industrycode as { options: { label: string }[] }).options.map((o) => o.label);
    expect(options(italian)).toEqual(['Hardware IT', 'Software']);
    expect(options(other)).toEqual(['Hardware', 'Software']);
  });

  it('asks_the_server_for_the_attribute_list_then_the_typed_casts', async () => {
    const seen = mockMetadata();
    await loadColumnKinds(api(), 'account', ['statuscode', 'createdon', 'nope'], 1033);
    expect(seen[0]).toBe(
      "EntityDefinitions(LogicalName='account')/Attributes?$filter=LogicalName eq 'statuscode' or LogicalName eq 'createdon' or LogicalName eq 'nope'&$select=LogicalName,AttributeType,AttributeTypeName",
    );
    expect(seen).toContain(
      `EntityDefinitions(LogicalName='account')/Attributes(LogicalName='statuscode')/${TYPE}StatusAttributeMetadata?$select=LogicalName&$expand=OptionSet`,
    );
    expect(seen).toContain(
      `EntityDefinitions(LogicalName='account')/Attributes(LogicalName='createdon')/${TYPE}DateTimeAttributeMetadata?$select=DateTimeBehavior,Format`,
    );
    expect(seen).toHaveLength(3);
  });

  it('attributes_that_were_not_asked_for_are_dropped_and_get_no_typed_request', async () => {
    const seen = mockMetadata();
    const kinds = await loadColumnKinds(api(), 'account', ['statuscode'], 1033);
    expect(Object.keys(kinds)).toEqual(['statuscode']);
    expect(seen.filter((s) => s.includes('(LogicalName=\'') && !s.startsWith("EntityDefinitions(LogicalName='account')/Attributes?"))).toHaveLength(1);
  });

  it('a_failed_typed_request_makes_that_column_other_and_warns', async () => {
    mockMetadata();
    server.use(
      http.get('*/api/data/v9.2/*', ({ request }) =>
        decodeURIComponent(request.url).includes(`(LogicalName='birthday')/`)
          ? HttpResponse.json({ error: { message: 'boom' } }, { status: 500 })
          : undefined,
      ),
    );
    const warn = vi.spyOn(console, 'warn').mockImplementation(() => {});
    const kinds = await loadColumnKinds(api(), 'account', ['birthday', 'createdon'], 1033);
    expect(kinds.birthday).toEqual({ kind: 'other' });
    expect(kinds.createdon).toEqual({ kind: 'date', dateOnly: false, zoneIndependent: false });
    expect(warn).toHaveBeenCalledWith('[xrmverse presentation]', expect.stringContaining('birthday'));
    warn.mockRestore();
  });

  it('no_names_makes_no_request', async () => {
    const seen = mockMetadata();
    expect(await loadColumnKinds(api(), 'account', [], 1033)).toEqual({});
    expect(seen).toEqual([]);
  });
});
