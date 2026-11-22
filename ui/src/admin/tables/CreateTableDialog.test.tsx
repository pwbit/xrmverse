import { render, screen, waitFor } from '@testing-library/react';
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

beforeEach(() => {
  signInAsAdministrator();
  installMetadata([project]);
});
afterEach(() => sessionStorage.clear());

describe('CreateTableDialog', () => {
  it('creates an activity table', async () => {
    let posted: unknown;
    server.use(
      http.post(`${API}/EntityDefinitions`, async ({ request }) => {
        posted = await request.json();
        return new HttpResponse(null, { status: 204 });
      }),
      onPath('get', "EntityDefinitions(LogicalName='xv_sms')", () =>
        HttpResponse.json(rawEntity('xv_sms', 'xv_smses', [], { display: 'SMS', IsActivity: true, HasNotes: true, HasActivities: false })),
      ),
    );
    const user = userEvent.setup();
    render(<App initialEntries={['/manage/tables']} />);
    await user.click(await screen.findByRole('button', { name: en['table.create.open'] }));
    await user.type(await screen.findByLabelText(/^Display name/), 'SMS');
    await user.type(screen.getByLabelText(/^Plural display name/), 'SMS messages');
    await waitFor(() => expect(screen.getByLabelText(/^Schema name/)).toHaveValue('xv_SMS'));
    await user.selectOptions(screen.getByLabelText(en['table.field.ownership']), 'organization');

    await user.click(screen.getByRole('switch', { name: en['table.field.activity'] }));
    // An activity's primary column is Subject and it is user owned: neither can be chosen.
    expect(screen.getByLabelText(en['table.field.primaryName'])).toBeDisabled();
    expect(screen.getByLabelText(en['table.field.primaryName'])).toHaveValue('Subject');
    expect(screen.getByLabelText(en['table.field.ownership'])).toBeDisabled();
    expect(screen.getByLabelText(en['table.field.ownership'])).toHaveValue('user');
    expect(screen.getByText(en['table.field.activity.hint'])).toBeInTheDocument();

    await user.click(screen.getByRole('button', { hidden: true, name: en['table.create.submit'] }));
    expect(await screen.findByText('Table "xv_sms" created.')).toBeInTheDocument();
    expect(posted).toMatchObject({ IsActivity: true, OwnershipType: 'UserOwned', Attributes: [{ LogicalName: 'subject', SchemaName: 'Subject', IsPrimaryName: true }] });
    expect(posted).toEqual(
      entityBody({
        name: 'xv_sms',
        schemaName: 'xv_SMS',
        label: 'SMS',
        pluralLabel: 'SMS messages',
        ownership: 'user',
        isActivity: true,
        primaryName: { name: 'subject', schemaName: 'Subject', label: 'Subject' },
      }),
    );
    await screen.findByRole('tab', { name: en['table.tab.columns'] });
  });

  it('an ordinary table states no activity setting', async () => {
    let posted: Record<string, unknown> | undefined;
    server.use(
      http.post(`${API}/EntityDefinitions`, async ({ request }) => {
        posted = (await request.json()) as Record<string, unknown>;
        return new HttpResponse(null, { status: 204 });
      }),
      onPath('get', "EntityDefinitions(LogicalName='xv_site')", () => HttpResponse.json(rawEntity('xv_site', 'xv_sites', [], { display: 'Site' }))),
    );
    const user = userEvent.setup();
    render(<App initialEntries={['/manage/tables']} />);
    await user.click(await screen.findByRole('button', { name: en['table.create.open'] }));
    await user.type(await screen.findByLabelText(/^Display name/), 'Site');
    await user.type(screen.getByLabelText(/^Plural display name/), 'Sites');
    await waitFor(() => expect(screen.getByLabelText(/^Schema name/)).toHaveValue('xv_Site'));
    // Switched on then off again: the primary name column is editable again.
    await user.click(screen.getByRole('switch', { name: en['table.field.activity'] }));
    await user.click(screen.getByRole('switch', { name: en['table.field.activity'] }));
    expect(screen.getByLabelText(en['table.field.primaryName'])).toBeEnabled();
    await user.click(screen.getByRole('button', { hidden: true, name: en['table.create.submit'] }));
    expect(await screen.findByText('Table "xv_site" created.')).toBeInTheDocument();
    expect(posted).not.toHaveProperty('IsActivity');
    expect(posted).toMatchObject({ Attributes: [{ LogicalName: 'xv_name', SchemaName: 'xv_Name' }] });
    await screen.findByRole('tab', { name: en['table.tab.columns'] });
  });
});
