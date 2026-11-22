import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { server } from '../../test/server';
import { App } from '../../shell/App';
import { en } from '../../shell/i18n/en';
import { API, onPath, signInAsAdministrator } from '../security/testSupport';
import { attributeBody, lookupBody } from './schemaBodies';
import { installMetadata, lbl, rawAttr, rawEntity, rawOneToMany } from './tableTestSupport';

const project = rawEntity(
  'xv_project',
  'xv_projects',
  [
    rawAttr('xv_projectid', 'Uniqueidentifier', { IsPrimaryId: true, IsValidForUpdate: false }),
    rawAttr('xv_name', 'String', { IsPrimaryName: true, display: 'Name' }),
    rawAttr('xv_parent', 'Lookup', { display: 'Parent', Targets: ['contact'] }),
  ],
  { PrimaryIdAttribute: 'xv_projectid', PrimaryNameAttribute: 'xv_name', display: 'Project' },
);
const contact = rawEntity('contact', 'contacts', [rawAttr('contactid', 'Uniqueidentifier', { IsPrimaryId: true })], { PrimaryIdAttribute: 'contactid', PrimaryNameAttribute: 'fullname', IsCustomEntity: false });
const relationships = [
  rawOneToMany('xv_project_xv_parent_contact', 'xv_project', 'xv_parent', 'contact', 'xv_ParentId'),
  rawOneToMany('xv_task_xv_project_xv_project', 'xv_task', 'xv_project', 'xv_project', 'xv_Project'),
  { '@odata.type': '#Microsoft.Dynamics.CRM.ManyToManyRelationshipMetadata', SchemaName: 'xv_project_xv_tag', Entity1LogicalName: 'xv_project', Entity2LogicalName: 'xv_tag' },
];

let entityReads = 0;
beforeEach(() => {
  signInAsAdministrator();
  entityReads = 0;
  installMetadata([project, contact], relationships);
  server.use(
    // Counts reads of the table so a test can wait for the reload that follows a change.
    onPath('get', "EntityDefinitions(LogicalName='xv_project')", () => {
      entityReads++;
      return HttpResponse.json(project);
    }),
  );
});
afterEach(() => sessionStorage.clear());

/** Types a schema name once the default prefix was read (shown by the suggestion for a display name), so no read is left in flight. */
async function typeSchemaNameAfterPrefix(user: ReturnType<typeof userEvent.setup>, schemaName: string) {
  await user.type(await screen.findByLabelText(en['column.field.label']), 'Tmp');
  const schema = screen.getByLabelText(/^Schema name/);
  await waitFor(() => expect(schema).toHaveValue('xv_Tmp'));
  await user.clear(schema);
  await user.type(schema, schemaName);
}

