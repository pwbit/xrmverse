import { makeStyles, Text, tokens } from '@fluentui/react-components';
import { t } from '../../shell/i18n/t';
import { phraseText } from '../model/phrase';
import { conditionText } from '../model/read';
import type { Branch, StepNode } from '../model/types';
import { cardKey, useCanvas, type Place } from './canvasContext';

const useStyles = makeStyles({
  columns: { display: 'flex', alignItems: 'stretch', columnGap: tokens.spacingHorizontalM, marginTop: tokens.spacingVerticalS },
  column: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
    minWidth: '200px',
    paddingTop: tokens.spacingVerticalS,
    paddingLeft: tokens.spacingHorizontalS,
    paddingRight: tokens.spacingHorizontalS,
    borderTop: `${tokens.strokeWidthThick} solid ${tokens.colorNeutralStroke1}`,
    backgroundColor: tokens.colorNeutralBackground2,
    borderRadius: tokens.borderRadiusMedium,
  },
  head: { display: 'flex', flexDirection: 'column', alignItems: 'center', textAlign: 'center' },
  condition: { color: tokens.colorNeutralForeground3 },
  join: { height: tokens.strokeWidthThick, backgroundColor: tokens.colorNeutralStroke1, marginTop: tokens.spacingVerticalS },
});

/** The label of each branch column, numbering the else-if branches. */
function labels(branches: Branch[]): string[] {
  let elseIf = 0;
  return branches.map((b) => {
    if (b.label === 'elseIf') return t('studio.canvas.branch.elseIf', { n: ++elseIf });
    if (b.label === 'else') return t('studio.canvas.branch.else');
    if (b.label === 'onTimeout') return t('studio.canvas.branch.onTimeout');
    return t('studio.canvas.branch.then');
  });
}

/**
 * The branches of an `if` (then, each else-if, else) or a `wait` (then, on timeout) as side-by-side labelled columns, each
 * with its condition and its steps, joined below by a bar where the flow continues.
 */
export function BranchColumns({ step, place }: { step: StepNode; place: Place }) {
  const styles = useStyles();
  const canvas = useCanvas();
  const inner: Place = step.type === 'if' ? { ...place, insideIf: true } : { ...place, insideWait: true };
  const names = labels(step.branches);
  return (
    <>
      <div className={styles.columns}>
        {step.branches.map((branch, i) => (
          <div key={cardKey(branch.path)} role="group" aria-label={names[i]} className={styles.column}>
            <div className={styles.head}>
              <Text weight="semibold">{names[i]}</Text>
              {branch.when !== undefined && (
                <Text size={200} className={styles.condition}>
                  {phraseText(conditionText(branch.when))}
                </Text>
              )}
            </div>
            {canvas.renderFlow(branch.path, branch.steps, inner)}
          </div>
        ))}
      </div>
      <div data-join="" aria-hidden className={styles.join} />
    </>
  );
}
