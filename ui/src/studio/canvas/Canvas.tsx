import { Button, Card, makeStyles, MessageBar, MessageBarActions, MessageBarBody, mergeClasses, Text, tokens } from '@fluentui/react-components';
import { DismissRegular, FlashRegular, ZoomFitRegular, ZoomInRegular, ZoomOutRegular } from '@fluentui/react-icons';
import { Fragment, useCallback, useEffect, useMemo, useRef, useState } from 'react';
import type { Document } from 'yaml';
import { ConfirmDialog } from '../../admin/common/ConfirmDialog';
import { t } from '../../shell/i18n/t';
import type { EditorProblem } from '../editor/problems';
import { stepAtLine } from '../editor/problems';
import type { EditorDocument } from '../editor/useEditorDocument';
import { removeStep, StudioEditError } from '../model/edit';
import { phraseText } from '../model/phrase';
import type { StepNode, StepPath, WorkflowModel } from '../model/types';
import { CanvasContext, cardKey, stepOf, type CanvasContextValue, type CardProblems, type Place } from './canvasContext';
import { InsertSlot } from './InsertSlot';
import { ProblemBadge, StepCard, useCardBehaviour } from './StepCard';
import { useZoomPan } from './useZoomPan';

/** What the editor hands the canvas. */
export interface CanvasProps {
  /** The document's model; the editor shows the "fix the problems" notice instead of the canvas while there is none. */
  model: WorkflowModel;
  /** The selected card: a step's path, or the trigger card. */
  selected?: StepPath | 'trigger';
  onSelect?(selected: StepPath | 'trigger'): void;
  /** Applies an `edit.ts` operation to the document (`useEditorDocument().mutate`); without it the canvas is read-only. */
  mutate?: EditorDocument['mutate'];
  /** The problems that have a line, by 1-based line: the card that holds the line shows a badge. */
  problemsByLine: Map<number, EditorProblem[]>;
  /** The zoom to start with (the editor keeps it for its session while the view switches); 1 by default. */
  zoom?: number;
  onZoomChange?(zoom: number): void;
}

const ROOT: StepPath = ['steps'];
const TOP: Place = { insideIf: false, insideWait: false };

const useStyles = makeStyles({
  root: { display: 'flex', flexDirection: 'column', rowGap: tokens.spacingVerticalS, minWidth: 0 },
  toolbar: { display: 'flex', alignItems: 'center', columnGap: tokens.spacingHorizontalXS },
  viewport: {
    overflow: 'auto',
    minHeight: '320px',
    height: '60vh',
    backgroundColor: tokens.colorNeutralBackground3,
    borderRadius: tokens.borderRadiusMedium,
    cursor: 'grab',
  },
  sizer: { position: 'relative', margin: '0 auto' },
  stage: {
    width: 'max-content',
    transformOrigin: '0 0',
    padding: tokens.spacingHorizontalXXL,
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
  },
  measured: { position: 'absolute', top: 0, left: 0 },
  flow: { display: 'flex', flexDirection: 'column', alignItems: 'center' },
  trigger: { minWidth: '280px', cursor: 'pointer' },
  selected: { outline: `${tokens.strokeWidthThick} solid ${tokens.colorBrandStroke1}` },
  head: { display: 'flex', alignItems: 'center', columnGap: tokens.spacingHorizontalS },
  icon: { fontSize: tokens.fontSizeBase500, color: tokens.colorBrandForeground1 },
  grow: { flexGrow: 1 },
  meta: { color: tokens.colorNeutralForeground3 },
  events: { margin: 0, paddingLeft: tokens.spacingHorizontalL },
  connector: { width: tokens.strokeWidthThin, height: tokens.spacingVerticalXL, backgroundColor: tokens.colorNeutralStroke1 },
});

const isOn = (value: unknown) => value !== undefined && value !== null && value !== 'false';