describe('TableDetail', () => {
  it('lists columns and relationships with their navigation property names', async () => {
    const user = userEvent.setup();
    render(<App initialEntries={['/manage/tables/xv_project']} />);
    const columns = await screen.findByRole('table', { name: en['table.tab.columns'] });
    expect(within(columns).getByText('xv_parent')).toBeInTheDocument();
    expect(within(columns).getByText('Lookup')).toBeInTheDocument();

    await user.click(screen.getByRole('tab', { name: en['table.tab.relationships'] }));
    const rels = await screen.findByRole('table', { name: en['table.tab.relationships'] });
    expect(within(rels).getByText('xv_ParentId')).toBeInTheDocument();
    expect(within(rels).getByText('xv_parent refers to contact')).toBeInTheDocument();
    expect(within(rels).getByText('xv_project of xv_task')).toBeInTheDocument();
    expect(within(rels).getByText('xv_tag')).toBeInTheDocument();
  });

  it('Add_column_posts_the_cli_shaped_attribute_body', async () => {
    let posted: unknown;
    server.use(
      onPath('post', "EntityDefinitions(LogicalName='xv_project')/Attributes", async ({ request }) => {
        posted = await request.json();
        return new HttpResponse(null, { status: 204 });
      }),
    );
    const user = userEvent.setup();
    render(<App initialEntries={['/manage/tables/xv_project']} />);
    await user.click(await screen.findByRole('button', { name: en['column.add.open'] }));
    await user.type(await screen.findByLabelText(en['column.field.label']), 'Notes');
    // Derived from the display name with the default publisher's prefix; the logical name is its lowercase.
    await waitFor(() => expect(screen.getByLabelText(/^Schema name/)).toHaveValue('xv_Notes'));
    expect(screen.getByLabelText(/^Logical name/)).toHaveValue('xv_notes');
    // The type options are named from the catalogue; their values are the schema's type names.
    expect(screen.getByRole('option', { hidden: true, name: en['column.type.string'] })).toHaveValue('string');
    await user.selectOptions(screen.getByLabelText(/^Type/), 'string');
    await user.type(screen.getByLabelText(en['column.field.maxLength']), '250');
    await user.click(screen.getByRole('button', { hidden: true, name: en['column.add.submit'] }));
    expect(await screen.findByText('Column "xv_notes" added.')).toBeInTheDocument();
    expect(posted).toMatchObject({ '@odata.type': 'Microsoft.Dynamics.CRM.StringAttributeMetadata', AttributeType: 'String', MaxLength: 250, LogicalName: 'xv_notes', SchemaName: 'xv_Notes' });
    expect(posted).toEqual(attributeBody({ name: 'xv_notes', schemaName: 'xv_Notes', type: 'string', label: 'Notes', maxLength: 250 }));
    // The table is read again after the change.
    await screen.findByRole('table', { name: en['table.tab.columns'] });
    expect(entityReads).toBe(2);
  });

  it('Add_image_column_sends_max_size_and_full_image_choice', async () => {
    let posted: unknown;
    server.use(
      onPath('post', "EntityDefinitions(LogicalName='xv_project')/Attributes", async ({ request }) => {
        posted = await request.json();
        return new HttpResponse(null, { status: 204 });
      }),
    );
    const user = userEvent.setup();
    render(<App initialEntries={['/manage/tables/xv_project']} />);
    await user.click(await screen.findByRole('button', { name: en['column.add.open'] }));
    await typeSchemaNameAfterPrefix(user, 'xv_Photo');
    await user.selectOptions(screen.getByLabelText(/^Type/), 'image');
    // Dataverse's range for an image column: 1 to 30720 KB.
    const size = screen.getByLabelText(en['column.field.maxSizeInKB']);
    await user.type(size, '30721');
    expect(await screen.findByText('Maximum size must be a whole number of KB from 1 to 30720.')).toBeInTheDocument();
    expect(screen.getByRole('button', { hidden: true, name: en['column.add.submit'] })).toBeDisabled();
    await user.clear(size);
    await user.type(size, '2048');
    await user.click(screen.getByLabelText(en['column.field.canStoreFullImage']));
    await user.click(screen.getByRole('button', { hidden: true, name: en['column.add.submit'] }));
    expect(await screen.findByText('Column "xv_photo" added.')).toBeInTheDocument();
    expect(posted).toEqual(attributeBody({ name: 'xv_photo', schemaName: 'xv_Photo', type: 'image', label: 'Tmp', maxSizeInKB: 2048, canStoreFullImage: true }));
    // No primary-image choice is offered: the server makes the first image column primary.
    expect(posted).not.toHaveProperty('IsPrimaryImage');
    await screen.findByRole('table', { name: en['table.tab.columns'] });
    await waitFor(() => expect(entityReads).toBe(2));
  });

  it('Add_file_column_checks_the_file_size_range_and_leaves_a_blank_size_to_the_server', async () => {
    let posted: unknown;
    server.use(
      onPath('post', "EntityDefinitions(LogicalName='xv_project')/Attributes", async ({ request }) => {
        posted = await request.json();
        return new HttpResponse(null, { status: 204 });
      }),
    );
    const user = userEvent.setup();
    render(<App initialEntries={['/manage/tables/xv_project']} />);
    await user.click(await screen.findByRole('button', { name: en['column.add.open'] }));
    await typeSchemaNameAfterPrefix(user, 'xv_Contract');
    await user.selectOptions(screen.getByLabelText(/^Type/), 'file');
    expect(screen.queryByLabelText(en['column.field.canStoreFullImage'])).toBeNull();
    const size = screen.getByLabelText(en['column.field.maxSizeInKB']);
    // Dataverse's range for a file column: 1 to 10485760 KB.
    await user.type(size, '0');
    expect(await screen.findByText('Maximum size must be a whole number of KB from 1 to 10485760.')).toBeInTheDocument();
    await user.clear(size);
    await user.type(size, '10485760');
    expect(screen.queryByText(/Maximum size must be/)).toBeNull();
    await user.clear(size);
    await user.click(screen.getByRole('button', { hidden: true, name: en['column.add.submit'] }));
    expect(await screen.findByText('Column "xv_contract" added.')).toBeInTheDocument();
    expect(posted).toEqual(attributeBody({ name: 'xv_contract', schemaName: 'xv_Contract', type: 'file', label: 'Tmp' }));
    await screen.findByRole('table', { name: en['table.tab.columns'] });
    await waitFor(() => expect(entityReads).toBe(2));
  });

  it('Add_column_of_a_global_choice_picks_from_the_existing_choices', async () => {
    let posted: unknown;
    server.use(
      http.get(`${API}/GlobalOptionSetDefinitions`, () => HttpResponse.json({ value: [{ Name: 'xv_priority', Options: [] }] })),
      onPath('post', "EntityDefinitions(LogicalName='xv_project')/Attributes", async ({ request }) => {
        posted = await request.json();
        return new HttpResponse(null, { status: 204 });
      }),
    );
    const user = userEvent.setup();
    render(<App initialEntries={['/manage/tables/xv_project']} />);
    await user.click(await screen.findByRole('button', { name: en['column.add.open'] }));
    await user.type(await screen.findByLabelText(/^Schema name/), 'xv_Prio');
    await user.selectOptions(screen.getByLabelText(/^Type/), 'choice');
    // The select exists before the choices arrive: wait for the option itself.
    await screen.findByRole('option', { hidden: true, name: 'xv_priority' });
    await user.selectOptions(screen.getByLabelText(/^Global choice/), 'xv_priority');
    await user.click(screen.getByRole('button', { hidden: true, name: en['column.add.submit'] }));
    expect(await screen.findByText('Column "xv_prio" added.')).toBeInTheDocument();
    expect(posted).toMatchObject({ '@odata.type': 'Microsoft.Dynamics.CRM.PicklistAttributeMetadata', OptionSet: { IsGlobal: true, Name: 'xv_priority' } });
    expect(posted).toEqual(attributeBody({ name: 'xv_prio', schemaName: 'xv_Prio', type: 'choice', label: 'xv_Prio', choice: 'xv_priority' }));
    await screen.findByRole('table', { name: en['table.tab.columns'] });
    expect(entityReads).toBe(2);
  });

  it('Add_lookup_column_creates_the_relationship_to_the_primary_id_of_the_target', async () => {
    let posted: unknown;
    server.use(
      onPath('get', "EntityDefinitions(LogicalName='contact')", () => HttpResponse.json({ PrimaryIdAttribute: 'contactid' })),
      http.post(`${API}/RelationshipDefinitions`, async ({ request }) => {
        posted = await request.json();
        return new HttpResponse(null, { status: 204 });
      }),
    );
    const user = userEvent.setup();
    render(<App initialEntries={['/manage/tables/xv_project']} />);
    await user.click(await screen.findByRole('button', { name: en['column.add.open'] }));
    await user.type(await screen.findByLabelText(/^Schema name/), 'xv_Owner');
    await user.selectOptions(screen.getByLabelText(/^Type/), 'lookup');
    await screen.findByRole('option', { hidden: true, name: 'contact' });
    await user.selectOptions(screen.getByLabelText(/^Target table/), 'contact');
    await user.click(screen.getByRole('button', { hidden: true, name: en['column.add.submit'] }));
    expect(await screen.findByText('Column "xv_owner" added.')).toBeInTheDocument();
    expect(posted).toMatchObject({ '@odata.type': 'Microsoft.Dynamics.CRM.OneToManyRelationshipMetadata', ReferencedAttribute: 'contactid', SchemaName: 'xv_project_xv_owner_contact' });
    expect(posted).toEqual(
      lookupBody({ name: 'xv_owner', schemaName: 'xv_Owner', type: 'lookup', label: 'xv_Owner', target: 'contact' }, 'xv_project', 'xv_project_xv_owner_contact', 'contactid'),
    );
    await screen.findByRole('table', { name: en['table.tab.columns'] });
    expect(entityReads).toBe(2);
  });

  it('shows the server error of a refused column', async () => {
    server.use(
      onPath('post', "EntityDefinitions(LogicalName='xv_project')/Attributes", () =>
        HttpResponse.json({ error: { code: '0x80048403', message: 'Column already exists.' } }, { status: 400 }),
      ),
    );
    const user = userEvent.setup();
    render(<App initialEntries={['/manage/tables/xv_project']} />);
    await user.click(await screen.findByRole('button', { name: en['column.add.open'] }));
    await user.type(await screen.findByLabelText(/^Schema name/), 'xv_Name');
    await user.click(screen.getByRole('button', { hidden: true, name: en['column.add.submit'] }));
    expect(await screen.findByText('0x80048403')).toBeInTheDocument();
    expect(screen.getByText('Column already exists.')).toBeInTheDocument();
  });

  it('shows a validation error for a non-numeric length and a too-long relationship name', async () => {
    const user = userEvent.setup();
    render(<App initialEntries={['/manage/tables/xv_project']} />);
    await user.click(await screen.findByRole('button', { name: en['column.add.open'] }));
    await typeSchemaNameAfterPrefix(user, 'xv_Notes');
    await user.type(screen.getByLabelText(en['column.field.maxLength']), '25x');
    expect(await screen.findByText(en['column.err.maxLength'])).toBeInTheDocument();
    expect(screen.getByRole('button', { hidden: true, name: en['column.add.submit'] })).toBeDisabled();
    await user.selectOptions(screen.getByLabelText(/^Type/), 'decimal');
    await user.type(await screen.findByLabelText(en['column.field.precision']), '99');
    expect(await screen.findByText(en['column.err.precision'])).toBeInTheDocument();
  });

  it('refuses a column schema name without a publisher prefix', async () => {
    const user = userEvent.setup();
    render(<App initialEntries={['/manage/tables/xv_project']} />);
    await user.click(await screen.findByRole('button', { name: en['column.add.open'] }));
    await typeSchemaNameAfterPrefix(user, 'Notes');
    expect(await screen.findByText(en['schema.err.prefix'])).toBeInTheDocument();
    expect(screen.getByRole('button', { hidden: true, name: en['column.add.submit'] })).toBeDisabled();
  });

  it('refuses a relationship name longer than 100 characters', async () => {
    // Logical names are at most 64 characters: the overflow needs a long target table too.
    const target = 'xv_' + 'b'.repeat(40);
    installMetadata([project, contact, rawEntity(target, `${target}s`, [])], relationships);
    const user = userEvent.setup();
    render(<App initialEntries={['/manage/tables/xv_project']} />);
    await user.click(await screen.findByRole('button', { name: en['column.add.open'] }));
    await typeSchemaNameAfterPrefix(user, 'xv_' + 'A'.repeat(60));
    await user.selectOptions(screen.getByLabelText(/^Type/), 'lookup');
    await screen.findByRole('option', { hidden: true, name: target });
    await user.selectOptions(screen.getByLabelText(/^Target table/), target);
    expect(await screen.findByText(/is longer than 100 characters/)).toBeInTheDocument();
    expect(screen.getByRole('button', { hidden: true, name: en['column.add.submit'] })).toBeDisabled();
  });
  it('TableDetail toggles table and column auditing and disables locked switches', async () => {
    const audit = (value: boolean, canBeChanged = true) => ({ IsAuditEnabled: { Value: value, CanBeChanged: canBeChanged } });
    let tableOn = false;
    let nameOn = false;
    const attributes = () => [
      rawAttr('xv_projectid', 'Uniqueidentifier', { IsPrimaryId: true, ...audit(false) }),
      rawAttr('xv_name', 'String', { IsPrimaryName: true, display: 'Name', ...audit(nameOn) }),
      rawAttr('xv_parent', 'Lookup', { display: 'Parent', Targets: ['contact'], ...audit(false, false) }),
    ];
    const entity = () => rawEntity('xv_project', 'xv_projects', attributes(), { PrimaryIdAttribute: 'xv_projectid', PrimaryNameAttribute: 'xv_name', display: 'Project', ...audit(tableOn) });
    const writes: { path: string; body: unknown }[] = [];
    server.use(
      onPath('get', "EntityDefinitions(LogicalName='xv_project')", () => HttpResponse.json(entity())),
      onPath('get', "EntityDefinitions(LogicalName='xv_project')/Attributes(LogicalName='xv_name')", () => HttpResponse.json(attributes()[1])),
      onPath('put', "EntityDefinitions(LogicalName='xv_project')", async ({ request }) => {
        const body = (await request.json()) as { IsAuditEnabled: { Value: boolean } };
        writes.push({ path: 'table', body });
        tableOn = body.IsAuditEnabled.Value;
        return new HttpResponse(null, { status: 204 });
      }),
      onPath('put', "EntityDefinitions(LogicalName='xv_project')/Attributes(LogicalName='xv_name')", async ({ request }) => {
        const body = (await request.json()) as { IsAuditEnabled: { Value: boolean } };
        writes.push({ path: 'column', body });
        nameOn = body.IsAuditEnabled.Value;
        return new HttpResponse(null, { status: 204 });
      }),
    );
    const user = userEvent.setup();
    render(<App initialEntries={['/manage/tables/xv_project']} />);
    const tableSwitch = await screen.findByRole('switch', { name: en['audit.table.switch'] });
    await waitFor(() => expect(screen.getByRole('table', { name: en['table.tab.columns'] })).toBeInTheDocument());
    expect(tableSwitch).not.toBeChecked();

    await user.click(tableSwitch);
    await waitFor(() => expect(screen.getByRole('switch', { name: en['audit.table.switch'] })).toBeChecked());
    // The view is refreshed in place: the same switch stays mounted and no spinner replaces the table.
    expect(tableSwitch.isConnected).toBe(true);
    expect(screen.queryByText(en['common.loading'])).toBeNull();
    expect(writes[0].body).toMatchObject({ LogicalName: 'xv_project', IsAuditEnabled: { Value: true, CanBeChanged: true } });

    await user.click(screen.getByRole('switch', { name: 'Audit xv_name' }));
    await waitFor(() => expect(screen.getByRole('switch', { name: 'Audit xv_name' })).toBeChecked());
    expect(writes[1]).toMatchObject({ path: 'column', body: { IsAuditEnabled: { Value: true } } });

    // A column the server has fixed cannot be switched; the reason is shown on hover.
    const locked = screen.getByRole('switch', { name: 'Audit xv_parent' });
    expect(locked).toBeDisabled();
    await user.hover(screen.getByTestId('audit-lock-xv_parent'));
    expect(await screen.findByRole('tooltip')).toHaveTextContent(en['audit.locked']);
    expect(writes).toHaveLength(2);
  });
  it('TableDetail toggles Auto-create access teams with a PUT of the whole definition', async () => {
    let on = false;
    const puts: Record<string, unknown>[] = [];
    const entity = () =>
      rawEntity('xv_project', 'xv_projects', [rawAttr('xv_projectid', 'Uniqueidentifier', { IsPrimaryId: true })], {
        PrimaryIdAttribute: 'xv_projectid',
        PrimaryNameAttribute: 'xv_name',
        display: 'Project',
        AutoCreateAccessTeams: on,
      });
    server.use(
      onPath('get', "EntityDefinitions(LogicalName='xv_project')", () => HttpResponse.json(entity())),
      onPath('put', "EntityDefinitions(LogicalName='xv_project')", async ({ request }) => {
        const body = (await request.json()) as { AutoCreateAccessTeams: boolean };
        puts.push(body);
        on = body.AutoCreateAccessTeams;
        return new HttpResponse(null, { status: 204 });
      }),
    );
    const user = userEvent.setup();
    render(<App initialEntries={['/manage/tables/xv_project']} />);
    const toggle = await screen.findByRole('switch', { name: en['accessTeams.enable'] });
    await waitFor(() => expect(screen.getByRole('table', { name: en['table.tab.columns'] })).toBeInTheDocument());
    expect(toggle).not.toBeChecked();

    await user.click(toggle);

    await waitFor(() => expect(screen.getByRole('switch', { name: en['accessTeams.enable'] })).toBeChecked());
    expect(puts).toHaveLength(1);
    expect(puts[0]).toMatchObject({ LogicalName: 'xv_project', AutoCreateAccessTeams: true });
  });
  describe('duplicate detection', () => {
    const dupEntity = (on: boolean) =>
      rawEntity('xv_project', 'xv_projects', [rawAttr('xv_projectid', 'Uniqueidentifier', { IsPrimaryId: true })], {
        PrimaryIdAttribute: 'xv_projectid',
        PrimaryNameAttribute: 'xv_name',
        display: 'Project',
        IsDuplicateDetectionEnabled: { Value: on, CanBeChanged: true, ManagedPropertyLogicalName: 'canmodifyduplicatedetection' },
      });

    it('table switch toggles flag', async () => {
      let on = false;
      const puts: Record<string, unknown>[] = [];
      server.use(
        onPath('get', "EntityDefinitions(LogicalName='xv_project')", () => HttpResponse.json(dupEntity(on))),
        onPath('put', "EntityDefinitions(LogicalName='xv_project')", async ({ request }) => {
          const body = (await request.json()) as { IsDuplicateDetectionEnabled: { Value: boolean } };
          puts.push(body);
          on = body.IsDuplicateDetectionEnabled.Value;
          return new HttpResponse(null, { status: 204 });
        }),
      );
      const user = userEvent.setup();
      render(<App initialEntries={['/manage/tables/xv_project']} />);
      const toggle = await screen.findByRole('switch', { name: en['duplicates.table.switch'] });
      await waitFor(() => expect(screen.getByRole('table', { name: en['table.tab.columns'] })).toBeInTheDocument());
      expect(toggle).not.toBeChecked();

      await user.click(toggle);

      await waitFor(() => expect(screen.getByRole('switch', { name: en['duplicates.table.switch'] })).toBeChecked());
      expect(puts).toHaveLength(1);
      expect(puts[0]).toMatchObject({ LogicalName: 'xv_project', IsDuplicateDetectionEnabled: { Value: true, CanBeChanged: true } });
    });

    it('shows the server refusal and keeps the switch as stored', async () => {
      server.use(
        onPath('get', "EntityDefinitions(LogicalName='xv_project')", () => HttpResponse.json(dupEntity(true))),
        onPath('put', "EntityDefinitions(LogicalName='xv_project')", () =>
          HttpResponse.json({ error: { code: '0x80040203', message: 'Duplicate detection cannot be turned off here.' } }, { status: 400 }),
        ),
      );
      const user = userEvent.setup();
      render(<App initialEntries={['/manage/tables/xv_project']} />);
      const toggle = await screen.findByRole('switch', { name: en['duplicates.table.switch'] });
      await waitFor(() => expect(screen.getByRole('table', { name: en['table.tab.columns'] })).toBeInTheDocument());

      await user.click(toggle);

      expect(await screen.findByText(/cannot be turned off here/)).toBeInTheDocument();
      expect(screen.getByRole('switch', { name: en['duplicates.table.switch'] })).toBeChecked();
    });
  });
  describe('track changes', () => {
    const trackedEntity = (on: boolean, can = true) =>
      rawEntity('xv_project', 'xv_projects', [rawAttr('xv_projectid', 'Uniqueidentifier', { IsPrimaryId: true })], {
        PrimaryIdAttribute: 'xv_projectid',
        PrimaryNameAttribute: 'xv_name',
        display: 'Project',
        ChangeTrackingEnabled: on,
        CanChangeTrackingBeEnabled: { Value: can },
      });
    const trackSwitchName = en['changeTracking.enable'];

    async function open() {
      render(<App initialEntries={['/manage/tables/xv_project']} />);
      const toggle = await screen.findByRole('switch', { name: trackSwitchName });
      await waitFor(() => expect(screen.getByRole('table', { name: en['table.tab.columns'] })).toBeInTheDocument());
      return toggle;
    }

    it('shows track changes switch', async () => {
      server.use(onPath('get', "EntityDefinitions(LogicalName='xv_project')", () => HttpResponse.json(trackedEntity(false))));
      const toggle = await open();
      expect(toggle).not.toBeChecked();
      expect(toggle).toBeEnabled();
    });

    it('confirms before enabling', async () => {
      let on = false;
      const puts: Record<string, unknown>[] = [];
      server.use(
        onPath('get', "EntityDefinitions(LogicalName='xv_project')", () => HttpResponse.json(trackedEntity(on))),
        onPath('put', "EntityDefinitions(LogicalName='xv_project')", async ({ request }) => {
          const body = (await request.json()) as { ChangeTrackingEnabled: boolean };
          puts.push(body);
          on = body.ChangeTrackingEnabled;
          return new HttpResponse(null, { status: 204 });
        }),
      );
      const user = userEvent.setup();
      const toggle = await open();
      await user.click(toggle);
      expect(await screen.findByText(en['changeTracking.confirmBody'])).toBeInTheDocument();
      expect(puts).toHaveLength(0);
      await user.click(screen.getByRole('button', { name: en['changeTracking.confirmButton'] }));
      await waitFor(() => expect(screen.getByRole('switch', { name: trackSwitchName })).toBeChecked());
      expect(puts).toHaveLength(1);
      expect(puts[0]).toMatchObject({ LogicalName: 'xv_project', ChangeTrackingEnabled: true });
      expect(puts[0]).not.toHaveProperty('CanChangeTrackingBeEnabled');
    });

    it('switch disabled when on', async () => {
      server.use(onPath('get', "EntityDefinitions(LogicalName='xv_project')", () => HttpResponse.json(trackedEntity(true))));
      const toggle = await open();
      expect(toggle).toBeChecked();
      expect(toggle).toBeDisabled();
      expect(screen.getByText(en['changeTracking.alreadyOn'])).toBeInTheDocument();
    });

    it('switch disabled when not eligible', async () => {
      server.use(onPath('get', "EntityDefinitions(LogicalName='xv_project')", () => HttpResponse.json(trackedEntity(false, false))));
      const toggle = await open();
      expect(toggle).toBeDisabled();
      expect(screen.getByText(en['changeTracking.notEligible'])).toBeInTheDocument();
    });

    it('shows server refusal', async () => {
      server.use(
        onPath('get', "EntityDefinitions(LogicalName='xv_project')", () => HttpResponse.json(trackedEntity(false))),
        onPath('put', "EntityDefinitions(LogicalName='xv_project')", () =>
          HttpResponse.json({ error: { code: '0x80048403', message: 'Change tracking cannot be enabled here.' } }, { status: 400 }),
        ),
      );
      const user = userEvent.setup();
      const toggle = await open();
      await user.click(toggle);
      await user.click(await screen.findByRole('button', { name: en['changeTracking.confirmButton'] }));
      expect(await screen.findByText('Change tracking cannot be enabled here.')).toBeInTheDocument();
      expect(screen.getByRole('switch', { name: trackSwitchName })).not.toBeChecked();
    });
  });
  it('the Secured switch writes IsSecured and is disabled for a column that cannot be secured', async () => {
    let budgetSecured = false;
    const attributes = () => [
      rawAttr('xv_projectid', 'Uniqueidentifier', { IsPrimaryId: true, CanBeSecuredForRead: false, IsSecured: false }),
      rawAttr('xv_name', 'String', { IsPrimaryName: true, display: 'Name', CanBeSecuredForRead: false, IsSecured: false }),
      rawAttr('xv_budget', 'Money', { display: 'Budget', CanBeSecuredForRead: true, IsSecured: budgetSecured }),
    ];
    const entity = () => rawEntity('xv_project', 'xv_projects', attributes(), { PrimaryIdAttribute: 'xv_projectid', PrimaryNameAttribute: 'xv_name', display: 'Project' });
    let reads = 0;
    const puts: Record<string, unknown>[] = [];
    server.use(
      onPath('get', "EntityDefinitions(LogicalName='xv_project')", () => {
        reads++;
        return HttpResponse.json(entity());
      }),
      onPath('get', "EntityDefinitions(LogicalName='xv_project')/Attributes(LogicalName='xv_budget')", () =>
        HttpResponse.json({ '@odata.context': 'ctx', ...attributes()[2], CanBeSecuredForCreate: true, CanBeSecuredForUpdate: true }),
      ),
      onPath('put', "EntityDefinitions(LogicalName='xv_project')/Attributes(LogicalName='xv_budget')", async ({ request }) => {
        const body = (await request.json()) as Record<string, unknown>;
        puts.push(body);
        budgetSecured = body.IsSecured === true;
        return new HttpResponse(null, { status: 204 });
      }),
    );
    const user = userEvent.setup();
    render(<App initialEntries={['/manage/tables/xv_project']} />);
    const budget = await screen.findByRole('switch', { name: 'Secure xv_budget' });
    await waitFor(() => expect(reads).toBe(1));
    expect(budget).not.toBeChecked();
    expect(budget).toBeEnabled();
    expect(screen.getByRole('switch', { name: 'Secure xv_name' })).toBeDisabled();
    expect(screen.getByRole('switch', { name: 'Secure xv_projectid' })).toBeDisabled();

    await user.click(budget);
    await waitFor(() => expect(screen.getByRole('switch', { name: 'Secure xv_budget' })).toBeChecked());
    expect(reads).toBe(2);
    expect(puts).toHaveLength(1);
    expect(puts[0]).toMatchObject({ LogicalName: 'xv_budget', IsSecured: true });
    expect(puts[0]).not.toHaveProperty('CanBeSecuredForRead');
    expect(puts[0]).not.toHaveProperty('@odata.context');
  });

  describe('notes and activities', () => {
    const flagged = (flags: { IsActivity?: boolean; HasNotes?: boolean; HasActivities?: boolean }) =>
      rawEntity('xv_project', 'xv_projects', [rawAttr('xv_projectid', 'Uniqueidentifier', { IsPrimaryId: true })], {
        PrimaryIdAttribute: 'xv_projectid',
        PrimaryNameAttribute: 'xv_name',
        display: 'Project',
        IsActivity: false,
        HasNotes: false,
        HasActivities: false,
        ...flags,
      });

    async function open() {
      render(<App initialEntries={['/manage/tables/xv_project']} />);
      await screen.findByRole('switch', { name: en['table.notes.enable'] });
      await waitFor(() => expect(screen.getByRole('table', { name: en['table.tab.columns'] })).toBeInTheDocument());
    }

    it('switches notes on with confirmation', async () => {
      let notes = false;
      const puts: Record<string, unknown>[] = [];
      server.use(
        onPath('get', "EntityDefinitions(LogicalName='xv_project')", () =>
          HttpResponse.json({ '@odata.context': 'ctx', ...flagged({ HasNotes: notes }), CanChangeTrackingBeEnabled: { Value: true } }),
        ),
        onPath('put', "EntityDefinitions(LogicalName='xv_project')", async ({ request }) => {
          const body = (await request.json()) as Record<string, unknown>;
          puts.push(body);
          notes = body.HasNotes === true;
          return new HttpResponse(null, { status: 204 });
        }),
      );
      const user = userEvent.setup();
      await open();
      const toggle = screen.getByRole('switch', { name: en['table.notes.enable'] });
      expect(toggle).not.toBeChecked();
      expect(toggle).toBeEnabled();

      await user.click(toggle);
      expect(await screen.findByText(en['table.notes.confirmBody'])).toBeInTheDocument();
      expect(puts).toHaveLength(0);
      await user.click(await screen.findByRole('button', { name: en['table.flag.confirmButton'] }));
      await waitFor(() => expect(screen.getByRole('switch', { name: en['table.notes.enable'] })).toBeChecked());
      expect(puts).toHaveLength(1);
      expect(puts[0]).toMatchObject({ LogicalName: 'xv_project', HasNotes: true, HasActivities: false });
      expect(puts[0]).not.toHaveProperty('CanChangeTrackingBeEnabled');
      expect(puts[0]).not.toHaveProperty('@odata.context');
      expect(screen.getByRole('switch', { name: en['table.notes.enable'] })).toBeDisabled();
      await waitFor(() => expect(screen.queryByRole('dialog', { hidden: true })).toBeNull());
    });

    it('cancelling the confirmation sends nothing', async () => {
      let puts = 0;
      server.use(
        onPath('get', "EntityDefinitions(LogicalName='xv_project')", () => HttpResponse.json(flagged({}))),
        onPath('put', "EntityDefinitions(LogicalName='xv_project')", () => {
          puts++;
          return new HttpResponse(null, { status: 204 });
        }),
      );
      const user = userEvent.setup();
      await open();
      await user.click(screen.getByRole('switch', { name: en['table.notes.enable'] }));
      const dialog = await screen.findByRole('dialog');
      await user.click(within(dialog).getByRole('button', { name: en['common.cancel'] }));
      // The dialog keeps its text while it closes (no empty dialog during the exit transition)...
      expect(dialog).toHaveTextContent(en['table.notes.confirmBody']);
      // ...and is then gone, hidden ones included.
      await waitFor(() => expect(screen.queryByRole('dialog', { hidden: true })).toBeNull());
      expect(screen.getByRole('switch', { name: en['table.notes.enable'] })).not.toBeChecked();
      expect(puts).toBe(0);
    });

    it('shows the refusal of the server', async () => {
      server.use(
        onPath('get', "EntityDefinitions(LogicalName='xv_project')", () => HttpResponse.json(flagged({}))),
        onPath('put', "EntityDefinitions(LogicalName='xv_project')", () =>
          HttpResponse.json({ error: { code: '0x80040203', message: 'HasActivities cannot be set here.' } }, { status: 400 }),
        ),
      );
      const user = userEvent.setup();
      await open();
      await user.click(screen.getByRole('switch', { name: en['table.activities.enable'] }));
      expect(await screen.findByText(en['table.activities.confirmBody'])).toBeInTheDocument();
      await user.click(screen.getByRole('button', { name: en['table.flag.confirmButton'] }));
      expect(await screen.findByText('HasActivities cannot be set here.')).toBeInTheDocument();
      expect(screen.getByRole('switch', { name: en['table.activities.enable'] })).not.toBeChecked();
    });

    it('switch is disabled once on', async () => {
      server.use(onPath('get', "EntityDefinitions(LogicalName='xv_project')", () => HttpResponse.json(flagged({ HasNotes: true, HasActivities: true }))));
      await open();
      for (const name of [en['table.notes.enable'], en['table.activities.enable']]) {
        const toggle = screen.getByRole('switch', { name });
        expect(toggle).toBeChecked();
        expect(toggle).toBeDisabled();
      }
      expect(screen.getByText(en['table.notes.alreadyOn'])).toBeInTheDocument();
      expect(screen.getByText(en['table.activities.alreadyOn'])).toBeInTheDocument();
    });

    it('activities switch hidden on activity tables', async () => {
      server.use(onPath('get', "EntityDefinitions(LogicalName='xv_project')", () => HttpResponse.json(flagged({ IsActivity: true, HasNotes: true }))));
      await open();
      expect(screen.queryByRole('switch', { name: en['table.activities.enable'] })).toBeNull();
      expect(screen.getByRole('switch', { name: en['table.notes.enable'] })).toBeChecked();
      expect(screen.getByRole('switch', { name: en['table.notes.enable'] })).toBeDisabled();
      expect(screen.getByText(en['table.isActivity'])).toBeInTheDocument();
    });

    it('switches queues on with confirmation, sending the managed flag', async () => {
      let queues = false;
      const puts: Record<string, unknown>[] = [];
      server.use(
        onPath('get', "EntityDefinitions(LogicalName='xv_project')", () =>
          HttpResponse.json({
            ...flagged({}),
            IsValidForQueue: { Value: queues, CanBeChanged: true, ManagedPropertyLogicalName: 'canmodifyqueuesettings' },
          }),
        ),
        onPath('put', "EntityDefinitions(LogicalName='xv_project')", async ({ request }) => {
          const body = (await request.json()) as Record<string, unknown>;
          puts.push(body);
          queues = (body.IsValidForQueue as { Value?: boolean }).Value === true;
          return new HttpResponse(null, { status: 204 });
        }),
      );
      const user = userEvent.setup();
      await open();
      const toggle = screen.getByRole('switch', { name: en['table.queues.enable'] });
      expect(toggle).not.toBeChecked();

      await user.click(toggle);
      expect(await screen.findByText(en['table.queues.confirmBody'])).toBeInTheDocument();
      await user.click(await screen.findByRole('button', { name: en['table.flag.confirmButton'] }));
      await waitFor(() => expect(screen.getByRole('switch', { name: en['table.queues.enable'] })).toBeChecked());
      expect(puts).toHaveLength(1);
      expect(puts[0]).toMatchObject({
        LogicalName: 'xv_project',
        HasNotes: false,
        IsValidForQueue: { Value: true, CanBeChanged: true, ManagedPropertyLogicalName: 'canmodifyqueuesettings' },
      });
      expect(screen.getByRole('switch', { name: en['table.queues.enable'] })).toBeDisabled();
      expect(screen.getByText(en['table.queues.alreadyOn'])).toBeInTheDocument();
    });

    it('queues switch hidden on the tables that cannot be queued', async () => {
      server.use(
        onPath('get', "EntityDefinitions(LogicalName='annotation')", () =>
          HttpResponse.json({ ...flagged({}), LogicalName: 'annotation', IsValidForQueue: { Value: false } }),
        ),
      );
      render(<App initialEntries={['/manage/tables/annotation']} />);
      await screen.findByRole('switch', { name: en['table.notes.enable'] });
      expect(screen.queryByRole('switch', { name: en['table.queues.enable'] })).toBeNull();
    });

    it('shows no switch when the server does not report the settings', async () => {
      render(<App initialEntries={['/manage/tables/xv_project']} />);
      await screen.findByRole('table', { name: en['table.tab.columns'] });
      expect(screen.queryByRole('switch', { name: en['table.notes.enable'] })).toBeNull();
      expect(screen.queryByRole('switch', { name: en['table.activities.enable'] })).toBeNull();
      expect(screen.queryByRole('switch', { name: en['table.queues.enable'] })).toBeNull();
    });
  });
});


