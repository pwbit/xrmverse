import { describe, expect, it } from 'vitest';
import { en } from '../../shell/i18n/en';
import { parseEntity } from '../tables/metadata';
import { rawAttr, rawEntity } from '../tables/tableTestSupport';
import type { SubgridSpec } from './formControls';
import { readForm } from './formDesign';
import { formRow } from './formDesignTestSupport';
import { filterProblem, filterSources, filterTargets, placedColumns } from './subgridFilterChoices';

const meta = (raw: ReturnType<typeof rawEntity>) => parseEntity(raw as Parameters<typeof parseEntity>[0]);
const TEXT = '{4273EDBD-AC1D-40d3-9FB2-095C621B552D}';

/** The form's table: a Customer column, a plain lookup, an owner, a text column and a lookup not placed on the form. */
const session = meta(rawEntity('xrmv_session', 'xrmv_sessions', [
  rawAttr('xrmv_sessionid', 'Uniqueidentifier', { IsPrimaryId: true }),
  rawAttr('xrmv_name', 'String', { IsPrimaryName: true, display: 'Name' }),
  rawAttr('xrmv_customerid', 'Lookup', { display: 'Customer', AttributeType: 'Customer', Targets: ['account', 'contact'] }),
  rawAttr('xrmv_accountid', 'Lookup', { display: 'Account', Targets: ['account'] }),
  rawAttr('ownerid', 'Lookup', { display: 'Owner', AttributeType: 'Owner', Targets: ['systemuser', 'team'] }),
  rawAttr('xrmv_otherid', 'Lookup', { display: 'Other', Targets: ['account'] }),
], { display: 'Session' }));

/** The subgrid's table. */
const caseTable = meta(rawEntity('xrmv_case', 'xrmv_cases', [
  rawAttr('xrmv_caseid', 'Uniqueidentifier', { IsPrimaryId: true }),
  rawAttr('xrmv_title', 'String', { IsPrimaryName: true, display: 'Title' }),
  rawAttr('xrmv_customerid', 'Lookup', { display: 'Customer', AttributeType: 'Customer', Targets: ['account', 'contact'] }),
  rawAttr('xrmv_accountid', 'Lookup', { display: 'Billing account', Targets: ['account'] }),
  rawAttr('xrmv_contactid', 'Lookup', { display: 'Contact', Targets: ['contact'] }),
  rawAttr('xrmv_anyid', 'Lookup', { display: 'Any party', AttributeType: 'Customer', Targets: ['contact', 'account', 'lead'] }),
], { display: 'Case' }));

const field = (id: number, name: string) =>
  `<row><cell id="{c0000000-0000-0000-0000-00000000000${id}}"><labels><label description="${name}" languagecode="1033" /></labels>` +
  `<control id="${name}" classid="${TEXT}" datafieldname="${name}" /></cell></row>`;

const FORM = `<form><tabs><tab name="t" id="{a0000000-0000-0000-0000-000000000001}"><columns><column width="100%"><sections>
  <section name="s" id="{a0000000-0000-0000-0000-000000000002}" columns="1"><rows>
    ${field(1, 'xrmv_name')}${field(2, 'xrmv_customerid')}${field(3, 'xrmv_accountid')}${field(4, 'ownerid')}
  </rows></section></sections></column></columns></tab></tabs></form>`;

const design = (xml = FORM) => readForm(formRow(xml, { objecttypecode: 'xrmv_session' }), 1033);
const names = (attrs: { logicalName: string }[]) => attrs.map((a) => a.logicalName);
const byName = (name: string) => session.attributes.find((a) => a.logicalName === name)!;

const spec = (filter?: SubgridSpec['filter']): SubgridSpec => ({
  kind: 'subgrid', controlId: 'cases', relationship: '', targetEntity: 'xrmv_case', viewId: '', recordsPerPage: 4,
  viewPicker: false, filter,
});

describe('subgrid filter choices', () => {
  it('lists the lookup, customer and owner columns placed on the form, by display name', () => {
    expect(names(filterSources(session, placedColumns(design())))).toEqual(['xrmv_accountid', 'xrmv_customerid', 'ownerid']);
  });

  it('never lists a text column nor a lookup not placed on the form', () => {
    const sources = names(filterSources(session, new Set(['xrmv_name', 'xrmv_customerid'])));
    expect(sources).toEqual(['xrmv_customerid']);
    expect(sources).not.toContain('xrmv_otherid');
  });

  it('a Customer source matches only the columns that accept every one of its targets', () => {
    expect(names(filterTargets(byName('xrmv_customerid'), caseTable))).toEqual(['xrmv_anyid', 'xrmv_customerid']);
  });

  it('a plain lookup to account matches the columns with account among their targets', () => {
    expect(names(filterTargets(byName('xrmv_accountid'), caseTable))).toEqual(['xrmv_anyid', 'xrmv_accountid', 'xrmv_customerid']);
  });

  it('flags a filter whose source is no longer on the form', () => {
    const d = design(FORM.replace(field(2, 'xrmv_customerid'), ''));
    expect(filterProblem(d, spec({ source: 'xrmv_customerid', target: 'xrmv_customerid' }), session, caseTable))
      .toBe(en['forms.props.filterSourceGone'].replace('{name}', 'Customer'));
  });

  it('flags a filter whose target can no longer match the source', () => {
    expect(filterProblem(design(), spec({ source: 'xrmv_customerid', target: 'xrmv_contactid' }), session, caseTable))
      .toBe(en['forms.props.filterTargetGone'].replace('{name}', 'Contact').replace('{table}', 'Case'));
    // The subgrid's table not read yet: only the source is checked.
    expect(filterProblem(design(), spec({ source: 'xrmv_customerid', target: 'xrmv_contactid' }), session)).toBeUndefined();
  });

  it('a valid filter, and a subgrid without one, have no problem', () => {
    expect(filterProblem(design(), spec({ source: 'xrmv_customerid', target: 'xrmv_customerid' }), session, caseTable)).toBeUndefined();
    expect(filterProblem(design(), spec(), session, caseTable)).toBeUndefined();
  });
});
