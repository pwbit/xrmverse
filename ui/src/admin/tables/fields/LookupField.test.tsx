import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { describe, expect, it, vi } from 'vitest';
import { RecordOpenerContext } from '../../../apps/record/recordOpener';
import type { DialogHost } from '../../../client-api/DialogHost';
import { mockApps } from '../../../apps/testSupport';
import { Providers } from '../../../renderers/classic/form/formTestSupport';
import { NotificationsProvider } from '../../../shell/notify/Notifications';
import { server } from '../../../test/server';
import { onPath } from '../../security/testSupport';
import type { FieldValue } from '../formValues';
import { attr } from '../testAttrs';
import type { FieldContext } from './fieldProps';
import { LookupField } from './LookupField';

const parent = attr('xv_parentid', { kind: 'lookup', targets: ['xv_node'], displayName: 'Parent' });
const context: FieldContext = {
  table: 'xv_project',
  lookups: [{ schemaName: 'p', referencingEntity: 'xv_project', referencingAttribute: 'xv_parentid', referencedEntity: 'xv_node', nav: 'xv_parentid' }],
  globalChoices: {},
};

function setup() {
  mockApps();
  const searches: URLSearchParams[] = [];
  server.use(
    onPath('get', "EntityDefinitions(LogicalName='xv_node')", () =>
      HttpResponse.json({ EntitySetName: 'xv_nodes', PrimaryIdAttribute: 'xv_nodeid', PrimaryNameAttribute: 'xv_name' }),
    ),
    http.get('*/api/data/v9.2/xv_nodes', ({ request }) => {
      searches.push(new URL(request.url).searchParams);
      return HttpResponse.json({ value: [{ xv_nodeid: 'n1', xv_name: 'Design' }] });
    }),
  );
  return searches;
}

function renderLookup(preSearch?: (target: string) => string[]) {
  const picked: FieldValue[] = [];
  render(
    <Providers>
      <NotificationsProvider>
        <LookupField attr={parent} value={null} onChange={(v) => picked.push(v)} disabled={false} context={context} preSearch={preSearch} />
      </NotificationsProvider>
    </Providers>,
  );
  return picked;
}

describe('LookupField', () => {
  it('without_pre_search_searches_with_odata_as_before', async () => {
    const searches = setup();
    const picked = renderLookup();
    await userEvent.click(await screen.findByRole('button', { name: 'Choose Parent' }));
    await userEvent.click(await screen.findByRole('option', { name: 'Design' }));
    expect(searches).toHaveLength(1);
    expect(searches[0]!.get('fetchXml')).toBeNull();
    expect(searches[0]!.get('$select')).toBe('xv_nodeid,xv_name');
    expect(picked).toEqual([{ id: 'n1', name: 'Design', target: 'xv_node', set: 'xv_nodes', nav: 'xv_parentid' }]);
  });

  it('pre_search_filters_are_anded_to_the_search', async () => {
    const searches = setup();
    const targets: string[] = [];
    renderLookup((target) => {
      targets.push(target);
      return [`<filter type="and"><condition attribute="xv_projectid" operator="eq" value="p1"/></filter>`];
    });
    await userEvent.click(await screen.findByRole('button', { name: 'Choose Parent' }));
    expect(await screen.findByRole('option', { name: 'Design' })).toBeInTheDocument();
    expect(targets).toEqual(['xv_node']);
    const xml = new DOMParser().parseFromString(searches.at(-1)!.get('fetchXml')!, 'application/xml');
    const top = xml.querySelector('entity > filter')!;
    expect(xml.querySelector('entity')!.getAttribute('name')).toBe('xv_node');
    expect(top.getAttribute('type')).toBe('and');
    expect(top.querySelector('filter > condition')!.getAttribute('attribute')).toBe('xv_projectid');
    await userEvent.type(screen.getByRole('textbox', { name: 'Parent: search' }), 'De');
    await waitFor(() => expect(targets).toHaveLength(2));
    await waitFor(() =>
      expect(new DOMParser().parseFromString(searches.at(-1)!.get('fetchXml')!, 'application/xml').querySelector('entity > filter > condition')!.getAttribute('value')).toBe('%De%'),
    );
  });

  it('pre_search_without_filters_keeps_the_odata_search', async () => {
    const searches = setup();
    renderLookup(() => []);
    await userEvent.click(await screen.findByRole('button', { name: 'Choose Parent' }));
    expect(await screen.findByRole('option', { name: 'Design' })).toBeInTheDocument();
    expect(searches.every((s) => s.get('fetchXml') === null)).toBe(true);
  });

  it('a_value_opens_its_record_through_the_app_opener_and_shows_the_saved_name', async () => {
    setup();
    const record = vi.fn(async () => ({ entityType: 'xv_node', id: 'n1', name: 'Design v2' }));
    const value: FieldValue = { id: 'n1', name: 'Design', target: 'xv_node', set: 'xv_nodes', nav: 'xv_parentid' };
    render(
      <Providers>
        <RecordOpenerContext.Provider value={{ appId: 'app1', mode: 'sidepane', dialogs: { record } as unknown as DialogHost }}>
          <LookupField attr={parent} value={value} onChange={() => undefined} disabled context={context} />
        </RecordOpenerContext.Provider>
      </Providers>,
    );
    const input = await screen.findByRole('textbox', { name: 'Parent' });
    // A read-only lookup still opens its record.
    expect(input).not.toBeDisabled();
    await userEvent.click(input);
    expect(record).toHaveBeenCalledWith(expect.objectContaining({ url: expect.stringContaining('etn=xv_node&id=n1'), position: 2 }));
    await waitFor(() => expect(input).toHaveValue('Design v2'));
  });

  it('outside_an_app_the_value_is_plain_text', async () => {
    setup();
    const value: FieldValue = { id: 'n1', name: 'Design', target: 'xv_node', set: 'xv_nodes', nav: 'xv_parentid' };
    render(
      <Providers>
        <LookupField attr={parent} value={value} onChange={() => undefined} disabled context={context} />
      </Providers>,
    );
    expect(await screen.findByRole('textbox', { name: 'Parent' })).toBeDisabled();
  });
});