describe('TableDetail views tab', () => {
  it('shows the Views and Forms tabs after Relationships', async () => {
    render(<App initialEntries={['/manage/tables/xv_project']} />);
    const tabs = (await screen.findAllByRole('tab')).map((t) => t.getAttribute('aria-label') ?? within(t).getAllByText(/./)[0].textContent);
    expect(tabs).toEqual([en['table.tab.columns'], en['table.tab.relationships'], en['table.tab.views'], en['table.tab.forms'], en['table.tab.records']]);
  });
});

describe('TableDetail choice colours', () => {
  it('offers Colours on choice, choices and status columns only, and opens the colour dialog', async () => {
    const options = { IsGlobal: false, Options: [{ Value: 1, Label: lbl('Low') }] };
    installMetadata([
      rawEntity('xv_ticket', 'xv_tickets', [
        rawAttr('xv_ticketid', 'Uniqueidentifier', { IsPrimaryId: true }),
        rawAttr('xv_name', 'String', { IsPrimaryName: true }),
        rawAttr('xv_priority', 'Picklist', { display: 'Priority', OptionSet: options }),
        rawAttr('xv_tags', 'MultiSelectPicklist', { OptionSet: options }),
        rawAttr('statuscode', 'Status', { OptionSet: options }),
        rawAttr('statecode', 'State', { OptionSet: options }),
      ]),
    ]);
    const user = userEvent.setup();
    render(<App initialEntries={['/manage/tables/xv_ticket']} />);
    await screen.findByRole('table', { name: en['table.tab.columns'] });
    const offered = screen
      .getAllByRole('button', { name: /^Colours of / })
      .map((b) => b.getAttribute('aria-label'));
    expect(offered).toEqual(['xv_priority', 'xv_tags', 'statuscode'].map((column) => en['choiceColors.openFor'].replace('{column}', column)));
    await user.click(screen.getByRole('button', { name: en['choiceColors.openFor'].replace('{column}', 'xv_priority') }));
    expect(await screen.findByRole('dialog', { name: en['choiceColors.title'].replace('{column}', 'Priority') })).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: en['common.cancel'] }));
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
  });
});

