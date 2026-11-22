import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { describe, expect, it } from 'vitest';
import { onPath } from '../../../admin/security/testSupport';
import { attr } from '../../../admin/tables/testAttrs';
import { installMetadata, rawAttr, rawEntity, rawOneToMany } from '../../../admin/tables/tableTestSupport';
import { USER_ID } from '../../../apps/testSupport';
import type { Cell } from '../../../model/form';
import { server } from '../../../test/server';
import { contextOf, field, formOf, RECORD_ID, renderForm, section, tab } from './formTestSupport';

const name = attr('xv_name', { isPrimaryName: true, displayName: 'Name' });
const contactLookup = attr('xv_contactid', {
  kind: 'lookup',
  targets: ['contact'],
  displayName: 'Contact',
});
const CONTACT_ID = 'dddddddd-0000-0000-0000-000000000007';
const QUICK_FORM_ID = '33333333-0000-0000-0000-000000000001';
const LOOKUP_NAME = '_xv_contactid_value@Microsoft.Dynamics.CRM.lookuplogicalname';

const base = { xv_projectid: RECORD_ID, xv_name: 'Alpha' };
const withContact = {
  ...base,
  _xv_contactid_value: CONTACT_ID,
  [LOOKUP_NAME]: 'contact',
};

const quickViewCell = (forms: Record<string, string> = { contact: QUICK_FORM_ID }): Cell => ({
  id: 'cell-contact',
  labels: { 1033: 'Contact card' },
  showLabel: true,
  visible: true,
  colSpan: 1,
  rowSpan: 1,
  control: {
    kind: 'quickview',
    id: 'contactcard',
    lookupAttribute: 'xv_contactid',
    forms,
  },
});

const QUICK_FORMXML = `<form type="quick"><tabs><tab name="t" verticallayout="true"><labels><label description="Contact" languagecode="1033"/></labels><columns><column width="100%"><sections>
  <section name="s" showlabel="false" columns="1"><rows>
    <row><cell id="{c1}"><labels><label description="Full name" languagecode="1033"/></labels><control id="fullname" classid="{4273EDBD-AC1D-40d3-9FB2-095C621B552D}" datafieldname="fullname"/></cell></row>
    <row><cell id="{c2}"><labels><label description="Email" languagecode="1033"/></labels><control id="emailaddress1" classid="{ADA2203E-B4CD-49be-9DDF-234642B43B52}" datafieldname="emailaddress1"/></cell></row>
  </rows></section></sections></column></columns></tab></tabs></form>`;

const contact = rawEntity(
  'contact',
  'contacts',
  [
    rawAttr('contactid', 'Uniqueidentifier', { IsPrimaryId: true }),
    rawAttr('fullname', 'String', { IsPrimaryName: true }),
    rawAttr('emailaddress1', 'String'),
  ],
  {
    SchemaName: 'Contact',
    PrimaryNameAttribute: 'fullname',
    PrimaryIdAttribute: 'contactid',
  },
);

function install(over: { formxml?: string; formStatus?: number; type?: number; objecttypecode?: string } = {}) {
  installMetadata([contact], [rawOneToMany('xv_x', 'xv_project', 'xv_contactid', 'contact', 'xv_contactid')]);
  const seen: string[] = [];
  server.use(
    onPath('get', `systemusers(${USER_ID})/Microsoft.Dynamics.CRM.RetrievePrincipalAccess(Target=@t)`, () =>
      HttpResponse.json({ AccessRights: 'ReadAccess' }),
    ),
    onPath('get', `systemforms(${QUICK_FORM_ID})`, () => {
      seen.push('form');
      return over.formStatus
        ? HttpResponse.json({ error: { code: '0x80040217', message: 'gone' } }, { status: over.formStatus })
        : HttpResponse.json({
            systemformid: QUICK_FORM_ID,
            name: 'Contact card',
            objecttypecode: over.objecttypecode ?? 'contact',
            type: over.type ?? 6,
            formxml: over.formxml ?? QUICK_FORMXML,
          });
    }),
    onPath('get', `contacts(${CONTACT_ID})`, () => {
      seen.push('record');
      return HttpResponse.json({
        contactid: CONTACT_ID,
        fullname: 'Carla Rossi',
        emailaddress1: 'carla@example.com',
      });
    }),
  );
  return seen;
}

function render(cell: Cell, data: Record<string, unknown>, over: Parameters<typeof install>[0] = {}) {
  let seen: string[] = [];
  const form = formOf([
    tab('main', [
      {
        width: '100%',
        sections: [section([[field('xv_name')], [cell]])],
      },
    ]),
  ]);
  const view = renderForm(form, contextOf([name, contactLookup], data), {
    apps: { privileges: [] },
    setup: () => {
      seen = install(over);
    },
  });
  return { ...view, seen: () => seen };
}

