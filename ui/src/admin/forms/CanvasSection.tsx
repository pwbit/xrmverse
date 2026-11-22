import { Button, makeStyles, mergeClasses, tokens } from '@fluentui/react-components';
import { t } from '../../shell/i18n/t';
import {
  DeleteIcon, DismissIcon, MoveDownIcon, MoveNextIcon, MovePreviousIcon, MoveUpIcon, NewIcon, NextPageIcon, PreviousPageIcon,
} from '../../shell/icons';
import type { AttributeMeta } from '../tables/metadata';
import { clampSpan } from './formCells';
import { moveSection, type DesignCell, type DesignSection, type FormDesign } from './formDesign';
import { cellLabel, labelOf, type Selection } from './formDesignerSlots';
import type { useDragMove } from './useDragMove';

const useStyles = makeStyles({
  toolbar: { display: 'flex', flexWrap: 'wrap', alignItems: 'center', gap: tokens.spacingHorizontalXS },
  section: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXS,
    padding: tokens.spacingHorizontalS,
    border: `1px solid ${tokens.colorNeutralStroke1}`,
    borderRadius: tokens.borderRadiusMedium,
  },
  cells: { display: 'grid', gap: tokens.spacingHorizontalXS },
  cell: {
    display: 'flex',
    flexWrap: 'wrap',
    alignItems: 'center',
    gap: tokens.spacingHorizontalXXS,
    padding: tokens.spacingHorizontalXXS,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
    minWidth: 0,
  },
  empty: {
    padding: tokens.spacingHorizontalXS,
    border: `1px dashed ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
    color: tokens.colorNeutralForeground3,
  },
  label: { overflowWrap: 'anywhere', textAlign: 'start' },
  hint: { color: tokens.colorNeutralForeground3, margin: 0 },
  zone: { minHeight: tokens.spacingVerticalS, borderRadius: tokens.borderRadiusSmall },
});

export type CellMove = 'up' | 'down' | 'prev-section' | 'next-section';

export interface CanvasSectionProps {
  section: DesignSection;
  /** The section's column in the tab shown: its index, the tab's column count, and how many sections it holds. */
  column: { index: number; count: number; sections: number };
  /** The section's place in the column. */
  index: number;
  design: FormDesign;
  attributes: AttributeMeta[];
  readOnly: boolean;
  selection?: Selection;
  onSelect: (selection: Selection) => void;
  drag: ReturnType<typeof useDragMove>;
  /** Every section key in reading order (all tabs), for a cell's previous and next section. */
  order: string[];
  /** The row of each cell as the write stores it. */
  rowOf: Map<string, number>;
  /** Whether the section can be deleted: not the only one of its tab. */
  deletable: boolean;
  /** Applies a move of the section and puts the focus back on the `id` button. */
  onMove: (id: string, next: FormDesign) => void;
  onMoveCell: (cell: DesignCell, to: CellMove, id: string) => void;
  onRemoveCell: (cell: DesignCell) => void;
  onDelete: () => void;
  onAddField: () => void;
}

/** One section of the canvas: its toolbar, its cells on the section's grid, and the drop zone at its end (F3, F4, F6, G1). */
export function CanvasSection({
  section: s, column, index: si, design, attributes, readOnly, selection, onSelect, drag, order, rowOf, deletable,
  onMove, onMoveCell, onRemoveCell, onDelete, onAddField,
}: CanvasSectionProps) {
  const styles = useStyles();
  const lcid = design.lcid;
  const label = labelOf(s.labels, lcid, s.name);
  const at = order.indexOf(s.key);
  const ci = column.index;
  const selected = selection?.kind === 'section' && selection.key === s.key;
  return (
    <section
      className={mergeClasses(styles.section, drag.line({ where: 'before', key: s.key }, 'above'))}
      aria-label={t('forms.canvas.section', { section: label })}
      {...drag.source('section', s.key)}
      {...drag.target('section', { where: 'before', key: s.key })}
    >
      <div className={styles.toolbar}>
        <Button
          appearance={selected ? 'primary' : 'transparent'}
          aria-pressed={selected}
          className={styles.label}
          data-refocus={`${s.key}:label`}
          onClick={() => onSelect({ kind: 'section', key: s.key })}
        >
          {label}
        </Button>
        {!readOnly && (
          <>
            <Button
              appearance="subtle"
              icon={<MoveUpIcon />}
              data-refocus={`${s.key}:up`}
              aria-label={t('forms.canvas.moveSectionUp', { section: label })}
              disabledFocusable={si === 0}
              onClick={() => onMove(`${s.key}:up`, moveSection(design, s.key, 'up'))}
            />
            <Button
              appearance="subtle"
              icon={<MoveDownIcon />}
              data-refocus={`${s.key}:down`}
              aria-label={t('forms.canvas.moveSectionDown', { section: label })}
              disabledFocusable={si === column.sections - 1}
              onClick={() => onMove(`${s.key}:down`, moveSection(design, s.key, 'down'))}
            />
            <Button
              appearance="subtle"
              icon={<PreviousPageIcon />}
              data-refocus={`${s.key}:prev`}
              aria-label={t('forms.canvas.moveSectionPrev', { section: label })}
              disabledFocusable={ci === 0}
              onClick={() => onMove(`${s.key}:prev`, moveSection(design, s.key, 'prev-column'))}
            />
            <Button
              appearance="subtle"
              icon={<NextPageIcon />}
              data-refocus={`${s.key}:next`}
              aria-label={t('forms.canvas.moveSectionNext', { section: label })}
              disabledFocusable={ci === column.count - 1}
              onClick={() => onMove(`${s.key}:next`, moveSection(design, s.key, 'next-column'))}
            />
            <Button appearance="subtle" icon={<NewIcon />} aria-label={t('forms.canvas.addField', { section: label })} onClick={onAddField} />
            <Button
              appearance="subtle"
              icon={<DeleteIcon />}
              aria-label={t('forms.canvas.deleteSection', { section: label })}
              disabled={!deletable}
              onClick={onDelete}
            />
          </>
        )}
      </div>
      {s.cells.length === 0 && <p className={styles.hint}>{t('forms.canvas.emptySection')}</p>}
      <div className={styles.cells} style={{ gridTemplateColumns: `repeat(${s.columns}, minmax(0, 1fr))` }}>
        {s.cells.map((cell, i) => {
          const span = {
            gridColumn: `span ${clampSpan(cell, s.columns).colSpan}`,
            gridRow: `${rowOf.get(cell.key) ?? 'auto'} / span ${cell.rowSpan}`,
          };
          if (cell.kind === 'empty') {
            return (
              <div key={cell.key} className={styles.empty} style={span}>
                {t('forms.kind.empty')}
              </div>
            );
          }
          const name = cellLabel(cell, lcid, attributes);
          const chosen = selection?.kind === 'cell' && selection.key === cell.key;
          const before = { where: 'before', key: cell.key } as const;
          return (
            <div
              key={cell.key}
              className={mergeClasses(styles.cell, drag.line(before, 'before'))}
              style={span}
              {...drag.source('cell', cell.key)}
              {...drag.target('cell', before)}
            >
              <Button
                appearance={chosen ? 'primary' : 'subtle'}
                aria-pressed={chosen}
                className={styles.label}
                data-refocus={`${cell.key}:label`}
                onClick={() => onSelect({ kind: 'cell', key: cell.key })}
              >
                {name}
              </Button>
              {!readOnly && (
                <>
                  <Button
                    appearance="subtle"
                    size="small"
                    icon={<MoveUpIcon />}
                    data-refocus={`${cell.key}:up`}
                    aria-label={t('forms.canvas.moveFieldUp', { field: name })}
                    disabledFocusable={i === 0}
                    onClick={() => onMoveCell(cell, 'up', `${cell.key}:up`)}
                  />
                  <Button
                    appearance="subtle"
                    size="small"
                    icon={<MoveDownIcon />}
                    data-refocus={`${cell.key}:down`}
                    aria-label={t('forms.canvas.moveFieldDown', { field: name })}
                    disabledFocusable={i === s.cells.length - 1}
                    onClick={() => onMoveCell(cell, 'down', `${cell.key}:down`)}
                  />
                  <Button
                    appearance="subtle"
                    size="small"
                    icon={<MovePreviousIcon />}
                    data-refocus={`${cell.key}:prev`}
                    aria-label={t('forms.canvas.moveFieldPrev', { field: name })}
                    disabledFocusable={at <= 0}
                    onClick={() => onMoveCell(cell, 'prev-section', `${cell.key}:prev`)}
                  />
                  <Button
                    appearance="subtle"
                    size="small"
                    icon={<MoveNextIcon />}
                    data-refocus={`${cell.key}:next`}
                    aria-label={t('forms.canvas.moveFieldNext', { field: name })}
                    disabledFocusable={at === order.length - 1}
                    onClick={() => onMoveCell(cell, 'next-section', `${cell.key}:next`)}
                  />
                  <Button
                    appearance="subtle"
                    size="small"
                    icon={<DismissIcon />}
                    aria-label={t('forms.canvas.removeField', { field: name })}
                    onClick={() => onRemoveCell(cell)}
                  />
                </>
              )}
            </div>
          );
        })}
      </div>
      {/* Pointer only (A1): the move buttons are the accessible way to move a cell (G1). */}
      {!readOnly && (
        <div
          aria-hidden="true"
          className={mergeClasses(styles.zone, drag.line({ where: 'end', key: s.key }, 'above'))}
          {...drag.target('cell', { where: 'end', key: s.key })}
        />
      )}
    </section>
  );
}
