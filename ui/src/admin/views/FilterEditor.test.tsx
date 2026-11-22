import { screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { describe, expect, it } from 'vitest';
import { server } from '../../test/server';
import { en } from '../../shell/i18n/en';
import { API, onPath } from '../security/testSupport';
import { FilterEditor } from './FilterEditor';
import { accountView, renderEditor } from './filterEditorTestSupport';
import { writeView } from './viewDesign';
import { normalizeXml } from './viewDesignTestSupport';

const ROOT = en['views.filter.root'];
const combo = (name: string) => screen.getByRole('combobox', { name });
const options = (name: string) => within(combo(name)).getAllByRole('option').map((o) => (o as HTMLOptionElement).value);
const click = (name: string) => userEvent.click(screen.getByRole('button', { name }));
const entityOf = (fetchxml: string) => /<entity name="account">([\s\S]*)<\/entity>/.exec(fetchxml)![1];

describe('FilterEditor', () => {
  it('builds "Status eq Active AND (City like Rom OR Revenue gt 1000)" as the expected FetchXML', async () => {
    const { latest } = await renderEditor(FilterEditor, accountView());
    expect(screen.getByText(en['views.filter.empty'])).toBeInTheDocument();

    await click(`Add condition to ${ROOT}`);
    await userEvent.selectOptions(combo('Column of condition 1'), 'statuscode');
    expect(combo('Operator of condition 1')).toHaveValue('eq');
    await userEvent.selectOptions(combo('Value of condition 1'), '1');

    await click(`Add group to ${ROOT}`);
    await userEvent.selectOptions(combo('Type of group 2'), 'or');
    await click('Add condition to group 2');
    await userEvent.selectOptions(combo('Column of condition 2.1'), 'address1_city');
    await userEvent.selectOptions(combo('Operator of condition 2.1'), 'like');
    await userEvent.type(screen.getByRole('textbox', { name: 'Value of condition 2.1' }), 'Rom');
    await click('Add condition to group 2');
    await userEvent.selectOptions(combo('Column of condition 2.2'), 'revenue');
    await userEvent.selectOptions(combo('Operator of condition 2.2'), 'gt');
    await userEvent.type(screen.getByRole('spinbutton', { name: 'Value of condition 2.2' }), '1000');

    expect(normalizeXml(writeView(latest()).fetchxml)).toBe(
      normalizeXml(
        '<fetch version="1.0" mapping="logical"><entity name="account"><attribute name="name"/><attribute name="accountid"/>' +
          '<filter type="and"><condition attribute="statuscode" operator="eq" value="1"/></filter>' +
          '<filter type="or"><condition attribute="address1_city" operator="like" value="%Rom%"/>' +
          '<condition attribute="revenue" operator="gt" value="1000"/></filter></entity></fetch>',
      ),
    );
  });

  it('offers the E5 operators of the chosen column kind', async () => {
    await renderEditor(FilterEditor, accountView());
    await click(`Add condition to ${ROOT}`);
    const column = 'Column of condition 1';
    const operator = 'Operator of condition 1';
    // Only filterable columns: no primary id (unsupported kind), companion or hidden column.
    expect(options(column)).toEqual(expect.arrayContaining(['name', 'revenue', 'statuscode', 'ownerid']));
    expect(options(column)).not.toEqual(expect.arrayContaining(['accountid']));
    expect(options(column)).not.toContain('primarycontactidname');
    expect(options(column)).not.toContain('documentbody');

    await userEvent.selectOptions(combo(column), 'address1_city');
    expect(options(operator)).toEqual(['eq', 'ne', 'like', 'not-like', 'begins-with', 'ends-with', 'null', 'not-null']);
    await userEvent.selectOptions(combo(column), 'revenue');
    expect(options(operator)).toEqual(['eq', 'ne', 'gt', 'ge', 'lt', 'le', 'null', 'not-null']);
    await userEvent.selectOptions(combo(column), 'createdon');
    expect(options(operator)).toEqual(['on', 'on-or-after', 'on-or-before', 'today', 'yesterday', 'tomorrow', 'this-week',
      'this-month', 'this-year', 'last-x-days', 'next-x-days', 'null', 'not-null']);
    await userEvent.selectOptions(combo(column), 'donotemail');
    expect(options(operator)).toEqual(['eq', 'ne', 'in', 'not-in', 'null', 'not-null']);
    await userEvent.selectOptions(combo(column), 'primarycontactid');
    expect(options(operator)).toEqual(['eq', 'ne', 'null', 'not-null']);
    await userEvent.selectOptions(combo(column), 'ownerid');
    expect(options(operator)).toEqual(['eq', 'ne', 'null', 'not-null', 'eq-userid', 'ne-userid']);
    await userEvent.selectOptions(combo(column), 'owningbusinessunit');
    expect(options(operator)).toEqual(['eq', 'ne', 'null', 'not-null', 'eq-businessid', 'ne-businessid']);
    expect(within(combo(operator)).getByRole('option', { name: en['views.filter.op.eq-businessid'] })).toBeInTheDocument();
  });

  it('shows the value editor of the operator: text, number, date, choices, none', async () => {
    const { latest } = await renderEditor(FilterEditor, accountView());
    await click(`Add condition to ${ROOT}`);
    const column = 'Column of condition 1';
    const operator = 'Operator of condition 1';
    const value = 'Value of condition 1';

    await userEvent.selectOptions(combo(column), 'name');
    expect(screen.getByRole('textbox', { name: value })).toBeInTheDocument();
    await userEvent.selectOptions(combo(column), 'numberofemployees');
    expect(screen.getByRole('spinbutton', { name: value })).toBeInTheDocument();
    await userEvent.selectOptions(combo(column), 'createdon');
    expect(screen.getByLabelText(value)).toHaveAttribute('type', 'date');
    await userEvent.selectOptions(combo(operator), 'last-x-days');
    expect(screen.getByRole('spinbutton', { name: value })).toBeInTheDocument();
    await userEvent.selectOptions(combo(operator), 'this-month');
    expect(screen.queryByLabelText(value)).toBeNull();

    await userEvent.selectOptions(combo(column), 'statuscode');
    await userEvent.selectOptions(combo(operator), 'in');
    const choices = screen.getByRole('group', { name: value });
    await userEvent.click(within(choices).getByRole('checkbox', { name: 'Active' }));
    await userEvent.click(within(choices).getByRole('checkbox', { name: 'Inactive' }));
    expect(entityOf(writeView(latest()).fetchxml)).toContain(
      '<filter type="and"><condition attribute="statuscode" operator="in"><value>1</value><value>2</value></condition></filter>',
    );
    await userEvent.selectOptions(combo(operator), 'null');
    expect(screen.queryByRole('group', { name: value })).toBeNull();
    expect(entityOf(writeView(latest()).fetchxml)).toContain('<condition attribute="statuscode" operator="null"/>');
  });

  it('picks a lookup value with the admin lookup field', async () => {
    const { latest } = await renderEditor(FilterEditor, accountView());
    server.use(
      onPath('get', "EntityDefinitions(LogicalName='contact')", () =>
        HttpResponse.json({ EntitySetName: 'contacts', PrimaryIdAttribute: 'contactid', PrimaryNameAttribute: 'fullname' }),
      ),
      http.get(`${API}/contacts`, () => HttpResponse.json({ value: [{ contactid: 'c-1', fullname: 'Maria Rossi' }] })),
    );
    await click(`Add condition to ${ROOT}`);
    await userEvent.selectOptions(combo('Column of condition 1'), 'primarycontactid');
    await click('Choose Value of condition 1');
    await userEvent.click(await screen.findByRole('option', { name: 'Maria Rossi' }));
    expect(screen.getByRole('textbox', { name: 'Value of condition 1' })).toHaveValue('Maria Rossi');
    expect(entityOf(writeView(latest()).fetchxml)).toContain('<condition attribute="primarycontactid" operator="eq" value="c-1"/>');
  });

  it('edits nested groups: add, switch and/or, delete', async () => {
    const { latest } = await renderEditor(
      FilterEditor,
      accountView('<filter type="and"><condition attribute="statuscode" operator="eq" value="1"/></filter>'),
    );
    expect(combo('Column of condition 1.1')).toHaveValue('statuscode');
    await click('Add group to group 1');
    await click('Add condition to group 1.2');
    await userEvent.type(screen.getByRole('textbox', { name: 'Value of condition 1.2.1' }), 'A');
    await click('Add group to group 1.2');
    await userEvent.selectOptions(combo('Type of group 1.2'), 'or');
    expect(combo('Type of group 1.2.2')).toHaveValue('and');
    await click('Delete group 1.2.2');
    expect(screen.queryByRole('group', { name: 'group 1.2.2' })).toBeNull();
    expect(entityOf(writeView(latest()).fetchxml)).toContain(
      '<filter type="and"><condition attribute="statuscode" operator="eq" value="1"/>' +
        '<filter type="or"><condition attribute="name" operator="eq" value="A"/></filter></filter>',
    );
    await click('Delete condition 1.1');
    await click('Delete group 1');
    expect(screen.getByText(en['views.filter.empty'])).toBeInTheDocument();
    expect(entityOf(writeView(latest()).fetchxml)).not.toContain('<filter');
  });

  it('keeps an opaque part read-only and byte for byte while a sibling is edited', async () => {
    const opaque = '<filter type="and" hint="x"><condition entityname="c" attribute="fullname" operator="like" value="%a%"/></filter>';
    const { latest } = await renderEditor(
      FilterEditor,
      accountView(`<filter type="and"><condition attribute="statuscode" operator="eq" value="1"/></filter>${opaque}`),
    );
    expect(screen.getByText(en['views.filter.opaque'])).toBeInTheDocument();
    expect(screen.queryByRole('combobox', { name: 'Column of condition 2.1' })).toBeNull();
    await userEvent.selectOptions(combo('Value of condition 1.1'), '2');
    const written = writeView(latest()).fetchxml;
    expect(written).toContain('<condition attribute="statuscode" operator="eq" value="2"/>');
    expect(written).toContain(opaque);
  });

  it('shows a condition on a column the table does not list under its logical name', async () => {
    await renderEditor(FilterEditor, accountView('<filter type="and"><condition attribute="statecode" operator="eq" value="0"/></filter>'));
    expect(combo('Column of condition 1.1')).toHaveValue('statecode');
    expect(combo('Operator of condition 1.1')).toHaveValue('eq');
    expect(screen.getByRole('textbox', { name: 'Value of condition 1.1' })).toHaveValue('0');
  });

  it('does not show the quick find conditions', async () => {
    await renderEditor(
      FilterEditor,
      accountView(
        '<filter type="and"><condition attribute="statuscode" operator="eq" value="1"/></filter>' +
          '<filter type="or" isquickfindfields="1"><condition attribute="name" operator="like" value="{0}"/></filter>',
        4,
      ),
    );
    expect(screen.getAllByRole('combobox', { name: /^Column of/ })).toHaveLength(1);
  });
});
