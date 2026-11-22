import { Button, TableHeaderCell, TableSelectionCell, type ButtonProps } from '@fluentui/react-components';
import { forwardRef, useCallback, useState, type MouseEvent } from 'react';
import { t } from '../../shell/i18n/t';

/**
 * The selected row of a web admin list, as in a model-driven view: a row is selected by its selection cell or by a click on
 * the row, and the page's command bar shows the commands that act on it. One row at a time; a click on the selected row
 * clears the selection. A row that leaves the list (a reload, a delete) is no longer selected.
 */
export interface RowSelection<T> {
  /** The selected item, if it is still in the list. */
  selected: T | undefined;
  isSelected: (item: T) => boolean;
  /** Selects `item`, or clears the selection when it is already selected. */
  toggle: (item: T) => void;
  clear: () => void;
}

export function useRowSelection<T>(items: readonly T[], keyOf: (item: T) => string): RowSelection<T> {
  const [key, setKey] = useState<string>();
  const selected = key === undefined ? undefined : items.find((i) => keyOf(i) === key);
  const isSelected = (item: T) => selected !== undefined && keyOf(item) === key;
  const toggle = (item: T) => setKey((k) => (k === keyOf(item) ? undefined : keyOf(item)));
  const clear = useCallback(() => setKey(undefined), []);
  return { selected, isSelected, toggle, clear };
}

/** Elements inside a row that keep their own click (inline switches, checkboxes, links, open buttons). */
const INTERACTIVE = 'button, a, input, select, textarea, label, [role="switch"], [role="checkbox"], [role="radio"], [role="menuitem"]';

/**
 * The props of a selectable `TableRow`: a click on the row (not on a control inside it) toggles the selection, and the selected
 * row is highlighted with `aria-selected`.
 */
export function selectableRow<T>(selection: RowSelection<T>, item: T) {
  const selected = selection.isSelected(item);
  return {
    'aria-selected': selected,
    appearance: selected ? ('brand' as const) : ('none' as const),
    style: { cursor: 'pointer' },
    onClick: (e: MouseEvent<HTMLElement>) => {
      const target = e.target as HTMLElement;
      if (target.closest(INTERACTIVE) && !target.closest('[data-row-select]')) return;
      selection.toggle(item);
    },
  };
}

/** The header of the selection column. */
export function SelectHeaderCell() {
  return <TableHeaderCell aria-label={t('common.col.select')} style={{ width: 44 }} />;
}

/** The selection cell of a row: a radio named after the row (`name`), checked when the row is selected. */
export function SelectCell<T>({ selection, item, name }: { selection: RowSelection<T>; item: T; name: string }) {
  return (
    <TableSelectionCell
      type="radio"
      data-row-select=""
      checked={selection.isSelected(item)}
      radioIndicator={{ 'aria-label': t('common.selectRow', { name }) }}
      onClick={(e: MouseEvent) => {
        e.stopPropagation();
        selection.toggle(item);
      }}
    />
  );
}

/** A command of the page's command bar that acts on the selected row (subtle, as in a model-driven command bar). */
export const RowCommand = forwardRef<HTMLButtonElement, ButtonProps>(function RowCommand(props, ref) {
  return <Button ref={ref} appearance="subtle" {...props} />;
});
