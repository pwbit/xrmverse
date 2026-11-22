import { Button, makeStyles, mergeClasses, tokens } from '@fluentui/react-components';
import { useState } from 'react';
import { t } from '../../shell/i18n/t';
import { DismissIcon, NewIcon, NextPageIcon, PreviousPageIcon } from '../../shell/icons';
import { ConfirmDialog } from '../common/ConfirmDialog';
import { FieldPicker, headerPickable } from './FieldPicker';
import { dropOnHeader } from './canvasDrop';
import { addHeaderField, fieldImpact, HEADER_LIMIT, moveHeaderField, removeHeaderField, type DesignCell } from './formDesign';
import { cellLabel, useRefocus, type FormDesignCardProps, type Selection } from './formDesignerSlots';
import { useDragMove, type DropAt } from './useDragMove';

/** The drop target after every header cell (H7). */
const HEADER_END: DropAt = { where: 'end', key: 'header' };

const useStyles = makeStyles({
  chips: { display: 'flex', flexWrap: 'wrap', gap: tokens.spacingHorizontalS, listStyle: 'none', margin: 0, padding: 0 },
  chip: {
    display: 'flex',
    alignItems: 'center',
    flexWrap: 'wrap',
    gap: tokens.spacingHorizontalXXS,
    padding: tokens.spacingHorizontalXS,
    border: `1px solid ${tokens.colorNeutralStroke1}`,
    borderRadius: tokens.borderRadiusMedium,
  },
  // The end of the header (H7): a drop zone of its own, after the last cell.
  end: { alignSelf: 'stretch', minWidth: tokens.spacingHorizontalXXL },
  other: { padding: tokens.spacingHorizontalS, color: tokens.colorNeutralForeground3 },
  hint: { margin: 0, color: tokens.colorNeutralForeground3 },
  add: { display: 'flex', flexWrap: 'wrap', alignItems: 'center', gap: tokens.spacingHorizontalS },
});

export interface HeaderEditorProps extends FormDesignCardProps {
  readOnly: boolean;
  selection?: Selection;
  onSelect: (selection: Selection) => void;
}

/**
 * The header's cells (F8), left to right: field cells can be selected, moved left and right, and removed; other cells
 * are kept and shown read-only. Add takes any viewable column up to {@link HEADER_LIMIT} fields.
 */
export function HeaderEditor({ design, attributes, onChange, readOnly, selection, onSelect }: HeaderEditorProps) {
  const styles = useStyles();
  const refocus = useRefocus();
  const [picking, setPicking] = useState(false);
  const [removing, setRemoving] = useState<{ cell: DesignCell; body: string }>();
  const fields = design.header.filter((c) => c.kind === 'field');
  const full = fields.length >= HEADER_LIMIT;
  const inHeader = new Set(fields.map((c) => c.attribute?.toLowerCase()));

  /** A header field goes before another header cell, or to the end of the header from the end-of-header drop zone (H7) ({@link dropOnHeader}). */
  const drag = useDragMove(!readOnly, (_, key, at) => {
    const next = dropOnHeader(design, key, at);
    if (next === design) return;
    refocus(`${key}:label`);
    onSelect({ kind: 'cell', key });
    onChange(next);
  });

  function remove(cell: DesignCell, label: string) {
    const impact = fieldImpact(design, cell.key, attributes);
    const lines: string[] = [];
    if (impact.handlers > 0) lines.push(t('forms.removeField.handlers', { label, n: impact.handlers }));
    if (impact.required) lines.push(t('forms.removeField.required', { label }));
    if (lines.length === 0) onChange(removeHeaderField(design, cell.key));
    else setRemoving({ cell, body: lines.join(' ') });
  }

  return (
    <>
      {fields.length === 0 && <p className={styles.hint}>{t('forms.header.empty')}</p>}
      {design.header.length > 0 && (
        <ul className={styles.chips} aria-label={t('forms.header.title')}>
          {design.header.map((cell) => {
            const label = cellLabel(cell, design.lcid, attributes);
            const before = { where: 'before', key: cell.key } as const;
            if (cell.kind !== 'field') {
              return (
                <li
                  key={cell.key}
                  className={mergeClasses(styles.chip, styles.other, drag.line(before, 'before'))}
                  aria-label={label}
                  {...drag.target('header', before)}
                >
                  {label}
                </li>
              );
            }
            const at = fields.indexOf(cell);
            const selected = selection?.kind === 'cell' && selection.key === cell.key;
            return (
              <li
                key={cell.key}
                className={mergeClasses(styles.chip, drag.line(before, 'before'))}
                aria-label={label}
                {...drag.source('header', cell.key)}
                {...drag.target('header', before)}
              >
                <Button appearance={selected ? 'primary' : 'subtle'} aria-pressed={selected} data-refocus={`${cell.key}:label`} onClick={() => onSelect({ kind: 'cell', key: cell.key })}>
                  {label}
                </Button>
                {!readOnly && (
                  <>
                    <Button
                      appearance="subtle"
                      size="small"
                      icon={<PreviousPageIcon />}
                      data-refocus={`${cell.key}:left`}
                      aria-label={t('forms.header.moveLeft', { field: label })}
                      disabledFocusable={at === 0}
                      onClick={() => {
                        refocus(`${cell.key}:left`);
                        onChange(moveHeaderField(design, cell.key, -1));
                      }}
                    />
                    <Button
                      appearance="subtle"
                      size="small"
                      icon={<NextPageIcon />}
                      data-refocus={`${cell.key}:right`}
                      aria-label={t('forms.header.moveRight', { field: label })}
                      disabledFocusable={at === fields.length - 1}
                      onClick={() => {
                        refocus(`${cell.key}:right`);
                        onChange(moveHeaderField(design, cell.key, 1));
                      }}
                    />
                    <Button
                      appearance="subtle"
                      size="small"
                      icon={<DismissIcon />}
                      aria-label={t('forms.header.remove', { field: label })}
                      onClick={() => remove(cell, label)}
                    />
                  </>
                )}
              </li>
            );
          })}
          {!readOnly && (
            // Pointer only: the move buttons are the accessible way to move a header field (G1).
            <li aria-hidden="true" className={mergeClasses(styles.end, drag.line(HEADER_END, 'before'))} {...drag.target('header', HEADER_END)} />
          )}
        </ul>
      )}
      {!readOnly && (
        <div className={styles.add}>
          <Button icon={<NewIcon />} disabled={full} onClick={() => setPicking(true)}>
            {t('forms.header.add')}
          </Button>
          {full && <p className={styles.hint}>{t('forms.header.full', { n: HEADER_LIMIT })}</p>}
        </div>
      )}
      {picking && (
        <FieldPicker
          attributes={attributes.filter((a) => headerPickable(a) && !inHeader.has(a.logicalName.toLowerCase()))}
          onClose={() => setPicking(false)}
          onPick={(a) => {
            onChange(addHeaderField(design, a));
            setPicking(false);
          }}
        />
      )}
      <ConfirmDialog
        open={removing !== undefined}
        title={removing ? t('forms.removeField.title', { field: cellLabel(removing.cell, design.lcid, attributes) }) : ''}
        body={removing?.body ?? ''}
        confirmLabel={t('forms.removeField.confirm')}
        cancelLabel={t('common.cancel')}
        onConfirm={() => {
          if (removing) onChange(removeHeaderField(design, removing.cell.key));
          setRemoving(undefined);
        }}
        onCancel={() => setRemoving(undefined)}
      />
    </>
  );
}
