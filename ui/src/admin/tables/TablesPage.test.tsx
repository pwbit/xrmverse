import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { server } from '../../test/server';
import { App } from '../../shell/App';
import { en } from '../../shell/i18n/en';
import { API, onPath, signInAsAdministrator } from '../security/testSupport';
import { entityBody } from './schemaBodies';
import { installMetadata, rawAttr, rawEntity } from './tableTestSupport';

const project = rawEntity('xv_project', 'xv_projects', [rawAttr('xv_projectid', 'Uniqueidentifier', { IsPrimaryId: true })], { display: 'Project' });
const contact = rawEntity('contact', 'contacts', [], { display: 'Contact', IsCustomEntity: false });

beforeEach(() => {
  signInAsAdministrator();
  installMetadata([project, contact]);
});
afterEach(() => sessionStorage.clear());

describe('TablesPage', () => {
  it('lists the tables and filters them by name or custom only', async () => {
    const user = userEvent.setup();
    render(<App initialEntries={['/manage/tables']} />);
    const table = await screen.findByRole('table', { name: en['nav.tables'] });
    expect(within(table).getByText('contact')).toBeInTheDocument();
    expect(within(table).getByRole('link', { name: 'xv_project' })).toHaveAttribute('href', '/manage/tables/xv_project');
    await user.click(screen.getByRole('checkbox', { name: en['tables.customOnly'] }));
    expect(within(table).queryByText('contact')).toBeNull();
    await user.click(screen.getByRole('checkbox', { name: en['tables.customOnly'] }));
    await user.type(screen.getByLabelText(en['tables.filter']), 'CONT');
    expect(within(table).queryByText('xv_project')).toBeNull();
    expect(within(table).getByText('contact')).toBeInTheDocument();
  });

  it('Create_table_posts_the_schema_name_derived_from_the_display_name_and_opens_the_table', async () => {
    let posted: unknown;
    server.use(
      http.post(`${API}/EntityDefinitions`, async ({ request }) => {
        posted = await request.json();
        return new HttpResponse(null, { status: 204 });
      }),
      onPath('get', "EntityDefinitions(LogicalName='xv_worktask')", () => HttpResponse.json(rawEntity('xv_worktask', 'xv_worktasks', [], { display: 'Work task' }))),
    );
    const user = userEvent.setup();
    render(<App initialEntries={['/manage/tables']} />);
    await user.click(await screen.findByRole('button', { name: en['table.create.open'] }));
    await user.type(await screen.findByLabelText(/^Display name/), 'Work task');
    await user.type(screen.getByLabelText(/^Plural display name/), 'Work tasks');
    await user.selectOptions(screen.getByLabelText(en['table.field.ownership']), 'organization');
    // The default publisher's prefix (xv) and the display name in PascalCase; the logical name is its lowercase.
    await waitFor(() => expect(screen.getByLabelText(/^Schema name/)).toHaveValue('xv_WorkTask'));
    expect(screen.getByLabelText(/^Logical name/)).toHaveValue('xv_worktask');
    expect(screen.getByLabelText(/^Logical name/)).toHaveAttribute('readonly');
    // The primary name column defaults to <prefix>_Name.
    expect(screen.getByLabelText(en['table.field.primaryName'])).toHaveAttribute('placeholder', 'xv_Name');
    await user.click(screen.getByRole('button', { hidden: true, name: en['table.create.submit'] }));
    expect(await screen.findByText('Table "xv_worktask" created.')).toBeInTheDocument();
    expect(posted).toMatchObject({ '@odata.type': 'Microsoft.Dynamics.CRM.EntityMetadata', OwnershipType: 'OrganizationOwned', LogicalName: 'xv_worktask', SchemaName: 'xv_WorkTask' });
    expect(posted).toEqual(
      entityBody({
        name: 'xv_worktask',
        schemaName: 'xv_WorkTask',
        label: 'Work task',
        pluralLabel: 'Work tasks',
        ownership: 'organization',
        primaryName: { name: 'xv_name', schemaName: 'xv_Name', label: 'Name' },
      }),
    );
    expect(await screen.findByRole('heading', { name: 'Work task (xv_worktask)' })).toBeInTheDocument();
    await screen.findByRole('tab', { name: en['table.tab.columns'] });
  });

  it('takes an edited schema name and refuses one without a publisher prefix or with other characters', async () => {
    let posted: { SchemaName?: string; LogicalName?: string } | undefined;
    server.use(
      http.post(`${API}/EntityDefinitions`, async ({ request }) => {
        posted = (await request.json()) as typeof posted;
        return new HttpResponse(null, { status: 204 });
      }),
      onPath('get', "EntityDefinitions(LogicalName='xv_widget')", () => HttpResponse.json(rawEntity('xv_widget', 'xv_widgets', [], { display: 'Gadget' }))),
    );
    const user = userEvent.setup();
    render(<App initialEntries={['/manage/tables']} />);
    await user.click(await screen.findByRole('button', { name: en['table.create.open'] }));
    await user.type(await screen.findByLabelText(/^Display name/), 'Gadget');
    await user.type(screen.getByLabelText(/^Plural display name/), 'Gadgets');
    const schema = screen.getByLabelText(/^Schema name/);
    await waitFor(() => expect(schema).toHaveValue('xv_Gadget'));
    const submit = screen.getByRole('button', { hidden: true, name: en['table.create.submit'] });

    await user.clear(schema);
    await user.type(schema, 'Widget');
    expect(await screen.findByText(en['schema.err.prefix'])).toBeInTheDocument();
    expect(submit).toBeDisabled();

    await user.clear(schema);
    await user.type(schema, 'xv_Wid-get');
    expect(await screen.findByText(en['schema.err.format'])).toBeInTheDocument();
    expect(submit).toBeDisabled();

    // Edited, it no longer follows the display name.
    await user.clear(schema);
    await user.type(schema, 'xv_Widget');
    await user.type(screen.getByLabelText(/^Display name/), 's');
    expect(schema).toHaveValue('xv_Widget');
    expect(screen.getByLabelText(/^Logical name/)).toHaveValue('xv_widget');
    await user.click(submit);
    expect(await screen.findByText('Table "xv_widget" created.')).toBeInTheDocument();
    expect(posted).toMatchObject({ LogicalName: 'xv_widget', SchemaName: 'xv_Widget' });
    await screen.findByRole('tab', { name: en['table.tab.columns'] });
  });

  it('shows why no schema name is suggested when the default publisher cannot be read', async () => {
    server.use(http.get(`${API}/solutions`, () => HttpResponse.json({ error: { code: '0x80040220', message: 'No read access to solution.' } }, { status: 403 })));
    const user = userEvent.setup();
    render(<App initialEntries={['/manage/tables']} />);
    await user.click(await screen.findByRole('button', { name: en['table.create.open'] }));
    await user.type(await screen.findByLabelText(/^Display name/), 'Task');
    expect(await screen.findByText(/^The prefix of the default publisher could not be read/)).toHaveTextContent('No read access to solution.');
    expect(screen.getByLabelText(/^Schema name/)).toHaveValue('');
    expect(screen.getByRole('button', { hidden: true, name: en['table.create.submit'] })).toBeDisabled();
  });
});