/** The trigger in our own words: when the process runs. */
function triggerLines(model: WorkflowModel): string[] {
  if (model.kind === 'businessRule') return [t('studio.canvas.trigger.rule')];
  if (model.kind === 'action') return [t('studio.canvas.trigger.action', { message: String(model.header.message ?? '') })];
  const trigger = (model.trigger && typeof model.trigger === 'object' ? model.trigger : {}) as Record<string, unknown>;
  const lines: string[] = [];
  if (isOn(trigger.create)) lines.push(t('studio.canvas.trigger.create'));
  if (isOn(trigger.update)) {
    const columns = (trigger.update as { columns?: unknown } | null)?.columns;
    lines.push(Array.isArray(columns) && columns.length > 0 ? t('studio.canvas.trigger.update', { columns: columns.join(', ') }) : t('studio.canvas.trigger.updateAny'));
  }
  if (isOn(trigger.delete)) lines.push(t('studio.canvas.trigger.delete'));
  if (model.header.onDemand === 'true') lines.push(t('studio.canvas.trigger.onDemand'));
  if (model.header.childProcess === 'true') lines.push(t('studio.canvas.trigger.child'));
  return lines.length > 0 ? lines : [t('studio.canvas.trigger.none')];
}

/** The first card: what starts the process. It has no menu: the trigger cannot be removed or moved. */
function TriggerCard({ model }: { model: WorkflowModel }) {
  const styles = useStyles();
  const card = useCardBehaviour('trigger');
  const meta = [
    t(`studio.kind.${model.kind}`),
    model.entity ?? t('studio.list.global'),
    ...(model.kind === 'workflow' && model.mode ? [t(`studio.mode.${model.mode}`)] : []),
  ].join(' · ');
  return (
    <Card {...card.props} aria-label={t('studio.canvas.trigger')} className={mergeClasses(styles.trigger, card.selected && styles.selected)}>
      <div className={styles.head}>
        <FlashRegular className={styles.icon} aria-hidden />
        <div className={styles.grow}>
          <Text weight="semibold" block>
            {t('studio.canvas.trigger')}
          </Text>
          <Text size={200} className={styles.meta}>
            {meta}
          </Text>
        </div>
        <ProblemBadge problems={card.problems} />
      </div>
      <ul className={styles.events}>
        {triggerLines(model).map((line) => (
          <li key={line}>
            <Text>{line}</Text>
          </li>
        ))}
      </ul>
    </Card>
  );
}

function Flow({ branch, steps, place, readOnly }: { branch: StepPath; steps: StepNode[]; place: Place; readOnly: boolean }) {
  const styles = useStyles();
  // A read-only diagram draws the connector between the cards without the "+" slot.
  const slot = (index: number) => (readOnly ? <div className={styles.connector} aria-hidden /> : <InsertSlot branch={branch} index={index} place={place} />);
  return (
    <div className={styles.flow}>
      {steps.map((step, i) => (
        <Fragment key={cardKey(step.path)}>
          {slot(i)}
          <StepCard step={step} index={i} count={steps.length} place={place} />
        </Fragment>
      ))}
      {slot(steps.length)}
    </div>
  );
}


/**
 * The flow canvas (spec §4.5): the trigger card, then the steps top to bottom with a "+" slot between the cards and at the end
 * of every branch; `if` and `wait` draw their branches as columns joined below. Cards move with their menu (Move up / Move
 * down / Cut / Paste) or by dragging onto a slot; Delete asks first when the card has steps inside it. Zoom 25-200 % (buttons,
 * Ctrl+wheel, Fit) and pan (drag the empty canvas, scrollbars).
 */
