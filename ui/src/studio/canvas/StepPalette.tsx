import { Button, Divider, makeStyles, Popover, PopoverSurface, PopoverTrigger, Text, tokens } from '@fluentui/react-components';
import { AddRegular, ClipboardPasteRegular } from '@fluentui/react-icons';
import { useState } from 'react';
import { t } from '../../shell/i18n/t';
import { insertStep, moveStep } from '../model/edit';
import { paletteFor } from '../model/palette';
import { phraseText } from '../model/phrase';
import type { StepPath } from '../model/types';
import { canMoveTo, STEP_ICONS, STEP_LABEL, useCanvas, within, type Place } from './canvasContext';

const useStyles = makeStyles({
  surface: { display: 'flex', flexDirection: 'column', rowGap: tokens.spacingVerticalXS, maxWidth: '360px', maxHeight: '60vh', overflowY: 'auto' },
  entry: { justifyContent: 'flex-start' },
  heading: { color: tokens.colorNeutralForeground3, paddingTop: tokens.spacingVerticalXS },
  missing: { display: 'flex', flexDirection: 'column', paddingLeft: tokens.spacingHorizontalS, paddingRight: tokens.spacingHorizontalS },
  missingLabel: { color: tokens.colorNeutralForegroundDisabled },
  reason: { color: tokens.colorNeutralForeground3 },
});

export interface StepPaletteProps {
  /** The branch list the slot belongs to and the slot's position in it. */
  branch: StepPath;
  index: number;
  place: Place;
}

/**
 * The "+" of a slot and its palette: the steps valid for the process's kind and the slot's place (`paletteFor`), Paste when a
 * step was cut, and, disabled with the reason and the alternative, the steps the library lacks.
 */
export function StepPalette({ branch, index, place }: StepPaletteProps) {
  const styles = useStyles();
  const canvas = useCanvas();
  const [open, setOpen] = useState(false);
  const entries = paletteFor(canvas.model.kind, canvas.model.kind === 'workflow' ? canvas.model.mode : undefined, place);
  const clip = canvas.clip && !within(branch, canvas.clip.path) ? canvas.clip : undefined;
  // Paste follows the palette's place rule: where the cut step (or a step inside it) is not valid, it is shown disabled.
  const canPaste = clip !== undefined && canMoveTo(canvas.model, clip.path, branch);

  const done = (path: StepPath | undefined) => {
    setOpen(false);
    if (path) canvas.select(path);
  };

  return (
    <Popover open={open} onOpenChange={(_, d) => setOpen(d.open)} positioning="below" trapFocus>
      <PopoverTrigger disableButtonEnhancement>
        <Button size="small" shape="circular" icon={<AddRegular />} aria-label={t('studio.canvas.insert')} title={t('studio.canvas.insert')} />
      </PopoverTrigger>
      <PopoverSurface role="dialog" aria-label={t('studio.canvas.palette')} className={styles.surface}>
        {clip && (
          <>
            <Button
              appearance="subtle"
              className={styles.entry}
              icon={<ClipboardPasteRegular />}
              disabledFocusable={!canPaste}
              onClick={() => canPaste && done(canvas.run((doc) => moveStep(doc, clip.path, branch, index), t('studio.canvas.moveNotHere')))}
            >
              {t('studio.canvas.pasteHere', { summary: phraseText(clip.summary) })}
            </Button>
            {!canPaste && (
              <Text size={200} className={styles.reason}>
                {t('studio.canvas.moveNotHere')}
              </Text>
            )}
            <Divider />
          </>
        )}
        {entries.map((entry) => {
          if (!entry.enabled) return null;
          const Icon = STEP_ICONS[entry.type];
          return (
            <Button
              key={entry.type}
              appearance="subtle"
              className={styles.entry}
              icon={<Icon />}
              onClick={() => done(canvas.run((doc) => insertStep(doc, branch, index, entry.type)))}
            >
              {t(STEP_LABEL(entry.type))}
            </Button>
          );
        })}
        <Text size={200} weight="semibold" className={styles.heading}>
          {t('studio.canvas.palette.unavailable')}
        </Text>
        {entries.map((entry) =>
          entry.enabled ? null : (
            <div key={entry.label} className={styles.missing} aria-disabled="true">
              <Text className={styles.missingLabel}>{t(entry.label)}</Text>
              <Text size={200} className={styles.reason}>
                {t(entry.reason)}
              </Text>
            </div>
          ),
        )}
      </PopoverSurface>
    </Popover>
  );
}
