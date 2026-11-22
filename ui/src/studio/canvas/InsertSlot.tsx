import { makeStyles, mergeClasses, tokens } from '@fluentui/react-components';
import { useState } from 'react';
import { moveStep } from '../model/edit';
import type { StepPath } from '../model/types';
import { t } from '../../shell/i18n/t';
import { canMoveTo, DRAG_TYPE, useCanvas, type Place } from './canvasContext';
import { StepPalette } from './StepPalette';

const useStyles = makeStyles({
  slot: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
    borderRadius: tokens.borderRadiusCircular,
    paddingLeft: tokens.spacingHorizontalS,
    paddingRight: tokens.spacingHorizontalS,
  },
  over: { backgroundColor: tokens.colorBrandBackground2, outline: `${tokens.strokeWidthThick} dashed ${tokens.colorBrandStroke1}` },
  line: { width: tokens.strokeWidthThin, height: tokens.spacingVerticalM, backgroundColor: tokens.colorNeutralStroke1 },
});

export interface InsertSlotProps {
  branch: StepPath;
  index: number;
  place: Place;
}

/** The place between two cards (or at the end of a branch): "+" opens the palette, and a dragged card can be dropped here. */
export function InsertSlot({ branch, index, place }: InsertSlotProps) {
  const styles = useStyles();
  const canvas = useCanvas();
  const [over, setOver] = useState(false);

  /** The dragged step, when it may land here (not inside itself, and only where the place takes it: `canMoveTo`). */
  const dragged = (data: DataTransfer | null): StepPath | undefined => {
    let from = canvas.dragging.current;
    if (!from) {
      try {
        from = JSON.parse(data?.getData(DRAG_TYPE) || 'null') as StepPath | undefined;
      } catch {
        from = undefined;
      }
    }
    return from && canMoveTo(canvas.model, from, branch) ? from : undefined;
  };

  return (
    <div
      data-slot=""
      className={mergeClasses(styles.slot, over && styles.over)}
      onDragOver={(e) => {
        if (!dragged(e.dataTransfer)) return;
        e.preventDefault();
        if (e.dataTransfer) e.dataTransfer.dropEffect = 'move';
        setOver(true);
      }}
      onDragLeave={() => setOver(false)}
      onDrop={(e) => {
        setOver(false);
        const from = dragged(e.dataTransfer);
        canvas.dragging.current = undefined;
        if (!from) return;
        e.preventDefault();
        const to = canvas.run((doc) => moveStep(doc, from, branch, index), t('studio.canvas.moveNotHere'));
        if (to) canvas.select(to);
      }}
    >
      <div className={styles.line} aria-hidden />
      <StepPalette branch={branch} index={index} place={place} />
      <div className={styles.line} aria-hidden />
    </div>
  );
}