export function Canvas({ model, selected, onSelect, mutate, problemsByLine, zoom: initialZoom, onZoomChange }: CanvasProps) {
  const styles = useStyles();
  const view = useZoomPan(initialZoom, onZoomChange);
  const [error, setError] = useState<string>();
  const [cut, setCut] = useState<{ path: StepPath; model: WorkflowModel }>();
  const [deleting, setDeleting] = useState<StepNode>();
  const dragging = useRef<StepPath | undefined>(undefined);

  // The unscaled size of the flow: the scroll area is that size times the zoom.
  const [size, setSize] = useState<{ width: number; height: number }>();
  const [stage, setStage] = useState<HTMLElement | null>(null);
  const contentRef = view.contentRef;
  const stageRef = useCallback(
    (el: HTMLElement | null) => {
      contentRef(el);
      setStage(el);
    },
    [contentRef],
  );
  useEffect(() => {
    if (!stage) return;
    const measure = () => setSize({ width: stage.offsetWidth, height: stage.offsetHeight });
    measure();
    const observer = new ResizeObserver(measure);
    observer.observe(stage);
    return () => observer.disconnect();
  }, [stage]);
  const measured = size !== undefined && size.width > 0 && size.height > 0;

  const problems = useMemo(() => {
    const byCard = new Map<string, CardProblems>();
    for (const [line, list] of problemsByLine) {
      const at = stepAtLine(model, line);
      if (!at || list.length === 0) continue;
      const key = cardKey(at);
      const was = byCard.get(key) ?? { count: 0, error: false };
      byCard.set(key, { count: was.count + list.length, error: was.error || list.some((p) => p.severity === 'error') });
    }
    return byCard;
  }, [model, problemsByLine]);

  const readOnly = mutate === undefined;
  const run = useCallback(
    <T,>(fn: (doc: Document.Parsed) => T, message?: string): T | undefined => {
      if (!mutate) return undefined;
      try {
        const out = mutate(fn);
        setError(undefined);
        setCut(undefined);
        return out;
      } catch (e) {
        if (!(e instanceof StudioEditError)) throw e;
        setError(message ?? t('studio.canvas.editFailed', { detail: e.message }));
        return undefined;
      }
    },
    [mutate],
  );

  // A cut step waits for Paste while the document is the one it was cut from.
  const clip = cut && cut.model === model ? stepOf(model, cut.path) : undefined;

  const context = useMemo<CanvasContextValue>(
    () => ({
      model,
      readOnly,
      selected: readOnly ? undefined : selected,
      select: (at) => {
        if (!readOnly) onSelect?.(at);
      },
      run,
      clip,
      cut: (step) => setCut({ path: step.path, model }),
      dragging,
      problems,
      askDelete: setDeleting,
      renderFlow: (branch, steps, place) => <Flow branch={branch} steps={steps} place={place} readOnly={readOnly} />,
    }),
    [model, readOnly, selected, onSelect, run, clip, problems],
  );

  const confirmDelete = () => {
    const step = deleting;
    setDeleting(undefined);
    if (!step) return;
    const removed = run((doc) => {
      removeStep(doc, step.path);
      return true;
    });
    if (removed) onSelect?.('trigger');
  };

  return (
    <CanvasContext.Provider value={context}>
      <div className={styles.root}>
        <div role="group" aria-label={t('studio.canvas.toolbar')} className={styles.toolbar}>
          <Button appearance="subtle" icon={<ZoomOutRegular />} aria-label={t('studio.canvas.zoomOut')} title={t('studio.canvas.zoomOut')} onClick={view.zoomOut} />
          <Text aria-live="polite">{t('studio.canvas.zoom', { percent: Math.round(view.zoom * 100) })}</Text>
          <Button appearance="subtle" icon={<ZoomInRegular />} aria-label={t('studio.canvas.zoomIn')} title={t('studio.canvas.zoomIn')} onClick={view.zoomIn} />
          <Button appearance="subtle" icon={<ZoomFitRegular />} onClick={view.fit}>
            {t('studio.canvas.fit')}
          </Button>
        </div>
        {error && (
          <MessageBar intent="error">
            <MessageBarBody>{error}</MessageBarBody>
            <MessageBarActions containerAction={<Button appearance="transparent" icon={<DismissRegular />} aria-label={t('studio.editor.close')} onClick={() => setError(undefined)} />} />
          </MessageBar>
        )}
        <div ref={view.viewportRef} className={styles.viewport} role="region" aria-label={t('studio.canvas.label')}>
          <div className={styles.sizer} style={measured ? { width: size.width * view.zoom, height: size.height * view.zoom } : undefined}>
            <div ref={stageRef} className={mergeClasses(styles.stage, measured && styles.measured)} style={{ transform: `scale(${view.zoom})` }}>
              <TriggerCard model={model} />
              <Flow branch={ROOT} steps={model.steps} place={TOP} readOnly={readOnly} />
            </div>
          </div>
        </div>
      </div>
      <ConfirmDialog
        open={deleting !== undefined}
        title={t('studio.canvas.delete.title')}
        body={t('studio.canvas.delete.body', { summary: deleting ? phraseText(deleting.summary) : '' })}
        confirmLabel={t('studio.canvas.delete.confirm')}
        cancelLabel={t('common.cancel')}
        onCancel={() => setDeleting(undefined)}
        onConfirm={confirmDelete}
      />
    </CanvasContext.Provider>
  );
}
