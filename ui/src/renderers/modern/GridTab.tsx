import { makeStyles, tokens } from '@fluentui/react-components';
import { useRef } from 'react';
import type { Tab } from '../../model/form';
import type { GridPlacement } from '../../parsers/presentationJson';
import { useMediaQuery } from '../../shell/layout/useMediaQuery';
import { isShown, type ControlEnv } from '../classic/form/controlEnv';
import { FormSection } from '../classic/form/FormSection';
import { GRID_COLUMNS, placeSections } from './gridLayout';
import { NARROW_QUERY } from './ModernViewGrid';

const useStyles = makeStyles({
  grid: { display: 'grid', gridTemplateColumns: `repeat(${GRID_COLUMNS}, minmax(0, 1fr))`, columnGap: tokens.spacingHorizontalXL, alignItems: 'start' },
  stack: { display: 'flex', flexDirection: 'column' },
  cell: { minWidth: 0 },
});

/**
 * A tab whose sections the form's overlay places on the 12-column free grid (ruling C3), ignoring the FormXml column split;
 * one column, in reading order, on a narrow screen. Sections hidden by a script are not drawn and take no row.
 */
export function GridTab({ tab, env, placements }: { tab: Tab; env: ControlEnv; placements: Record<string, GridPlacement> }) {
  const styles = useStyles();
  const narrow = useMediaQuery(NARROW_QUERY);
  const warned = useRef(new Set<string>());
  const warn = (message: string) => {
    if (warned.current.has(message)) return;
    warned.current.add(message);
    console.warn('[xrmverse presentation]', message);
  };
  const placed = placeSections(tab, placements, (s) => isShown(env, s), warn);
  return (
    <div className={narrow ? styles.stack : styles.grid} data-testid="grid-tab">
      {placed.map(({ section, col, span, row, rowSpan }, i) => (
        <div
          key={`${section.name}-${i}`}
          className={styles.cell}
          data-testid="grid-section"
          style={narrow ? undefined : { gridColumn: `${col} / span ${span}`, gridRow: `${row} / span ${rowSpan}` }}
        >
          <FormSection section={section} env={env} />
        </div>
      ))}
    </div>
  );
}
