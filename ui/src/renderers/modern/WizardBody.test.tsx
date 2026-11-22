import { act, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { HttpResponse } from 'msw';
import { describe, expect, it } from 'vitest';
import { onPath } from '../../admin/security/testSupport';
import { attr } from '../../admin/tables/testAttrs';
import type { RecordContext } from '../../apps/record/loadRecord';
import { useRecordForm } from '../../apps/record/useRecordForm';
import { mockApps } from '../../apps/testSupport';
import { createUiOverlay, type UiOverlay } from '../../client-api/uiOverlay';
import type { FormModel } from '../../model/form';
import type { PresentationModel } from '../../parsers/presentationJson';
import { server } from '../../test/server';
import { contextOf, field, formOf, labelsOf, Providers, section, tab } from '../classic/form/formTestSupport';
import { ModernFormView } from './ModernFormView';
import { PresentationProvider } from './PresentationContext';

const name = attr('xv_name', { isPrimaryName: true, displayName: 'Name', requiredLevel: 'ApplicationRequired' });
const note = attr('xv_note', { displayName: 'Note' });
const code = attr('xv_code', { displayName: 'Code' });
const RECORD = { xv_projectid: 'cccccccc-0000-0000-0000-000000000003', xv_name: 'Alpha', xv_note: 'n1', xv_code: 'c1' };
const FORM_ID = 'AAAAAAAA-0000-0000-0000-0000000000F1';

const wizardForm = (over: Parameters<typeof formOf>[1] = {}): FormModel =>
  formOf(
    [
      tab('general', [{ width: '100%', sections: [section([[field('xv_name')]])] }], { labels: labelsOf('General') }),
      tab('details', [{ width: '100%', sections: [section([[field('xv_note')]])] }], { labels: labelsOf('Details') }),
      tab('extra', [{ width: '100%', sections: [section([[field('xv_code')]])] }], { labels: labelsOf('Extra') }),
    ],
    { id: `{${FORM_ID}}`, ...over },
  );

const wizard = (id = FORM_ID): PresentationModel => ({
  theme: { density: 'comfortable' },
  whiteLabel: {},
  views: {},
  forms: { [id.toLowerCase()]: { layout: 'wizard', grid: {} } },
  choices: {},
});

function Page({ form, ctx, overlay, saves, withSave, prevented }: { form: FormModel; ctx: RecordContext; overlay: UiOverlay; saves: string[]; withSave: boolean; prevented: () => boolean }) {
  const state = useRecordForm(ctx, form, { lcid: 1033, overlay });
  return (
    <ModernFormView
      form={form}
      ctx={ctx}
      lcid={1033}
      state={state}
      overlay={overlay}
      onSelectForm={() => {}}
      onSave={
        withSave
          ? () => {
              // An OnSave handler that calls preventDefault: the page never reaches the form's save.
              if (prevented()) {
                saves.push('prevented');
                return;
              }
              saves.push('save');
              void state.save();
            }
          : undefined
      }
    />
  );
}

interface WizardOptions {
  form?: FormModel;
  record?: Record<string, unknown>;
  presentation?: PresentationModel;
  /** Overrides of the record context (the create privilege). */
  ctx?: Partial<RecordContext>;
  /** The page passes no `onSave` (default true: it does). */
  withSave?: boolean;
  /** The create POST waits for this promise before it answers. */
  hold?: Promise<void>;
  /** While it returns true, the page's save is stopped as by an OnSave preventDefault. */
  prevented?: () => boolean;
}

function renderWizard(opts: WizardOptions = {}) {
  mockApps();
  const posted: unknown[] = [];
  server.use(
    onPath('post', 'xv_projects', async ({ request }) => {
      posted.push(await request.json());
      await opts.hold;
      return new HttpResponse(null, { status: 204, headers: { 'OData-EntityId': `http://x/api/data/v9.2/xv_projects(${RECORD.xv_projectid})` } });
    }),
  );
  const form = opts.form ?? wizardForm();
  const overlay = createUiOverlay();
  const saves: string[] = [];
  const view = render(
    <Providers>
      <PresentationProvider model={opts.presentation ?? wizard()}>
        <Page form={form} ctx={{ ...contextOf([name, note, code], opts.record), ...opts.ctx }} overlay={overlay} saves={saves} withSave={opts.withSave ?? true} prevented={opts.prevented ?? (() => false)} />
      </PresentationProvider>
    </Providers>,
  );
  return { ...view, form, overlay, posted, saves };
}

/** The names of tabs (Fluent draws a tab's content twice, once to reserve the width of its selected style). */
const names = (tabs: HTMLElement[]) => tabs.map((tab) => tab.querySelector('.fui-Tab__content')?.textContent);

const next = () => userEvent.click(screen.getByRole('button', { name: 'Next' }));
const back = () => userEvent.click(screen.getByRole('button', { name: 'Back' }));

describe('Wizard layout (UI-2c)', () => {
  it('in create mode shows the first step with its indicator, Next and no free navigation forward', async () => {
    renderWizard();
    expect(await screen.findByText('Step 1 of 3: General')).toBeInTheDocument();
    expect(screen.getByLabelText(/xv_name label/)).toBeInTheDocument();
    expect(screen.queryByLabelText(/xv_note label/)).toBeNull();
    expect(screen.getByRole('button', { name: 'Next' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Back' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Save' })).toBeNull();
    const steps = screen.getByRole('tablist', { name: 'Steps' });
    const tabs = within(steps).getAllByRole('tab');
    expect(names(tabs)).toEqual(['General', 'Details', 'Extra']);
    // Later steps cannot be reached from the step list.
    expect(tabs[1]).toBeDisabled();
    expect(tabs[2]).toBeDisabled();
    await userEvent.click(tabs[2]!);
    expect(screen.getByText('Step 1 of 3: General')).toBeInTheDocument();
  });

  it('Next with an empty required field stays and shows the field error; filled, it moves to step 2', async () => {
    const { posted } = renderWizard();
    await screen.findByText('Step 1 of 3: General');
    await next();
    expect(screen.getByText('Step 1 of 3: General')).toBeInTheDocument();
    expect(screen.getByText('xv_name label: a value is required.')).toBeInTheDocument();
    await userEvent.type(screen.getByLabelText(/xv_name label/), 'Alpha');
    await next();
    expect(screen.getByText('Step 2 of 3: Details')).toBeInTheDocument();
    expect(screen.getByLabelText(/xv_note label/)).toBeInTheDocument();
    expect(posted).toHaveLength(0);
  });

  it('Back returns to the previous step without a check, and the step list goes back too', async () => {
    const { overlay } = renderWizard();
    await screen.findByText('Step 1 of 3: General');
    await userEvent.type(screen.getByLabelText(/xv_name label/), 'Alpha');
    await next();
    // A field made required by a script on step 2, left empty: Back does not check it.
    act(() => overlay.setAttribute('xv_note', { requiredLevel: 'required' }));
    await back();
    expect(screen.getByText('Step 1 of 3: General')).toBeInTheDocument();
    expect(screen.queryByText(/xv_note label: a value is required/)).toBeNull();
    await next();
    await next();
    expect(screen.getByText('Step 2 of 3: Details')).toBeInTheDocument();
    expect(screen.getByText('xv_note label: a value is required.')).toBeInTheDocument();
    await userEvent.type(screen.getByLabelText(/xv_note label/), 'n');
    await next();
    expect(screen.getByText('Step 3 of 3: Extra')).toBeInTheDocument();
    await userEvent.click(within(screen.getByRole('tablist', { name: 'Steps' })).getByRole('tab', { name: 'General' }));
    expect(screen.getByText('Step 1 of 3: General')).toBeInTheDocument();
  });

  it('a required field hidden by a script does not block Next', async () => {
    const { form, overlay } = renderWizard();
    await screen.findByText('Step 1 of 3: General');
    const cell = form.tabs[0]!.columns[0]!.sections[0]!.rows[0]![0]!;
    act(() => overlay.setElement(cell, { visible: false }));
    await next();
    expect(screen.getByText('Step 2 of 3: Details')).toBeInTheDocument();
  });

  it('a required section hidden by a script does not block Next', async () => {
    const { form, overlay } = renderWizard();
    await screen.findByText('Step 1 of 3: General');
    act(() => overlay.setElement(form.tabs[0]!.columns[0]!.sections[0]!, { visible: false }));
    await next();
    expect(screen.getByText('Step 2 of 3: Details')).toBeInTheDocument();
  });

  it('a script hiding the current step moves to the next visible step, else the previous one', async () => {
    const { form, overlay } = renderWizard();
    await screen.findByText('Step 1 of 3: General');
    await userEvent.type(screen.getByLabelText(/xv_name label/), 'Alpha');
    await next();
    act(() => overlay.setElement(form.tabs[1]!, { visible: false }));
    expect(screen.getByText('Step 2 of 2: Extra')).toBeInTheDocument();
    expect(screen.getByLabelText('xv_code label')).toBeInTheDocument();
    // Shown again, the tab is a step again, but the user stays where they were moved.
    act(() => overlay.setElement(form.tabs[1]!, { visible: true }));
    expect(screen.getByText('Step 3 of 3: Extra')).toBeInTheDocument();
    act(() => overlay.setElement(form.tabs[2]!, { visible: false }));
    expect(screen.getByText('Step 2 of 2: Details')).toBeInTheDocument();
  });

  it("a script's tab.setFocus() moves to that step", async () => {
    const { form, overlay } = renderWizard();
    await screen.findByText('Step 1 of 3: General');
    act(() => overlay.selectTab(form.tabs[2]!));
    expect(screen.getByText('Step 3 of 3: Extra')).toBeInTheDocument();
    expect(screen.getByLabelText('xv_code label')).toBeInTheDocument();
  });

  it("the last step shows Save; with an earlier step's required field empty the save shows the required error and sends nothing", async () => {
    const { overlay, posted, saves } = renderWizard();
    await screen.findByText('Step 1 of 3: General');
    await userEvent.type(screen.getByLabelText(/xv_name label/), 'Alpha');
    await next();
    await next();
    expect(screen.getByText('Step 3 of 3: Extra')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Next' })).toBeNull();
    // A script makes a field of step 2 required after the user passed it.
    act(() => overlay.setAttribute('xv_note', { requiredLevel: 'required' }));
    await userEvent.click(screen.getByRole('button', { name: 'Save' }));
    expect(saves).toEqual(['save']);
    expect(posted).toHaveLength(0);
    // The wizard moves to the first step with a field in error, which shows the error at the field.
    expect(await screen.findByText('Step 2 of 3: Details')).toBeInTheDocument();
    expect(screen.getByLabelText(/xv_note label/)).toBeInTheDocument();
    expect(screen.getAllByText(/xv_note label: a value is required/).length).toBeGreaterThan(0);
  });

  it('a failed save selects the first step with a field in error, not a later one', async () => {
    const { form, overlay, posted } = renderWizard();
    await screen.findByText('Step 1 of 3: General');
    // A script jumps to the last step: step 1's required name is still empty, and step 2's note becomes required.
    act(() => overlay.selectTab(form.tabs[2]!));
    act(() => overlay.setAttribute('xv_note', { requiredLevel: 'required' }));
    await userEvent.type(screen.getByLabelText(/xv_code label/), 'c');
    await userEvent.click(screen.getByRole('button', { name: 'Save' }));
    expect(posted).toHaveLength(0);
    expect(await screen.findByText('Step 1 of 3: General')).toBeInTheDocument();
    expect(screen.getByLabelText(/xv_name label/)).toBeInTheDocument();
  });

  it('a save stopped before the required check does not move the user later', async () => {
    let stop = false;
    const { overlay, saves } = renderWizard({ prevented: () => stop });
    await screen.findByText('Step 1 of 3: General');
    await userEvent.type(screen.getByLabelText(/xv_name label/), 'Alpha');
    await next();
    await next();
    // Save #1 fails on a required field of step 3: the user stays on step 3.
    act(() => overlay.setAttribute('xv_code', { requiredLevel: 'required' }));
    await userEvent.click(screen.getByRole('button', { name: 'Save' }));
    expect(await screen.findAllByText('xv_code label: a value is required.')).not.toHaveLength(0);
    // Save #2 is stopped by OnSave: the errors do not change.
    stop = true;
    await userEvent.click(screen.getByRole('button', { name: 'Save' }));
    expect(saves).toEqual(['save', 'prevented']);
    // Back to step 1 and Next: the wizard goes to step 2, not back to step 3.
    await userEvent.click(screen.getByRole('button', { name: 'Back' }));
    await userEvent.click(screen.getByRole('button', { name: 'Back' }));
    expect(screen.getByText('Step 1 of 3: General')).toBeInTheDocument();
    await next();
    expect(screen.getByText('Step 2 of 3: Details')).toBeInTheDocument();
  });

  it('a failed save stays on the last step when only its own fields are in error', async () => {
    const { overlay, posted } = renderWizard();
    await screen.findByText('Step 1 of 3: General');
    await userEvent.type(screen.getByLabelText(/xv_name label/), 'Alpha');
    await next();
    await next();
    act(() => overlay.setAttribute('xv_code', { requiredLevel: 'required' }));
    await userEvent.click(screen.getByRole('button', { name: 'Save' }));
    expect(posted).toHaveLength(0);
    expect(await screen.findAllByText('xv_code label: a value is required.')).not.toHaveLength(0);
    expect(screen.getByText('Step 3 of 3: Extra')).toBeInTheDocument();
  });

  it('Save is disabled while nothing changed', async () => {
    const { form, overlay } = renderWizard();
    await screen.findByText('Step 1 of 3: General');
    act(() => overlay.selectTab(form.tabs[2]!));
    expect(screen.getByText('Step 3 of 3: Extra')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Save' })).toBeDisabled();
  });

  it('Save is disabled while the save runs', async () => {
    let release!: () => void;
    const hold = new Promise<void>((resolve) => (release = resolve));
    const { posted } = renderWizard({ hold });
    await screen.findByText('Step 1 of 3: General');
    await userEvent.type(screen.getByLabelText(/xv_name label/), 'Alpha');
    await next();
    await next();
    const save = screen.getByRole('button', { name: 'Save' });
    expect(save).toBeEnabled();
    await userEvent.click(save);
    await waitFor(() => expect(posted).toHaveLength(1));
    // The values are still unsaved (dirty): only the running request disables Save.
    expect(screen.getByRole('button', { name: 'Save' })).toBeDisabled();
    release();
  });

  it('Save is hidden without the create privilege', async () => {
    const { form, overlay } = renderWizard({ ctx: { canCreate: false } });
    await screen.findByText('Step 1 of 3: General');
    act(() => overlay.selectTab(form.tabs[2]!));
    expect(screen.getByText('Step 3 of 3: Extra')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Save' })).toBeNull();
  });

  it('draws no Save without the page\'s onSave (the command bar still saves)', async () => {
    const { form, overlay } = renderWizard({ withSave: false });
    await screen.findByText('Step 1 of 3: General');
    await userEvent.type(screen.getByLabelText(/xv_name label/), 'Alpha');
    act(() => overlay.selectTab(form.tabs[2]!));
    expect(screen.getByText('Step 3 of 3: Extra')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Save' })).toBeNull();
  });

  it('the step survives opening the Related tab and coming back', async () => {
    const navigation = [{ kind: 'unsupported' as const, id: 'navLink', labels: { 1033: 'Help page' } }];
    renderWizard({ form: wizardForm({ navigation }) });
    await screen.findByText('Step 1 of 3: General');
    await userEvent.type(screen.getByLabelText(/xv_name label/), 'Alpha');
    await next();
    expect(screen.getByText('Step 2 of 3: Details')).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Related' }));
    expect(screen.queryByText(/Step \d of/)).toBeNull();
    await userEvent.click(screen.getByRole('button', { name: 'Back to the form' }));
    expect(screen.getByText('Step 2 of 3: Details')).toBeInTheDocument();
  });

  it('an existing record shows the normal tabs', async () => {
    renderWizard({ record: RECORD });
    expect(await screen.findByLabelText(/xv_name label/)).toHaveValue('Alpha');
    expect(screen.queryByText(/Step \d of/)).toBeNull();
    expect(screen.queryByRole('button', { name: 'Next' })).toBeNull();
    expect(names(screen.getAllByRole('tab'))).toEqual(['General', 'Details', 'Extra']);
  });

  it('only the form the overlay names gets the wizard', async () => {
    renderWizard({ presentation: wizard('bbbbbbbb-0000-0000-0000-0000000000f2') });
    expect(await screen.findByLabelText(/xv_name label/)).toBeInTheDocument();
    expect(screen.queryByText(/Step \d of/)).toBeNull();
    expect(names(screen.getAllByRole('tab'))).toEqual(['General', 'Details', 'Extra']);
  });
});
