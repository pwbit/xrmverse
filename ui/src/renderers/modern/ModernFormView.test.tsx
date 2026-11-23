import { render, screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { describe, expect, it, vi } from 'vitest';
import { rawOneToMany } from '../../admin/tables/tableTestSupport';
import { attr } from '../../admin/tables/testAttrs';
import type { RecordContext } from '../../apps/record/loadRecord';
import { useRecordForm } from '../../apps/record/useRecordForm';
import { server } from '../../test/server';
import { mockApps } from '../../apps/testSupport';
import type { Cell, FormModel } from '../../model/form';
import type { PresentationModel } from '../../parsers/presentationJson';
import { FormView } from '../classic/form/FormView';
import { contextOf, field, formOf, labelsOf, Providers, section, tab } from '../classic/form/formTestSupport';
import { classicRenderer } from '../classic';
import { modernRenderer } from '.';
import { ModernFormView } from './ModernFormView';
import { PresentationProvider } from './PresentationContext';
import { modeNames, queryShowAs, showAs } from '../../apps/views/modeMenuTestSupport';

const name = attr('xv_name', { isPrimaryName: true, displayName: 'Name' });
const note = attr('xv_note', { displayName: 'Note' });
const RECORD = { xv_projectid: 'cccccccc-0000-0000-0000-000000000003', xv_name: 'Alpha', xv_note: 'n1' };
const FORM_ID = 'AAAAAAAA-0000-0000-0000-0000000000F1';

const form = formOf(
  [
    tab('summary', [{ width: '100%', sections: [section([[field('xv_name')]])] }], { labels: labelsOf('Summary') }),
    tab('details', [{ width: '100%', sections: [section([[field('xv_note')]])] }], { labels: labelsOf('Details') }),
  ],
  { id: `{${FORM_ID}}` },
);

const model = (forms: PresentationModel['forms']): PresentationModel => ({ theme: { density: 'comfortable' }, whiteLabel: {}, views: {}, forms, choices: {} });

function Page({ View, form: f, ctx }: { View: typeof FormView; form: FormModel; ctx: RecordContext }) {
  const state = useRecordForm(ctx, f, { lcid: 1033 });
  return <View form={f} ctx={ctx} lcid={1033} state={state} onSelectForm={() => {}} />;
}

/** The markup of a render, with React's generated ids made stable so that two renders compare. */
async function markup(View: typeof FormView, presentation?: PresentationModel): Promise<string> {
  mockApps();
  const view = render(
    <Providers>
      <PresentationProvider model={presentation}>
        <Page View={View} form={form} ctx={contextOf([name, note], RECORD)} />
      </PresentationProvider>
    </Providers>,
  );
  await screen.findByLabelText('xv_name label');
  const html = view.container.innerHTML.replace(/:r[0-9a-z]+:/g, ':id:').replace(/-r[0-9a-z]+__/g, '-id__');
  view.unmount();
  return html;
}

describe('ModernFormView', () => {
  it('is the modern FormView slot', () => {
    expect(modernRenderer.FormView).toBe(ModernFormView);
    expect(classicRenderer.FormView).toBe(FormView);
  });

  it('without an overlay renders exactly the classic form', async () => {
    const classic = await markup(FormView);
    expect(classic).toContain('role="tablist"');
    expect(await markup(ModernFormView)).toBe(classic);
  });

  it('with an overlay that does not name the form renders exactly the classic form', async () => {
    const classic = await markup(FormView);
    expect(await markup(ModernFormView, model({ 'bbbbbbbb-0000-0000-0000-0000000000f2': { layout: 'wizard', grid: {} } }))).toBe(classic);
  });

  it('a form named with the tabs layout and no grid keeps the classic tabs', async () => {
    const classic = await markup(FormView);
    expect(await markup(ModernFormView, model({ [FORM_ID.toLowerCase()]: { layout: 'tabs', grid: {} } }))).toBe(classic);
  });

  describe('timeline appearance', () => {
    const timelineForm = (): FormModel => {
      const cell: Cell = {
        ...field('x'),
        id: 't',
        control: { kind: 'timeline', id: 't', classId: '{06375649-C143-495E-A496-C962E5B4488E}', parameterNames: [] },
        labels: labelsOf('Timeline'),
      };
      return formOf([tab('summary', [{ width: '100%', sections: [section([[cell]])] }], { labels: labelsOf('Summary') })], { id: `{${FORM_ID}}` });
    };

    async function renderTimeline(View: typeof FormView, presentation: PresentationModel | undefined) {
      mockApps();
      server.use(
        http.get('*/api/data/v9.2/customcontrols', () => HttpResponse.json({ value: [] })),
        http.get(/RetrieveUserPrivileges/, () => HttpResponse.json({ RolePrivileges: [{ PrivilegeName: 'prvReadNote' }] })),
        http.get('*/api/data/v9.2/annotations', ({ request }) =>
          HttpResponse.json(
            new URL(request.url).searchParams.has('$count')
              ? { '@odata.count': 1, value: [] }
              : { value: [{ annotationid: 'n1', subject: 'Kickoff minutes', modifiedon: '2026-11-10T08:00:00Z' }] },
          ),
        ),
      );
      const ctx = contextOf([name, note], RECORD);
      ctx.entity.hasNotes = true;
      render(
        <Providers>
          <PresentationProvider model={presentation}>
            <Page View={View} form={timelineForm()} ctx={ctx} />
          </PresentationProvider>
        </Providers>,
      );
      const region = await screen.findByRole('region', { name: 'Timeline' });
      await within(region).findByText('Kickoff minutes');
      return region;
    }

    it('puts the overlay timeline into the timeline control', async () => {
      const region = await renderTimeline(ModernFormView, model({ [FORM_ID.toLowerCase()]: { layout: 'tabs', grid: {}, timeline: { appearance: 'cards', density: 'compact' } } }));
      const list = within(region).getByRole('list', { name: 'Timeline items' });
      expect(list).toHaveAttribute('data-appearance', 'cards');
      expect(list).toHaveAttribute('data-density', 'compact');
      expect(region.querySelectorAll('li [data-appearance="cards"]')).toHaveLength(1);
    });

    it('leaves the timeline classic when the overlay has no timeline key', async () => {
      const region = await renderTimeline(ModernFormView, model({ [FORM_ID.toLowerCase()]: { layout: 'tabs', grid: {} } }));
      const list = within(region).getByRole('list', { name: 'Timeline items' });
      expect(list).not.toHaveAttribute('data-appearance');
      expect(list).not.toHaveAttribute('data-density');
    });

    it('never reads the overlay with the classic renderer', async () => {
      const region = await renderTimeline(FormView, model({ [FORM_ID.toLowerCase()]: { layout: 'tabs', grid: {}, timeline: { appearance: 'cards', density: 'compact' } } }));
      expect(region.querySelector('[data-appearance]')).toBeNull();
    });
  });

  describe('background colours', () => {
    const coloured = formOf(
      [
        tab('summary', [{ width: '100%', sections: [section([[field('xv_name'), field('xv_note')]], { name: 'main', labels: labelsOf('Main') })] }], { labels: labelsOf('Summary') }),
      ],
      { id: `{${FORM_ID}}` },
    );
    async function renderColoured(View: typeof FormView, presentation?: PresentationModel) {
      mockApps();
      render(
        <Providers>
          <PresentationProvider model={presentation}>
            <Page View={View} form={coloured} ctx={contextOf([name, note], RECORD)} />
          </PresentationProvider>
        </Providers>,
      );
      await screen.findByLabelText('xv_name label');
      const main = screen.getByRole('group', { name: 'Main' });
      const cellOf = (label: string) => screen.getByLabelText(label).closest<HTMLElement>('[data-testid="form-cell"]')!;
      return { main, name: cellOf('xv_name label'), note: cellOf('xv_note label') };
    }
    const overlay = model({ [FORM_ID.toLowerCase()]: { layout: 'tabs', grid: {}, background: { sections: { main: 'yellow' }, fields: { xv_note: '#a1b2c3' } } } });

    it('colours the section, and a field with its own colour over it; the other field shows the section through', async () => {
      const { main, name: nameCell, note: noteCell } = await renderColoured(ModernFormView, overlay);
      expect(main.style.backgroundColor).toBe('var(--colorPaletteYellowBackground2)');
      // The colour fills the control box (through the custom property), never the cell itself.
      expect(noteCell.style.backgroundColor).toBe('');
      expect(noteCell.style.getPropertyValue('--xrmverse-field-fill')).toBe('#a1b2c3');
      expect(noteCell).toHaveAttribute('data-field-fill', 'true');
      expect(nameCell.style.backgroundColor).toBe('');
      expect(nameCell.style.getPropertyValue('--xrmverse-field-fill')).toBe('');
      expect(nameCell).not.toHaveAttribute('data-field-fill');
    });

    it('never colours with the classic renderer', async () => {
      const { main, note: noteCell } = await renderColoured(FormView, overlay);
      expect(main.style.backgroundColor).toBe('');
      expect(noteCell.style.backgroundColor).toBe('');
    });
  });

  describe('subgrid modes', () => {
    const VIEW_ID = 'a0000000-0000-0000-0000-0000000000a1';
    const subgrid: Cell = {
      id: 'cell-tasks',
      labels: labelsOf('Tasks'),
      showLabel: true,
      visible: true,
      colSpan: 1,
      rowSpan: 1,
      control: { kind: 'subgrid', id: 'Tasks', relationship: 'xv_project_task', targetEntity: 'xv_task', viewId: VIEW_ID, recordsPerPage: 4, enableViewPicker: false },
    };
    const withSubgrid = formOf([tab('summary', [{ width: '100%', sections: [section([[field('xv_name')], [subgrid]])] }], { labels: labelsOf('Summary') })], {
      id: `{${FORM_ID}}`,
    });

    function renderSubgrid(View: typeof FormView, presentation: PresentationModel) {
      mockApps({
        savedQueries: [
          {
            id: VIEW_ID,
            name: 'Active Tasks',
            entity: 'xv_task',
            isdefault: true,
            fetchxml: '<fetch version="1.0" mapping="logical"><entity name="xv_task"><attribute name="xv_title"/><attribute name="xv_taskid"/></entity></fetch>',
            layoutxml: '<grid name="resultset" jump="xv_title" select="1" icon="1" preview="1"><row name="result" id="xv_taskid"><cell name="xv_title" width="200"/></row></grid>',
          },
        ],
        tables: { xv_task: { entitySetName: 'xv_tasks', primaryId: 'xv_taskid', labels: { xv_title: { 1033: 'Title' } } } },
        records: () => ({ value: [{ xv_taskid: 't1', xv_title: 'Write docs' }] }),
      });
      server.use(
        http.get('*/api/data/v9.2/RelationshipDefinitions', () =>
          HttpResponse.json({ value: [rawOneToMany('xv_project_task', 'xv_task', 'xv_projectid', 'xv_project')] }),
        ),
      );
      render(
        <Providers>
          <PresentationProvider model={presentation}>
            <Page View={View} form={withSubgrid} ctx={contextOf([name, note], RECORD)} />
          </PresentationProvider>
        </Providers>,
      );
    }
    const cardsOverlay = model({ [FORM_ID.toLowerCase()]: { layout: 'tabs', grid: {}, subgrids: { tasks: { cards: {} } } } });
    const presentationWarnings = (warn: { mock: { calls: unknown[][] } }) =>
      warn.mock.calls.filter((c) => c[0] === '[xrmverse presentation]').map((c) => String(c[1]));

    it('gives the subgrid its entry on its own form', async () => {
      renderSubgrid(ModernFormView, cardsOverlay);
      expect(await modeNames()).toEqual(['Grid', 'Cards']);
    });

    it('never offers modes with the classic renderer', async () => {
      renderSubgrid(FormView, cardsOverlay);
      expect(await screen.findByText('Write docs')).toBeInTheDocument();
      expect(queryShowAs()).toBeNull();
    });

    it('warns once about an entry that names no subgrid of the form', async () => {
      const warn = vi.spyOn(console, 'warn').mockImplementation(() => {});
      renderSubgrid(ModernFormView, model({ [FORM_ID.toLowerCase()]: { layout: 'tabs', grid: {}, subgrids: { nosuch: { cards: {} }, tasks: { cards: {} } } } }));
      await showAs();
      await waitFor(() => expect(presentationWarnings(warn)).toHaveLength(1));
      expect(presentationWarnings(warn)[0]).toContain('"nosuch" names no subgrid');
      warn.mockRestore();
    });
  });
});

describe('ModernFormView party lists (plan 4.1b, task 9)', () => {
  it('draws a party list with the same chips as the classic form', async () => {
    mockApps();
    const to = attr('to', { kind: 'partylist', typeName: 'PartyList', displayName: 'To', targets: ['contact'] });
    const base = contextOf([name, to], {
      ...RECORD,
      xv_project_activity_parties: [{ participationtypemask: 2, _partyid_value: null, addressused: 'orders@example.com' }],
    });
    const ctx = { ...base, fieldContext: { ...base.fieldContext, partyNavigation: 'xv_project_activity_parties' } };
    const f = formOf([tab('summary', [{ width: '100%', sections: [section([[field('xv_name')], [field('to')]])] }])], { id: `{${FORM_ID}}` });
    render(
      <Providers>
        <PresentationProvider model={model({ [FORM_ID.toLowerCase()]: { layout: 'tabs', grid: { summary: {} } } })}>
          <Page View={ModernFormView} form={f} ctx={ctx} />
        </PresentationProvider>
      </Providers>,
    );
    const list = await screen.findByRole('list', { name: 'to label' });
    expect(within(list).getByRole('listitem').textContent).toContain('orders@example.com');
    expect(screen.getByRole('button', { name: 'Remove orders@example.com' })).toBeInTheDocument();
  });
});
