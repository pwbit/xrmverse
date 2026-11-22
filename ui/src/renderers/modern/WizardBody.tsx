import { Button, makeStyles, Tab, TabList, Text, tokens } from '@fluentui/react-components';
import { Fragment, useEffect, useRef, type ReactNode } from 'react';
import type { Tab as FormTab } from '../../model/form';
import { labelFor } from '../../parsers/formXml';
import { t } from '../../shell/i18n/t';
import type { ControlEnv } from '../classic/form/controlEnv';
import type { IndexedTab } from '../classic/form/FormView';
import { stepAttributes } from '../classic/form/stepFields';

const useStyles = makeStyles({
  steps: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalS, paddingBlockStart: tokens.spacingVerticalS },
  indicator: { color: tokens.colorNeutralForeground2 },
  panel: { paddingBlock: tokens.spacingVerticalM },
  actions: { display: 'flex', gap: tokens.spacingHorizontalS, justifyContent: 'flex-end', paddingBlockEnd: tokens.spacingVerticalM },
});

/**
 * The wizard's step: the tab a user or a script selected while it is shown; when a script hid it, the nearest shown tab after
 * it in FormXml order, else the nearest before it (Review Focus 2); with no selection, the first shown tab. `formTabs` are all
 * of the form's tabs, for the FormXml index of a hidden selection.
 */
export function wizardStep(tabs: IndexedTab[], selected: object | undefined, formTabs: FormTab[]): IndexedTab | undefined {
  const shown = tabs.find(({ tab }) => tab === selected);
  if (shown) return shown;
  const at = selected ? formTabs.indexOf(selected as FormTab) : -1;
  if (at < 0) return tabs[0];
  return tabs.find(({ index }) => index > at) ?? [...tabs].reverse().find(({ index }) => index < at);
}

export interface WizardBodyProps {
  /** The steps: the shown tabs, in FormXml order. */
  tabs: IndexedTab[];
  /** The current step (see {@link wizardStep}). */
  current?: IndexedTab;
  env: ControlEnv;
  /** Selects a tab in the form's overlay: the step lives there, shared with the scripts' `tab.setFocus()`. */
  selectTab(tab: FormTab): void;
  /** Draws one step's tab. */
  renderTab(tab: FormTab): ReactNode;
  /**
   * The last step's Save: the form's own save (OnSave, the required check) and whether the button is disabled (the command
   * bar's rules, `saveAvailability`). Absent, the wizard draws no Save (the page gave no save, or the user may not save).
   */
  save?: { run(): void; disabled: boolean };
  saveLabel: string;
  lcid: number;
}

/**
 * A main form in create mode as a wizard (UI-2c, ruling C2): one step per shown tab, an indicator "Step n of total", a step
 * list that only goes back, Back (no check), Next (the step's required check) and, on the last step, the form's Save.
 */
export function WizardBody({ tabs, current, env, selectTab, renderTab, save, saveLabel, lcid }: WizardBodyProps) {
  const styles = useStyles();
  const overlay = env.overlay;
  const selected = overlay?.selectedTab();
  const currentTab = current?.tab;
  // A script hid the selected step: the user stays on the step they were moved to, even when the tab is shown again. For one
  // render the wizard draws that neighbour while the overlay still selects the hidden tab.
  useEffect(() => {
    if (currentTab && selected !== undefined && selected !== currentTab) selectTab(currentTab);
  }, [currentTab, selected, selectTab]);
  // After the last step's Save: the save sets the field errors; the first step with a field in error is selected.
  const saving = useRef(false);
  const errors = env.state.errors;
  useEffect(() => {
    if (!saving.current) return;
    saving.current = false;
    const first = tabs.find(({ tab }) => stepAttributes(tab, env).some((a) => Object.hasOwn(errors, a)));
    if (first && first.tab !== currentTab) selectTab(first.tab);
    // Only a change of the errors (the save's required check) moves the step; `tabs` and `env` are read as of that render.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [errors]);
  if (!current) return null;
  const position = tabs.indexOf(current);
  const total = tabs.length;
  const label = (tab: FormTab) => overlay?.element(tab).label ?? labelFor(tab.labels, lcid) ?? tab.name;
  const previous = tabs[position - 1];
  const following = tabs[position + 1];
  // Moving between steps drops a pending "go to the step in error": a Save that never reached the required check (stopped by
  // an OnSave preventDefault, a blocking notification…) must not move the user later.
  const go = (tab: FormTab) => {
    saving.current = false;
    selectTab(tab);
  };
  const goNext = () => {
    saving.current = false;
    if (!following) return;
    if (env.state.checkRequired(stepAttributes(current.tab, env))) go(following.tab);
  };

  return (
    <div className={styles.steps}>
      <TabList
        aria-label={t('form.wizard.steps')}
        selectedValue={String(current.index)}
        onTabSelect={(_, d) => {
          const target = tabs.find(({ index }) => String(index) === d.value);
          if (target && tabs.indexOf(target) < position) go(target.tab);
        }}
      >
        {tabs.map((step, i) => (
          <Tab key={`${step.tab.name}-${step.index}`} value={String(step.index)} disabled={i > position}>
            {label(step.tab)}
          </Tab>
        ))}
      </TabList>
      <Text className={styles.indicator} weight="semibold" role="status">
        {t('form.wizard.step', { n: position + 1, total, label: label(current.tab) })}
      </Text>
      <div className={styles.panel} role="group" aria-label={label(current.tab)}>
        <Fragment key={current.index}>{renderTab(current.tab)}</Fragment>
      </div>
      <div className={styles.actions}>
        {previous && (
          <Button onClick={() => go(previous.tab)}>{t('form.wizard.back')}</Button>
        )}
        {following ? (
          <Button appearance="primary" onClick={goNext}>
            {t('form.wizard.next')}
          </Button>
        ) : (
          save && (
            <Button
              appearance="primary"
              disabled={save.disabled}
              onClick={() => {
                saving.current = true;
                save.run();
              }}
            >
              {saveLabel}
            </Button>
          )
        )}
      </div>
    </div>
  );
}