describe('QuickViewControl', () => {
  it('quick_view_renders_related_record_read_only', async () => {
    render(quickViewCell(), withContact);
    const full = await screen.findByLabelText('Full name');
    expect(full).toHaveValue('Carla Rossi');
    expect(full).toBeDisabled();
    const email = screen.getByLabelText('Email');
    expect(email).toHaveValue('carla@example.com');
    expect(email).toBeDisabled();
    expect(screen.getByText('Contact card')).toBeInTheDocument();
    // The host form's own field stays editable.
    expect(screen.getByLabelText('xv_name label')).toBeEnabled();
  });

  it('quick_view_empty_lookup_renders_nothing', async () => {
    const { seen, container } = render(quickViewCell(), base);
    await screen.findByLabelText('xv_name label');
    expect(screen.queryByText('Contact card')).toBeNull();
    expect(container.querySelector('[data-testid="quickview"]')).toBeNull();
    expect(seen()).toEqual([]);
  });

  it('quick_view_target_without_form_renders_nothing', async () => {
    const { seen, container } = render(quickViewCell({ account: QUICK_FORM_ID }), withContact);
    await screen.findByLabelText('xv_name label');
    expect(screen.queryByText('Contact card')).toBeNull();
    expect(container.querySelector('[data-testid="quickview"]')).toBeNull();
    expect(seen()).toEqual([]);
  });

  it('quick_view_that_cannot_load_shows_reason_and_the_form_still_renders', async () => {
    render(quickViewCell(), withContact, { formStatus: 404 });
    expect(await screen.findByText(/This quick view cannot be shown: /)).toBeInTheDocument();
    expect(screen.getByLabelText('xv_name label')).toHaveValue('Alpha');
    await waitFor(() => expect(screen.queryByRole('progressbar')).toBeNull());
  });

  it('quick_view_form_of_another_type_is_not_drawn', async () => {
    render(quickViewCell(), withContact, { type: 2 });
    expect(await screen.findByText('This quick view form cannot be shown.')).toBeInTheDocument();
    expect(screen.queryByLabelText('Full name')).toBeNull();
  });

  it('quick_view_form_of_another_table_is_not_drawn', async () => {
    render(quickViewCell(), withContact, { objecttypecode: 'account' });
    expect(await screen.findByText('This quick view form cannot be shown.')).toBeInTheDocument();
  });

  it('quick_view_reads_no_rights', async () => {
    const { requests } = render(quickViewCell(), withContact);
    await screen.findByLabelText('Full name');
    expect(requests.some((r) => r.includes('WhoAmI') || r.includes('RetrieveUserPrivileges') || r.includes('RetrievePrincipalAccess'))).toBe(false);
  });

  it('quick_view_subgrid_never_takes_the_host_forms_subgrid_modes', async () => {
    const TASK_VIEW = 'a0000000-0000-0000-0000-0000000000a1';
    const subgridXml = QUICK_FORMXML.replace(
      '</rows></section>',
      `<row><cell id="{c3}"><labels><label description="Tasks" languagecode="1033"/></labels><control id="Tasks" classid="{E7A81278-8635-4d9e-8D4D-59480B391C5B}"><parameters><ViewId>{${TASK_VIEW}}</ViewId><RelationshipName>contact_xv_task</RelationshipName><TargetEntityType>xv_task</TargetEntityType><RecordsPerPage>4</RecordsPerPage></parameters></control></cell></row></rows></section>`,
    );
    const form = formOf([tab('main', [{ width: '100%', sections: [section([[field('xv_name')], [quickViewCell()]])] }])]);
    renderForm(form, contextOf([name, contactLookup], withContact), {
      apps: {
        privileges: [],
        savedQueries: [
          {
            id: TASK_VIEW,
            name: 'Active Tasks',
            entity: 'xv_task',
            isdefault: true,
            fetchxml: '<fetch version="1.0" mapping="logical"><entity name="xv_task"><attribute name="xv_title"/><attribute name="xv_taskid"/></entity></fetch>',
            layoutxml: '<grid name="resultset" jump="xv_title" select="1" icon="1" preview="1"><row name="result" id="xv_taskid"><cell name="xv_title" width="200"/></row></grid>',
          },
        ],
        tables: { xv_task: { entitySetName: 'xv_tasks', primaryId: 'xv_taskid', labels: { xv_title: { 1033: 'Title' } } } },
        records: () => ({ value: [{ xv_taskid: 't1', xv_title: 'Call back' }] }),
      },
      setup: () => {
        install({ formxml: subgridXml });
        server.use(
          http.get('*/api/data/v9.2/RelationshipDefinitions', () =>
            HttpResponse.json({ value: [rawOneToMany('xv_x', 'xv_project', 'xv_contactid', 'contact', 'xv_contactid'), rawOneToMany('contact_xv_task', 'xv_task', 'xv_contactid', 'contact')] }),
          ),
        );
      },
      // The host form's entry for a subgrid "tasks": the quick view's subgrid of the same id must not use it.
      formView: { subgridModes: { formId: 'f1', entries: { tasks: { cards: {}, default: 'cards' } } } },
    });
    const grid = await screen.findByRole('grid', { name: 'Active Tasks' });
    expect(within(grid).getByText('Call back')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /^Show as: / })).toBeNull();
  });
});
