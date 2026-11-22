import { makeStyles, mergeClasses, Subtitle2, tokens } from '@fluentui/react-components';
import type { CSSProperties } from 'react';
import type { Cell, Section } from '../../../model/form';
import { labelFor } from '../../../parsers/formXml';
import { isShown, type ControlEnv } from './controlEnv';
import { FormCell, type RenderControl } from './FormCell';
import { useSurfaceStyles } from '../../../shell/layout/surface';

/** Below this width of a section, its columns collapse into one, each cell over the whole row. */
export const SECTION_COLLAPSE_WIDTH = '32rem';

const useStyles = makeStyles({
  section: { containerType: 'inline-size', containerName: 'xrmverse-section' },
  row: {
    display: 'grid',
    columnGap: tokens.spacingHorizontalL,
    rowGap: tokens.spacingVerticalM,
    alignItems: 'start',
    gridTemplateColumns: 'var(--xrmverse-section-tracks)',
    [`@container xrmverse-section (max-width: ${SECTION_COLLAPSE_WIDTH})`]: {
      gridTemplateColumns: 'minmax(0, 1fr)',
      '& > *': { gridColumn: '1 / -1 !important' },
    },
  },
});

/** The grid tracks of a section's columns: equal, or at the relative widths of `columns="12"`. */
const tracks = (section: Section): string =>
  section.widths ? section.widths.map((w) => `minmax(0, ${w}fr)`).join(' ') : `repeat(${section.columns}, minmax(0, 1fr))`;

/** A section: its label and its rows, each a CSS grid of the section's `columns` tracks in which a cell spans its `colSpan`. */
export function FormSection({ section, env, readOnly = false, renderControl }: { section: Section; env: ControlEnv; readOnly?: boolean; renderControl?: RenderControl }) {
  const styles = useStyles();
  const surface = useSurfaceStyles();
  if (!isShown(env, section)) return null;
  const label = section.showLabel ? (env.overlay?.element(section).label ?? labelFor(section.labels, env.lcid) ?? '') : '';
  const rows = section.rows.filter((row) => row.some((c) => isShown(env, c)));
  // The modern overlay's colours: the section's shows behind its cells; a field's own colour is drawn on its cell, over it.
  const background = env.backgrounds?.section(section.name);
  const fieldBackground = (cell: Cell) => (cell.control?.kind === 'field' ? env.backgrounds?.field(cell.control.attribute) : undefined);
  return (
    <div
      className={mergeClasses(surface.section, styles.section)}
      style={background ? { backgroundColor: background } : undefined}
      data-background={background ? 'true' : undefined}
      role="group"
      aria-label={label || undefined}
      data-columns={section.columns}
    >
      {label && (
        <Subtitle2 as="h3" className={surface.sectionTitle}>
          {label}
        </Subtitle2>
      )}
      {rows.map((row, i) => (
        <div key={i} className={styles.row} data-testid="section-row" style={{ '--xrmverse-section-tracks': tracks(section) } as CSSProperties}>
          {row.map((cell) => (
            <FormCell key={cell.id} cell={cell} env={env} labelPosition={section.labelPosition} readOnly={readOnly} renderControl={renderControl} background={fieldBackground(cell)} />
          ))}
        </div>
      ))}
    </div>
  );
}
