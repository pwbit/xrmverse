import { render, screen, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { rawOneToMany } from '../../../admin/tables/tableTestSupport';
import { attr } from '../../../admin/tables/testAttrs';
import { onPath } from '../../../admin/security/testSupport';
import { mockApps, type AppsMock, type MockView } from '../../../apps/testSupport';
import { loadColumnKinds } from '../../../apps/views/columnKinds';
import type { Cell } from '../../../model/form';
import { server } from '../../../test/server';
import { PresentationProvider } from '../../modern/PresentationContext';
import { contextOf, field, formOf, Page, Providers, RECORD_ID, section, tab } from './formTestSupport';

vi.mock('../../../apps/views/columnKinds', () => ({ loadColumnKinds: vi.fn() }));

const GREEN = '#107C10';
const name = attr('xv_name', { isPrimaryName: true, displayName: 'Name' });
const RECORD = { xv_projectid: RECORD_ID, xv_name: 'Alpha' };
const VIEW_ID = 'a0000000-0000-0000-0000-0000000000a1';
const VIEW: MockView = {
  id: VIEW_ID,
  name: 'Active Tasks',
  entity: 'xv_task',
  isdefault: true,
  fetchxml: `<fetch version="1.0" mapping="logical"><entity name="xv_task"><attribute name="xv_title"/><attribute name="xv_priority"/><attribute name="xv_taskid"/><order attribute="xv_title" descending="false"/></entity></fetch>`,
  layoutxml: `<grid name="resultset" jump="xv_title" select="1" icon="1" preview="1"><row name="result" id="xv_taskid"><cell name="xv_title" width="200"/><cell name="xv_priority" width="100"/></row></grid>`,
};
const APPS: AppsMock = {
  savedQueries: [VIEW],
  tables: { xv_task: { entitySetName: 'xv_tasks', primaryId: 'xv_taskid', labels: { xv_title: { 1033: 'Title' }, xv_priority: { 1033: 'Priority' } } } },
  records: () => ({
    value: [{ xv_taskid: 't1', xv_title: 'Write docs', xv_priority: 2, 'xv_priority@OData.Community.Display.V1.FormattedValue': 'High' }],
  }),
};
const CELL: Cell = {
  id: 'cell-tasks',
  labels: { 1033: 'Tasks' },
  showLabel: true,
  visible: true,
  colSpan: 1,
  rowSpan: 1,
  control: { kind: 'subgrid', id: 'tasks', relationship: 'xv_project_task', targetEntity: 'xv_task', viewId: VIEW_ID, recordsPerPage: 4, enableViewPicker: false },
};
const FORM = formOf([tab('main', [{ width: '100%', sections: [section([[field('xv_name')], [CELL]])] }])]);

function open(modern: boolean) {
  mockApps(APPS);
  server.use(
    onPath('get', `xv_projects(${RECORD_ID})`, () => HttpResponse.json(RECORD)),
    http.get('*/api/data/v9.2/RelationshipDefinitions', () => HttpResponse.json({ value: [rawOneToMany('xv_project_task', 'xv_task', 'xv_projectid', 'xv_project')] })),
  );
  render(
    <Providers>
      <PresentationProvider modern={modern}>
        <Page form={FORM} ctx={contextOf([name], RECORD)} lcid={1033} onSelectForm={() => {}} />
      </PresentationProvider>
    </Providers>,
  );
}

beforeEach(() => {
  vi.mocked(loadColumnKinds).mockReset();
  vi.mocked(loadColumnKinds).mockResolvedValue({
    xv_priority: { kind: 'choice', type: 'Picklist', options: [{ value: 2, label: 'High', color: GREEN }] },
  });
});

describe('SubgridControl choice badges', () => {
  it('the modern look shows a coloured choice as a badge', async () => {
    open(true);
    const grid = await screen.findByRole('grid', { name: 'Active Tasks' });
    const high = await within(grid).findByText('High', { selector: '.fui-Badge' });
    expect(high).toHaveStyle({ backgroundColor: GREEN });
    expect(loadColumnKinds).toHaveBeenCalledWith(expect.anything(), 'xv_task', ['xv_title', 'xv_priority'], 1033);
  });

  it('the classic look shows the plain text and reads no column kinds', async () => {
    open(false);
    const grid = await screen.findByRole('grid', { name: 'Active Tasks' });
    expect(await within(grid).findByText('High')).not.toHaveClass('fui-Badge');
    expect(grid.querySelector('.fui-Badge')).toBeNull();
    expect(loadColumnKinds).not.toHaveBeenCalled();
  });
});
