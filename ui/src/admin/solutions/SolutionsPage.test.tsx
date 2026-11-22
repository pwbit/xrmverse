import { act, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { server } from '../../test/server';
import { App } from '../../shell/App';
import { en } from '../../shell/i18n/en';
import { API, onPath, signInAsAdministrator } from '../security/testSupport';
import { SOL1, SOL2, SOL_DEFAULT, serveSolutions, signInAsCustomizerOnly, solution } from './solutionTestSupport';

const readText = (blob: Blob) =>
  new Promise<string>((resolve, reject) => {
    const reader = new FileReader();
    reader.onerror = () => reject(reader.error);
    reader.onload = () => resolve(String(reader.result));
    reader.readAsText(blob);
  });

const selectRow = (user: ReturnType<typeof userEvent.setup>, name: string) =>
  user.click(screen.getByRole('radio', { hidden: true, name: `Select ${name}` }));

beforeEach(() => signInAsAdministrator());
afterEach(() => {
  sessionStorage.clear();
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
});

describe('SolutionsPage', () => {
  it('lists solutions with publisher, version and type, and no actions on the system solutions', async () => {
    serveSolutions([
      solution(),
      solution({ solutionid: SOL2, uniquename: 'managed_pack', friendlyname: 'Managed Pack', version: '3.0.0.0', ismanaged: true }),
      solution({ solutionid: SOL_DEFAULT, uniquename: 'Default', friendlyname: 'Default Solution', version: '1.0' }),
    ]);
    const user = userEvent.setup();
    render(<App initialEntries={['/manage/solutions']} />);
    const row = (await screen.findByText('Sales Kit')).closest('tr') as HTMLElement;
    expect(await within(row).findByText('Acme Publisher')).toBeInTheDocument();
    expect(within(row).getByText('1.2.0.0')).toBeInTheDocument();
    expect(within(row).getByText(en['solutions.type.unmanaged'])).toBeInTheDocument();
    const managed = screen.getByText('Managed Pack').closest('tr') as HTMLElement;
    expect(within(managed).getByText(en['solutions.type.managed'])).toBeInTheDocument();
    // The rows hold no commands: they are on the command bar, for the selected row.
    expect(within(row).queryByRole('button')).toBeNull();
    // A managed solution cannot be exported or deleted; it is uninstalled and its layers can be listed. Default has no actions.
    await selectRow(user, 'Managed Pack');
    expect(screen.queryByRole('button', { name: /^Export/ })).toBeNull();
    expect(screen.getByRole('button', { name: en['solutions.uninstall.open'].replace('{name}', 'Managed Pack') })).toBeInTheDocument();
    await selectRow(user, 'Default Solution');
    expect(screen.queryByRole('button', { name: /Default Solution/ })).toBeNull();
  });

  it('Export_downloads_zip_with_version_name', async () => {
    serveSolutions();
    let body: unknown;
    server.use(
      http.post(`${API}/ExportSolution`, async ({ request }) => {
        body = await request.json();
        return HttpResponse.json({ ExportSolutionFile: btoa('PK-zip-bytes') });
      }),
    );
    const blobs: Blob[] = [];
    class StubUrl extends URL {
      static createObjectURL(blob: Blob | MediaSource) {
        blobs.push(blob as Blob);
        return 'blob:export';
      }
      static revokeObjectURL() {}
    }
    vi.stubGlobal('URL', StubUrl);
    const downloads: string[] = [];
    vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(function (this: HTMLAnchorElement) {
      downloads.push(this.download);
    });
    const user = userEvent.setup();
    render(<App initialEntries={['/manage/solutions']} />);
    await screen.findByText('Acme Publisher');
    await selectRow(user, 'Sales Kit');
    await user.click(screen.getByRole('button', { name: en['solutions.export.open'].replace('{name}', 'Sales Kit') }));
    await waitFor(() => expect(downloads).toEqual(['sales_kit_1_2_0_0.zip']));
    expect(body).toEqual({ SolutionName: 'sales_kit', Managed: false });
    expect(blobs).toHaveLength(1);
    expect(await readText(blobs[0])).toBe('PK-zip-bytes');
    expect(await screen.findByText(en['solutions.exported'].replace('{file}', 'sales_kit_1_2_0_0.zip'))).toBeInTheDocument();
  });

  it('Delete_solution_removes_only_the_container_after_confirmation', async () => {
    const counts = serveSolutions();
    const deleted: string[] = [];
    server.use(
      onPath('delete', `solutions(${SOL1})`, ({ request }) => {
        deleted.push(new URL(request.url).pathname);
        return new HttpResponse(null, { status: 204 });
      }),
    );
    const user = userEvent.setup();
    render(<App initialEntries={['/manage/solutions']} />);
    await screen.findByText('Acme Publisher');
    await selectRow(user, 'Sales Kit');
    await user.click(screen.getByRole('button', { name: en['solutions.delete.open'].replace('{name}', 'Sales Kit') }));
    // The dialog says what the delete does: the components stay in Default.
    expect(await screen.findByText(/Only the solution container is removed/, { selector: '*' })).toBeInTheDocument();
    expect(deleted).toEqual([]);
    await user.click(screen.getByRole('button', { hidden: true, name: en['solutions.delete.confirm'] }));
    await waitFor(() => expect(deleted).toEqual([`/api/data/v9.2/solutions(${SOL1})`]));
    // The list is read again after the delete.
    await waitFor(() => expect(counts.list).toBe(2));
    expect(await screen.findByText(en['solutions.deleted'].replace('{name}', 'Sales Kit'))).toBeInTheDocument();
  });

  it('Cascade_option_hidden_for_customizer', async () => {
    serveSolutions();
    signInAsCustomizerOnly();
    const user = userEvent.setup();
    render(<App initialEntries={['/manage/solutions']} />);
    await screen.findByText('Acme Publisher');
    await selectRow(user, 'Sales Kit');
    expect(screen.getByRole('button', { name: en['solutions.delete.open'].replace('{name}', 'Sales Kit') })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: en['solutions.cascade.open'].replace('{name}', 'Sales Kit') })).toBeNull();
  });

  it('Cascade_requires_typing_name_and_shows_deleted_and_kept', async () => {
    const counts = serveSolutions();
    const componentFilters: (string | null)[] = [];
    const requests: { url: string; method: string }[] = [];
    server.use(
      http.get(`${API}/solutioncomponents`, ({ request }) => {
        componentFilters.push(new URL(request.url).searchParams.get('$filter'));
        return HttpResponse.json({
          value: [
            { objectid: 'c1', componenttype: 1 },
            { objectid: 'c2', componenttype: 1 },
            { objectid: 'c3', componenttype: 61 },
          ],
        });
      }),
      http.delete('*/admin/solutions/:name', ({ request }) => {
        requests.push({ url: request.url, method: request.method });
        return HttpResponse.json({
          deleted: [
            { type: 1, name: 'new_widget' },
            { type: 61, name: 'new_widget.js' },
          ],
          kept: [{ type: 1, name: 'account', reason: 'in solution other_pack' }],
        });
      }),
    );
    const user = userEvent.setup();
    render(<App initialEntries={['/manage/solutions']} />);
    await screen.findByText('Acme Publisher');
    await selectRow(user, 'Sales Kit');
    await user.click(screen.getByRole('button', { name: en['solutions.cascade.open'].replace('{name}', 'Sales Kit') }));

    // The dialog says this is not the Dataverse delete, and lists the components with the "kept" caveat.
    expect(await screen.findByText(/not the Dataverse delete/, { selector: '*' })).toBeInTheDocument();
    expect(await screen.findByText(en['solutions.cascade.listTitle'], { selector: '*' })).toBeInTheDocument();
    expect(await screen.findByText('Table: 2')).toBeInTheDocument();
    expect(screen.getByText('Web resource: 1')).toBeInTheDocument();
    expect(componentFilters).toEqual([`_solutionid_value eq ${SOL1}`]);

    const confirm = screen.getByRole('button', { hidden: true, name: en['solutions.cascade.confirm'] });
    expect(confirm).toBeDisabled();
    const input = screen.getByLabelText(en['solutions.cascade.typeName'].replace('{name}', 'sales_kit'));
    await user.type(input, 'sales');
    expect(confirm).toBeDisabled();
    await user.type(input, '_kit');
    expect(confirm).toBeEnabled();
    expect(requests).toEqual([]);
    await user.click(confirm);

    await waitFor(() => expect(requests).toHaveLength(1));
    expect(requests[0].method).toBe('DELETE');
    expect(new URL(requests[0].url).pathname).toBe('/admin/solutions/sales_kit');
    expect(new URL(requests[0].url).searchParams.get('cascade')).toBe('true');

    // The result lists what was deleted and what was kept, with the reason.
    expect(await screen.findByText(en['solutions.cascade.resultTitle'].replace('{name}', 'sales_kit'))).toBeInTheDocument();
    expect(screen.getByText(en['solutions.cascade.deleted'].replace('{count}', '2'))).toBeInTheDocument();
    // Component types are named as in the pre-delete list, not shown as numbers.
    expect(screen.getByText('new_widget (Table)')).toBeInTheDocument();
    expect(screen.getByText('new_widget.js (Web resource)')).toBeInTheDocument();
    expect(screen.getByText(en['solutions.cascade.kept'].replace('{count}', '1'))).toBeInTheDocument();
    expect(screen.getByText('account (Table): in solution other_pack')).toBeInTheDocument();
    // Closing the result reads the list again.
    await act(async () => {
      await user.click(screen.getByRole('button', { hidden: true, name: en['common.close'] }));
    });
    await waitFor(() => expect(counts.list).toBe(2));
  });
});
