/**
 * The properties panel of the canvas (spec §4.3, §4.6): the selected card's fields, or the process header for the trigger
 * card. It reads the values from the document and writes each change at its YAML path through the editor's `mutate`, so
 * the code view, the canvas and the live problems follow. The selected step is looked up by its path: after a move or a
 * delete a path may name no step any more, and the panel then asks for a card.
 */
import { makeStyles, MessageBar, MessageBarBody, Text, tokens } from '@fluentui/react-components';
import { useCallback, useMemo, useState } from 'react';
import type { Document } from 'yaml';
import type { XrmApi } from '../../shell/api/client';
import { t } from '../../shell/i18n/t';
import type { EditorDocument } from '../editor/useEditorDocument';
import { blockStyle, setAt, StudioEditError } from '../model/edit';
import { phraseText } from '../model/phrase';
import type { StepPath, WorkflowModel } from '../model/types';
import { valueAt } from '../model/values';
import { HeaderForm } from './HeaderForm';
import { earlierSteps, findStep } from './stepSchema';
import { StepForm, type DocWriter } from './StepForm';
import { useStudioMetadata } from './useStudioMetadata';
import type { ValueContext } from './valueModel';

const useStyles = makeStyles({
  panel: {
    display: 'flex',
    flexDirection: 'column',
    rowGap: tokens.spacingVerticalM,
    padding: tokens.spacingHorizontalM,
    backgroundColor: tokens.colorNeutralBackground1,
    border: `${tokens.strokeWidthThin} solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
    flexGrow: 1,
    minWidth: 0,
    overflowY: 'auto',
  },
  hint: { color: tokens.colorNeutralForeground3 },
});

export interface PropertiesPanelProps {
  /** The signed-in API, for the table metadata and the workflow names; without it every picker is a text field. */
  api?: XrmApi;
  model: WorkflowModel;
  /** The parsed document the values are read from (`useEditorDocument().doc`). */
  doc: Document;
  selected?: StepPath | 'trigger';
  /** Applies a change to the document (`useEditorDocument().mutate`). */
  mutate: EditorDocument['mutate'];
}

const isMapping = (value: unknown): value is Record<string, unknown> => !!value && typeof value === 'object' && !Array.isArray(value);

export function PropertiesPanel({ api, model, doc, selected, mutate }: PropertiesPanelProps) {
  const styles = useStyles();
  const meta = useStudioMetadata(api);
  const [error, setError] = useState<string>();
  const step = selected && selected !== 'trigger' ? findStep(model, selected) : undefined;

  const writer = useMemo<DocWriter>(() => {
    const run = (fn: (d: Document) => void) => {
      try {
        mutate(fn);
        setError(undefined);
      } catch (e) {
        if (!(e instanceof StudioEditError)) throw e;
        setError(t('studio.canvas.editFailed', { detail: e.message }));
      }
    };
    return {
      edit: run,
      write: (path, value, block) =>
        run((d) => {
          setAt(d, path, value);
          if (block) blockStyle(d, block);
        }),
    };
  }, [mutate]);

  const args = model.kind === 'action' ? valueAt(doc, ['arguments']) : undefined;
  const argumentList = Array.isArray(args) ? args.filter(isMapping) : [];
  const ctx: ValueContext = {
    kind: model.kind,
    entity: model.entity,
    meta,
    earlier: step ? earlierSteps(model, step.path) : [],
    inputArgs: argumentList.filter((a) => a.direction === 'input').map((a) => String(a.name ?? '')),
    outputArgs: argumentList.filter((a) => a.direction === 'output').map((a) => ({ name: String(a.name ?? ''), type: String(a.type ?? '') })),
  };

  const title = selected === 'trigger' ? t('studio.props.header.title') : step ? t(`studio.step.${step.type}`) : t('studio.props.title');
  const dismiss = useCallback(() => setError(undefined), []);

  return (
    <aside className={styles.panel} aria-label={t('studio.props.title')}>
      <div>
        <Text as="h2" size={400} weight="semibold" block>
          {title}
        </Text>
        {step && (
          <Text size={200} className={styles.hint}>
            {phraseText(step.summary)}
          </Text>
        )}
      </div>
      {error && (
        <MessageBar intent="error" onClick={dismiss}>
          <MessageBarBody>{error}</MessageBarBody>
        </MessageBar>
      )}
      {selected === 'trigger' ? (
        <HeaderForm model={model} doc={doc} writer={writer} ctx={ctx} />
      ) : step ? (
        // Keyed by path and type: another card gets fresh local state (a pending row, a typed name).
        <StepForm key={`${step.path.join('.')}:${step.type}`} step={step} doc={doc} writer={writer} ctx={ctx} api={api} />
      ) : (
        <Text className={styles.hint}>{t('studio.props.none')}</Text>
      )}
    </aside>
  );
}
