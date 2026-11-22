import { act, render, screen, waitFor, within } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { attr } from '../../admin/tables/testAttrs';
import type { RecordContext } from '../../apps/record/loadRecord';
import { useRecordForm } from '../../apps/record/useRecordForm';
import { mockApps } from '../../apps/testSupport';
import { createUiOverlay, type UiOverlay } from '../../client-api/uiOverlay';
import type { FormModel } from '../../model/form';
import type { FormPresentation, GridPlacement, PresentationModel } from '../../parsers/presentationJson';
import { contextOf, field, formOf, labelsOf, Providers, section, tab } from '../classic/form/formTestSupport';
import { ModernFormView } from './ModernFormView';
import { PresentationProvider } from './PresentationContext';

const name = attr('xv_name', { isPrimaryName: true, displayName: 'Name' });
const note = attr('xv_note', { displayName: 'Note' });
const code = attr('xv_code', { displayName: 'Code' });
const RECORD = { xv_projectid: 'cccccccc-0000-0000-0000-000000000003', xv_name: 'Alpha', xv_note: 'n1', xv_code: 'c1' };
const FORM_ID = 'AAAAAAAA-0000-0000-0000-0000000000F1';

/** A tab whose FormXml stacks three sections in one column; a second tab stays classic. */
const gridForm = (): FormModel =>
  formOf(
    [
      tab(
        'general',
        [
          {
            width: '100%',
            sections: [
              section([[field('xv_name')]], { name: 'left', labels: labelsOf('Left') }),
              section([[field('xv_note')]], { name: 'right', labels: labelsOf('Right') }),
              section([[field('xv_code')]], { name: 'rest', labels: labelsOf('Rest') }),
            ],
          },
        ],
        { labels: labelsOf('General') },
      ),
      tab('other', [{ width: '100%', sections: [section([], { name: 'o', labels: labelsOf('Other') })] }], { labels: labelsOf('Other') }),
    ],
    { id: `{${FORM_ID}}` },
  );

const at = (col: number, span: number, row: number, rowSpan = 1): GridPlacement => ({ col, span, row, rowSpan });

const model = (form: Partial<FormPresentation>): PresentationModel => ({
  theme: { density: 'comfortable' },
  whiteLabel: {},
  views: {},
  forms: { [FORM_ID.toLowerCase()]: { layout: 'tabs', grid: {}, ...form } },
  choices: {},
});

function Page({ form, ctx, overlay }: { form: FormModel; ctx: RecordContext; overlay: UiOverlay }) {
  const state = useRecordForm(ctx, form, { lcid: 1033, overlay });
  return <ModernFormView form={form} ctx={ctx} lcid={1033} state={state} overlay={overlay} onSelectForm={() => {}} />;
}

function renderGrid(presentation: PresentationModel, opts: { record?: Record<string, unknown> | null } = {}) {
  mockApps();
  const form = gridForm();
  const overlay = createUiOverlay();
  const record = opts.record === null ? undefined : (opts.record ?? RECORD);
  const view = render(
    <Providers>
      <PresentationProvider model={presentation}>
        <Page form={form} ctx={contextOf([name, note, code], record)} overlay={overlay} />
      </PresentationProvider>
    </Providers>,
  );
  return { ...view, form, overlay };
}

const cells = () => screen.getAllByTestId('grid-section');
const labelOf = (el: HTMLElement) => el.querySelector('h3')?.textContent;
const SIDE_BY_SIDE = { general: { left: at(1, 6, 1), right: at(7, 6, 1) } };

