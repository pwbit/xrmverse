import { makeStyles, tokens } from '@fluentui/react-components';
import type { CSSProperties, ReactNode } from 'react';
import type { Tab } from '../../../model/form';
import type { ControlEnv } from './controlEnv';
import type { RenderControl } from './FormCell';
import { FormSection } from './FormSection';

/** Below this width of the tab, its columns stack in one column (the Timeline under the main column), as in Unified Interface. */
export const TAB_STACK_WIDTH = '48rem';

const useStyles = makeStyles({
  // The tab's width, not the window's, decides: a form in a side pane stacks too.
  tab: { containerType: 'inline-size', containerName: 'xrmverse-tab' },
  columns: {
    display: 'grid',
    columnGap: tokens.spacingHorizontalXL,
    alignItems: 'start',
    gridTemplateColumns: 'var(--xrmverse-tab-columns)',
    [`@container xrmverse-tab (max-width: ${TAB_STACK_WIDTH})`]: { gridTemplateColumns: 'minmax(0, 1fr)' },
  },
  column: { minWidth: 0 },
  // The lead (an aside) has the vertical margins of a section card, so its top lines up with the first section of the other columns.
  lead: { marginBlock: tokens.spacingVerticalS },
});

/** The grid track of a column width: `60%` is `60fr`, so the columns keep their proportions whatever the gaps. */
function track(width: string): string {
  const n = Number.parseFloat(width);
  return Number.isFinite(n) && n > 0 ? `${n}fr` : '1fr';
}

/**
 * The body of a tab: its columns side by side at their percentage widths, each stacking its sections (`lead` is drawn first in its column); `readOnly` draws every
 * control read-only, and `renderControl` (a dashboard) draws the cells' controls in place of the form's.
 */
export function FormTab({ tab, env, readOnly = false, renderControl, lead }: { tab: Tab; env: ControlEnv; readOnly?: boolean; renderControl?: RenderControl; lead?: { column: number; node: ReactNode } }) {
  const styles = useStyles();
  return (
    <div className={styles.tab}>
    <div className={styles.columns} style={{ '--xrmverse-tab-columns': tab.columns.map((c) => track(c.width)).join(' ') } as CSSProperties}>
      {tab.columns.map((column, i) => (
        <div key={i} className={styles.column} data-testid="tab-column" data-form-column="" data-width={column.width}>
          {lead?.column === i && (
            <div className={styles.lead} data-form-lead="">
              {lead.node}
            </div>
          )}
          {column.sections.map((section, j) => (
            <FormSection key={`${section.name}-${j}`} section={section} env={env} readOnly={readOnly} renderControl={renderControl} />
          ))}
        </div>
      ))}
    </div>
    </div>
  );
}
