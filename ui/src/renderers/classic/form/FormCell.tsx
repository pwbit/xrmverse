import { makeStyles, MessageBar, MessageBarBody, Text, tokens } from '@fluentui/react-components';
import { useEffect, useRef, type CSSProperties, type ReactNode } from 'react';
import type { Cell, Control } from '../../../model/form';
import { labelFor } from '../../../parsers/formXml';
import { t } from '../../../shell/i18n/t';
import { isShown, type ControlEnv, type LabelPosition } from './controlEnv';
import { FieldControl } from './FieldControl';
import { PlaceholderControl } from './PlaceholderControl';
import { QuickViewControl } from './QuickViewControl';
import { SubgridControl } from './SubgridControl';
import { TimelineControl } from './timeline/TimelineControl';
import { WebResourceControl } from './WebResourceControl';

const useStyles = makeStyles({
  // A cell is a container so its own width decides where its label goes (see `left`).
  cell: { minWidth: 0, containerType: 'inline-size', containerName: 'xrmverse-cell' },
  // The label sits beside the control (Left) or above it; in the header, under it. Fluent's Field owns the label, so its parts are placed here.
  // These selectors rely on the static class names (`.fui-Field`, `.fui-Field__label`) and the child structure of Fluent's
  // Field in the pinned @fluentui/react-components 9.75.1 (package.json): check the form layout when that version changes.
  left: {
    '& .fui-Field': {
      display: 'grid',
      gridTemplateColumns: 'minmax(6rem, 30%) minmax(0, 1fr)',
      columnGap: tokens.spacingHorizontalM,
      alignItems: 'start',
    },
    '& .fui-Field__label': { gridColumn: 1 },
    '& .fui-Field > :not(.fui-Field__label)': { gridColumn: 2 },
    // Too narrow for label and control side by side: the label goes above, as in Unified Interface on a narrow screen.
    '@container xrmverse-cell (max-width: 20rem)': {
      '& .fui-Field': { display: 'flex', flexDirection: 'column', alignItems: 'stretch' },
    },
  },
  top: {},
  bottom: {
    '& .fui-Field': { display: 'flex', flexDirection: 'column-reverse' },
  },
  labelHidden: {
    '& .fui-Field__label': {
      position: 'absolute',
      width: '1px',
      height: '1px',
      overflow: 'hidden',
      clipPath: 'inset(50%)',
      whiteSpace: 'nowrap',
    },
  },
  controlLabel: { display: 'block', marginBlockEnd: tokens.spacingVerticalXXS },
  // A coloured field: the colour fills the control's box (the gray input), never the cell, so the label and the cell stay as they are.
  // The selectors rely on Fluent's static root class names (see `left` above); a custom box adds `data-field-box`.
  fieldFill: {
    '& .fui-Input, & .fui-Textarea, & .fui-Combobox, & .fui-Dropdown, & .fui-SpinButton, & .fui-Select__select, & [data-field-box]': {
      backgroundColor: 'var(--xrmverse-field-fill)',
    },
  },
});

/** Draws a cell's control in place of the form's (a dashboard); `label` is the cell's label when it shows one. */
export type RenderControl = (cell: Cell, control: Control, label: string | undefined) => ReactNode;

export interface FormCellProps {
  cell: Cell;
  env: ControlEnv;
  labelPosition: LabelPosition;
  /** The footer shows its cells read-only. */
  readOnly?: boolean;
  /** Draws the control and its label in place of the form's controls. */
  renderControl?: RenderControl;
  /** The colour of the field's control box (a CSS colour, the modern form's overlay); absent, the control keeps its own fill; the cell itself is never painted. */
  background?: string;
}

/** One cell of a section, the header or the footer: its control, with its label. A cell without a control keeps its space. */
export function FormCell({ cell, env, labelPosition, readOnly = false, renderControl, background }: FormCellProps) {
  const styles = useStyles();
  const ref = useRef<HTMLDivElement>(null);
  const own = env.overlay?.element(cell);
  // A script's setFocus(): the first control of the cell that takes focus.
  const focus = own?.focus;
  useEffect(() => {
    // The timeline places the focus itself (its first item, else its search box).
    if (focus && cell.control?.kind !== 'timeline') ref.current?.querySelector<HTMLElement>('input, textarea, select, button, [tabindex]')?.focus();
  }, [focus]);
  if (!isShown(env, cell)) return null;
  const control = cell.control;
  // A cell that hides its label gives the control the whole width: no label track beside it, nothing above or below it.
  const classes = [styles.cell, cell.showLabel ? styles[labelPosition] : styles.labelHidden, background && styles.fieldFill].filter(Boolean).join(' ');
  const label = own?.label ?? labelFor(cell.labels, env.lcid);
  return (
    <div
      ref={ref}
      className={classes}
      data-testid="form-cell"
      data-label-position={labelPosition}
      data-label-hidden={cell.showLabel ? undefined : 'true'}
      data-field-fill={background ? 'true' : undefined}
      style={{ gridColumn: `span ${cell.colSpan}`, ...(background ? ({ '--xrmverse-field-fill': background } as CSSProperties) : {}) }}
    >
      {!control ? null : renderControl ? (
        renderControl(cell, control, cell.showLabel ? label : undefined)
      ) : control.kind === 'field' ? (
        <FieldControl cell={cell} control={control} env={env} forceReadOnly={readOnly} />
      ) : control.kind === 'quickview' ? (
        <QuickViewControl control={control} env={env} label={cell.showLabel ? label : undefined} />
      ) : control.kind === 'timeline' ? (
        <TimelineControl control={control} env={env} label={cell.showLabel ? label : undefined} reload={own?.reload} focus={focus} disabled={own?.disabled === true} />
      ) : (
        <>
          {/* A subgrid draws its own title, on one line with its commands. */}
          {cell.showLabel && label && control.kind !== 'subgrid' && (
            <Text weight="semibold" className={styles.controlLabel}>
              {label}
            </Text>
          )}
          {control.kind === 'subgrid' ? (
            <>
              {/* A form subgrid's chart needs the parent record's filter on the chart query, which is not built: the grid is shown, and said so. */}
              {control.chartGridMode === 'Chart' && control.visualizationId && (
                <MessageBar intent="info">
                  <MessageBarBody>{t('record.subgrid.chartNotShown')}</MessageBarBody>
                </MessageBar>
              )}
              <SubgridControl control={control} env={env} reload={own?.reload} label={label} title={cell.showLabel ? label : undefined} />
            </>
          ) : control.kind === 'webresource' ? (
            <WebResourceControl cell={cell} control={control} env={env} />
          ) : (
            <PlaceholderControl />
          )}
        </>
      )}
    </div>
  );
}
