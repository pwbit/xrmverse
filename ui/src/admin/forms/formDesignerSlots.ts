import { useCallback, useEffect, useRef } from 'react';
import { t } from '../../shell/i18n/t';
import type { AttributeMeta } from '../tables/metadata';
import type { CellKind, DesignCell, FormDesign } from './formDesign';

/**
 * What every card of the form designer gets: the design the page holds, the table's own columns, and `onChange` with the
 * next design (the page keeps it and marks the page changed). A card never mutates `design`.
 */
export interface FormDesignCardProps {
  design: FormDesign;
  attributes: AttributeMeta[];
  onChange: (next: FormDesign) => void;
}

/** The item the properties panel edits: a tab, a section, or a body or header cell, by key. */
export type Selection = { kind: 'tab' | 'section' | 'cell'; key: string };

/** The label of the UI language, else the first label, else `fallback` (ruling F9). Empty labels count as missing. */
export function labelOf(labels: Record<number, string>, lcid: number, fallback: string): string {
  const own = labels[lcid]?.trim();
  if (own) return labels[lcid];
  const first = Object.values(labels).find((l) => l.trim() !== '');
  return first ?? fallback;
}

const KIND_KEYS: Record<CellKind, Parameters<typeof t>[0]> = {
  field: 'forms.kind.unknown',
  subgrid: 'forms.kind.subgrid',
  quickview: 'forms.kind.quickview',
  webresource: 'forms.kind.webresource',
  timeline: 'forms.kind.timeline',
  custom: 'forms.kind.custom',
  unknown: 'forms.kind.unknown',
  empty: 'forms.kind.empty',
};

export const kindLabel = (kind: CellKind): string => t(KIND_KEYS[kind]);

/**
 * How a cell is named on the canvas and in its buttons: a field by its label (else the column's display name, else its
 * logical name); any other control as "{label} ({kind})", or its kind when it has no label.
 */
export function cellLabel(cell: DesignCell, lcid: number, attributes: AttributeMeta[]): string {
  if (cell.kind === 'field') {
    const attr = attributes.find((a) => a.logicalName.toLowerCase() === cell.attribute?.toLowerCase());
    return labelOf(cell.labels, lcid, attr?.displayName || cell.attribute || '');
  }
  const label = labelOf(cell.labels, lcid, '');
  return label ? t('forms.canvas.control', { label, kind: kindLabel(cell.kind) }) : kindLabel(cell.kind);
}

/**
 * Keeps the focus on a moved item: call the returned function with the `data-refocus` value of the button pressed
 * before applying the move; after the next render the button with that value (in its new place) gets the focus back.
 */
export function useRefocus(): (id: string) => void {
  const pending = useRef<string>();
  useEffect(() => {
    const id = pending.current;
    if (id === undefined) return;
    pending.current = undefined;
    const target = Array.from(document.querySelectorAll<HTMLElement>('[data-refocus]')).find((el) => el.dataset.refocus === id);
    if (target && document.activeElement !== target) target.focus();
  });
  return useCallback((id: string) => {
    pending.current = id;
  }, []);
}
