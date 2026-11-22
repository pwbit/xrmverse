import { useEffect, useMemo, type ReactNode } from 'react';
import type { FormModel, Tab } from '../../model/form';
import type { FormPresentation } from '../../parsers/presentationJson';
import { t } from '../../shell/i18n/t';
import type { ControlEnv, SubgridModes } from '../classic/form/controlEnv';
import { FormTab } from '../classic/form/FormTab';
import { saveAvailability } from '../classic/form/saveAvailability';
import { FormView, type FormViewProps, type TabBodyArgs } from '../classic/form/FormView';
import { formBackgrounds } from './background';
import { GridTab } from './GridTab';
import { useFormPresentation } from './PresentationContext';
import { WizardBody, wizardStep } from './WizardBody';

/**
 * One tab's body in the modern look: the tab's sections on the free grid when the form's overlay places them (ruling C3), else
 * the classic tab. Every layout (the tab list, the wizard's steps) draws its tabs through it.
 */
export function renderTab(tab: Tab, env: ControlEnv, presentation: FormPresentation | undefined): ReactNode {
  const placements = presentation && Object.hasOwn(presentation.grid, tab.name) ? presentation.grid[tab.name] : undefined;
  return placements ? <GridTab tab={tab} env={env} placements={placements} /> : <FormTab tab={tab} env={env} />;
}

/**
 * The body of the form's own tabs for its overlay, or undefined for the classic tab list: the wizard (ruling C2) for a form
 * in create mode whose overlay asks for it; else, when the overlay places sections on a grid, the tab list drawing its tabs
 * through `renderTab`.
 */
export function tabBody(args: TabBodyArgs, presentation: FormPresentation, props: FormViewProps): ReactNode | undefined {
  // An existing record shows the normal tabs (ruling C2: the wizard is for create mode only).
  if (presentation.layout !== 'wizard' || props.ctx.record) {
    return Object.keys(presentation.grid).length > 0 ? args.tabList((tab) => renderTab(tab, args.env, presentation)) : undefined;
  }
  const { tabs, env, selectTab } = args;
  // Without the page's save the wizard draws no Save (the command bar still saves); else the command bar's rules apply.
  const availability = saveAvailability(props.ctx, props.state);
  const save = props.onSave && availability.shown ? { run: props.onSave, disabled: availability.disabled } : undefined;
  return (
    <WizardBody
      tabs={tabs}
      current={wizardStep(tabs, env.overlay?.selectedTab(), props.form.tabs)}
      env={env}
      selectTab={selectTab}
      renderTab={(tab) => renderTab(tab, env, presentation)}
      save={save}
      saveLabel={t('record.cmd.save')}
      lcid={props.lcid}
    />
  );
}

/** The control ids of the form's subgrids, lowercase and trimmed (the keys of the overlay's `subgrids`). */
function subgridIds(form: FormModel): Set<string> {
  const ids = new Set<string>();
  for (const tab of form.tabs)
    for (const column of tab.columns)
      for (const section of column.sections)
        for (const row of section.rows) for (const cell of row) if (cell.control?.kind === 'subgrid') ids.add(cell.control.id.trim().toLowerCase());
  return ids;
}

/** The modern form slot (UI-2c): the classic form, with the tab body its overlay asks for; without one, the classic form as is. */
export function ModernFormView(props: FormViewProps) {
  const presentation = useFormPresentation(props.form.id);
  const { tabs } = props.form;
  const backgrounds = useMemo(() => formBackgrounds(presentation?.background), [presentation]);
  const formId = props.form.id;
  const subgrids = presentation?.subgrids;
  const subgridModes = useMemo((): SubgridModes | undefined => (subgrids ? { formId, entries: subgrids } : undefined), [formId, subgrids]);
  useEffect(() => {
    if (!subgrids) return;
    const ids = subgridIds(props.form);
    for (const key of Object.keys(subgrids)) {
      if (!ids.has(key)) console.warn('[xrmverse presentation]', `The subgrids entry "${key}" names no subgrid of the form; it is ignored.`);
    }
  }, [subgrids, props.form]);
  useEffect(() => {
    if (!presentation) return;
    const names = new Set(tabs.map((tab) => tab.name));
    for (const name of Object.keys(presentation.grid)) {
      if (!names.has(name)) console.warn('[xrmverse presentation]', `The grid names a tab "${name}" that the form does not have; it is ignored.`);
    }
  }, [presentation, tabs]);
  return <FormView {...props} timelineAppearance={presentation?.timeline} backgrounds={backgrounds} subgridModes={subgridModes} renderTabBody={presentation ? (args) => tabBody(args, presentation, props) : undefined} />;
}
