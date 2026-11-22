import { makeStyles, tokens } from '@fluentui/react-components';
import { useCallback, useRef, useState, type DragEvent } from 'react';

/** What can be dragged in the form designer; an item only drops on targets of its own kind (ruling G1). */
export type DragKind = 'cell' | 'section' | 'tab' | 'header';

/**
 * Where a drop puts the dragged item: `before` the item `key` (a cell, section, tab or header cell), or at the `end` of
 * the container `key` (a section for a cell, a column for a section, `tabs` for a tab, `header` for a header field).
 */
export interface DropAt {
  where: 'before' | 'end';
  key: string;
}

const mime = (kind: DragKind) => `application/x-xrm-form-${kind}`;

const idOf = (at: DropAt) => `${at.where}:${at.key}`;

const useStyles = makeStyles({
  // The insertion line of a drop target (ruling G1): at the start of an item, or on top of a section or a zone.
  before: { boxShadow: `inset 3px 0 0 0 ${tokens.colorBrandStroke1}` },
  above: { boxShadow: `inset 0 3px 0 0 ${tokens.colorBrandStroke1}` },
});

/**
 * Native HTML5 drag and drop for the designer (as in the Kanban board): `source` makes an element draggable, `target`
 * makes one a drop target of a kind. `over` is the target under the pointer, for the insertion line (`line`). The drag
 * lives in a ref as well as in the data transfer, because a browser hides the data until the drop.
 */
export function useDragMove(enabled: boolean, onDrop: (kind: DragKind, key: string, at: DropAt) => void) {
  const styles = useStyles();
  const dragging = useRef<{ kind: DragKind; key: string }>();
  const [over, setOver] = useState<string>();

  const source = (kind: DragKind, key: string) =>
    enabled
      ? {
          draggable: true,
          onDragStart: (e: DragEvent) => {
            e.stopPropagation();
            dragging.current = { kind, key };
            e.dataTransfer?.setData(mime(kind), key);
            if (e.dataTransfer) e.dataTransfer.effectAllowed = 'move';
          },
          onDragEnd: () => {
            dragging.current = undefined;
            setOver(undefined);
          },
        }
      : {};

  const target = (kind: DragKind, at: DropAt) => {
    const id = idOf(at);
    return enabled
      ? {
          'data-drop': id,
          'data-drop-over': over === id ? 'true' : undefined,
          onDragOver: (e: DragEvent) => {
            if (dragging.current?.kind !== kind) return;
            e.preventDefault();
            e.stopPropagation();
            if (e.dataTransfer) e.dataTransfer.dropEffect = 'move';
            setOver(id);
          },
          onDragLeave: () => setOver((o) => (o === id ? undefined : o)),
          onDrop: (e: DragEvent) => {
            const held = dragging.current;
            if (held?.kind !== kind) return;
            e.preventDefault();
            e.stopPropagation();
            dragging.current = undefined;
            setOver(undefined);
            onDrop(kind, e.dataTransfer?.getData(mime(kind)) || held.key, at);
          },
        }
      : {};
  };

  const isOver = useCallback((at: DropAt) => over === idOf(at), [over]);
  /** The insertion-line class of the target `at` while the pointer is over it: on its start edge, or on its top. */
  const line = (at: DropAt, edge: 'before' | 'above'): string | undefined => (isOver(at) ? styles[edge] : undefined);
  return { source, target, isOver, line };
}