function stubNarrow(narrow: boolean) {
  vi.stubGlobal('matchMedia', (query: string) => ({ matches: narrow && query === '(max-width: 640px)', media: query, addEventListener: () => {}, removeEventListener: () => {} }));
}

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe('Free grid (UI-2c, ruling C3)', () => {
  it('places two sections side by side on the same row; an unplaced section follows full width', async () => {
    renderGrid(model({ grid: SIDE_BY_SIDE }));
    await screen.findByLabelText('xv_name label');
    const [left, right, rest] = cells();
    expect(cells().map(labelOf)).toEqual(['Left', 'Right', 'Rest']);
    expect(left!.style.gridColumn).toBe('1 / span 6');
    expect(left!.style.gridRow).toBe('1 / span 1');
    expect(right!.style.gridColumn).toBe('7 / span 6');
    expect(right!.style.gridRow).toBe('1 / span 1');
    expect(rest!.style.gridColumn).toBe('1 / span 12');
    expect(rest!.style.gridRow).toBe('2 / span 1');
    expect(screen.getByTestId('grid-tab')).toBeInTheDocument();
    // The FormXml column split of the tab is ignored.
    expect(screen.queryByTestId('tab-column')).toBeNull();
  });

  it('warns once about an unknown section name', async () => {
    const warn = vi.spyOn(console, 'warn').mockImplementation(() => {});
    const { overlay, form } = renderGrid(model({ grid: { general: { ...SIDE_BY_SIDE.general, ghost: at(1, 3, 1) } } }));
    await screen.findByLabelText('xv_name label');
    // A re-render (a script change) does not repeat it.
    act(() => overlay.setElement(form.tabs[0]!.columns[0]!.sections[2]!, { visible: false }));
    const ours = warn.mock.calls.filter((c) => c[0] === '[xrmverse presentation]' && String(c[1]).includes('ghost'));
    expect(ours).toHaveLength(1);
  });

  it('a section hidden by a script is not drawn and leaves no gap row', async () => {
    const { overlay, form } = renderGrid(model({ grid: { general: { left: at(1, 12, 1), right: at(1, 12, 2), rest: at(1, 12, 3) } } }));
    await screen.findByLabelText('xv_name label');
    // The overlay notifies the grid through a subscription; on a busy CI runner the re-render lands after act returns.
    act(() => overlay.setElement(form.tabs[0]!.columns[0]!.sections[1]!, { visible: false }));
    await waitFor(() => expect(cells().map(labelOf)).toEqual(['Left', 'Rest']));
    expect(cells()[1]!.style.gridRow).toBe('2 / span 1');
    expect(screen.queryByLabelText('xv_note label')).toBeNull();
    act(() => overlay.setElement(form.tabs[0]!.columns[0]!.sections[1]!, { visible: true }));
    await waitFor(() => expect(cells().map(labelOf)).toEqual(['Left', 'Right', 'Rest']));
  });

  it('on a narrow screen is one column, ordered by row then column', async () => {
    stubNarrow(true);
    renderGrid(model({ grid: { general: { left: at(7, 6, 2), right: at(1, 6, 2), rest: at(1, 12, 1) } } }));
    await screen.findByLabelText('xv_name label');
    expect(cells().map(labelOf)).toEqual(['Rest', 'Right', 'Left']);
    for (const c of cells()) {
      expect(c.style.gridColumn).toBe('');
      expect(c.style.gridRow).toBe('');
    }
  });

  it('a tab not named in grid keeps the classic layout', async () => {
    renderGrid(model({ grid: SIDE_BY_SIDE }));
    await screen.findByLabelText('xv_name label');
    const other = screen.getByRole('tab', { name: 'Other' });
    await act(async () => other.click());
    expect(screen.queryByTestId('grid-tab')).toBeNull();
    expect(screen.getAllByTestId('tab-column')).toHaveLength(1);
  });

  it('warns about a tab name in grid that the form does not have', async () => {
    const warn = vi.spyOn(console, 'warn').mockImplementation(() => {});
    renderGrid(model({ grid: { ...SIDE_BY_SIDE, nowhere: { a: at(1, 6, 1) } } }));
    await screen.findByLabelText('xv_name label');
    const ours = warn.mock.calls.filter((c) => c[0] === '[xrmverse presentation]' && String(c[1]).includes('nowhere'));
    expect(ours).toHaveLength(1);
    expect(cells()).toHaveLength(3);
  });

  it('a grid tab inside the wizard is drawn on the grid', async () => {
    renderGrid(model({ layout: 'wizard', grid: SIDE_BY_SIDE }), { record: null });
    expect(await screen.findByText('Step 1 of 2: General')).toBeInTheDocument();
    expect(cells().map(labelOf)).toEqual(['Left', 'Right', 'Rest']);
    expect(cells()[1]!.style.gridColumn).toBe('7 / span 6');
    expect(within(screen.getByTestId('grid-tab')).getByLabelText(/xv_name label/)).toBeInTheDocument();
  });
});
