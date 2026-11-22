/**
 * The header properties of the process, shown when the trigger card is selected (spec §4.6): name, description, mode,
 * scope, runAs, the triggers with the update columns and the stage, onDemand, childProcess, autoDelete, logFailures; the
 * action's message and arguments. A setting at its default is left out of the document, as the 2.21 writer does.
 */
import { Button, Checkbox, Field, makeStyles, Text, tokens } from '@fluentui/react-components';
import { useState, type ReactElement } from 'react';
import type { Document } from 'yaml';
import { t } from '../../shell/i18n/t';
import { DeleteIcon, NewIcon } from '../../shell/icons';
import { blockStyle, setAt } from '../model/edit';
import type { StepNode, WorkflowModel } from '../model/types';
import { valueAt } from '../model/values';
import { ARGUMENT_TYPES, WORKFLOW_SCOPES } from './stepSchema';
import type { DocWriter } from './StepForm';
import { ChoicePicker, ColumnPicker, TextInput } from './ValueEditor';
import type { ValueContext } from './valueModel';

const useStyles = makeStyles({
  form: { display: 'flex', flexDirection: 'column', rowGap: tokens.spacingVerticalM, minWidth: 0 },
  section: { display: 'flex', flexDirection: 'column', rowGap: tokens.spacingVerticalXS },
  nested: {
    display: 'flex',
    flexDirection: 'column',
    rowGap: tokens.spacingVerticalXS,
    paddingLeft: tokens.spacingHorizontalL,
  },
  chips: { display: 'flex', flexWrap: 'wrap', gap: tokens.spacingHorizontalXS },
  argument: {
    display: 'flex',
    flexDirection: 'column',
    rowGap: tokens.spacingVerticalXS,
    padding: tokens.spacingHorizontalS,
    border: `${tokens.strokeWidthThin} solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
  },
  row: { display: 'flex', alignItems: 'flex-end', columnGap: tokens.spacingHorizontalXS },
  grow: { flexGrow: 1, minWidth: 0 },
  hint: { color: tokens.colorNeutralForeground3 },
});

const isMapping = (value: unknown): value is Record<string, unknown> => !!value && typeof value === 'object' && !Array.isArray(value);
const asText = (value: unknown): string => (typeof value === 'string' ? value : value == null ? '' : String(value));

function hasWait(steps: StepNode[]): boolean {
  return steps.some((step) => step.type === 'wait' || step.branches.some((branch) => hasWait(branch.steps)));
}

export interface HeaderFormProps {
  model: WorkflowModel;
  doc: Document;
  writer: DocWriter;
  ctx: ValueContext;
}

export function HeaderForm({ model, doc, writer, ctx }: HeaderFormProps) {
  const styles = useStyles();
  const read = (key: string) => valueAt(doc, [key]);
  const text = (key: string, required?: boolean) => (next: string) => writer.write([key], next === '' && !required ? undefined : next);
  /** A setting written only when it differs from its default (`undefined` deletes the key). */
  const setting = (key: string, value: string, fallback: string) => writer.write([key], value === fallback ? undefined : value);
  const flag = (key: string, fallback: boolean) => (
    <Checkbox
      label={t(`studio.props.header.${key}` as 'studio.props.header.onDemand')}
      checked={read(key) === undefined ? fallback : read(key) === 'true'}
      onChange={(_, d) => setting(key, d.checked ? 'true' : 'false', String(fallback))}
    />
  );

  return (
    <div className={styles.form}>
      <TextInput label={t('studio.props.header.name')} value={asText(read('name'))} onChange={text('name', true)} required />
      <TextInput label={t('studio.props.header.description')} value={asText(read('description'))} onChange={text('description')} />
      <Field label={t('studio.props.header.entity')}>
        <Text>{model.entity ?? t('studio.list.global')}</Text>
      </Field>
      {model.kind === 'workflow' && <WorkflowHeader model={model} doc={doc} writer={writer} ctx={ctx} read={read} setting={setting} flag={flag} />}
      {model.kind === 'businessRule' && (
        <Field label={t('studio.props.header.scope')}>
          <Text>{t('studio.props.scope.entity')}</Text>
        </Field>
      )}
      {model.kind === 'action' && (
        <>
          <TextInput label={t('studio.props.header.message')} value={asText(read('message'))} onChange={text('message', true)} required hint={t('studio.props.messageHint')} />
          <ArgumentsEditor value={read('arguments')} writer={writer} />
        </>
      )}
    </div>
  );
}

function WorkflowHeader({
  model,
  doc,
  writer,
  ctx,
  read,
  setting,
  flag,
}: HeaderFormProps & { read(key: string): unknown; setting(key: string, value: string, fallback: string): void; flag(key: string, fallback: boolean): ReactElement }) {
  const styles = useStyles();
  const realtime = model.mode === 'realtime';
  const waits = hasWait(model.steps);

  /** The other mode: the settings it does not take are removed with the switch, so that the document stays valid. */
  const switchMode = (mode: string) =>
    writer.edit((d) => {
      setAt(d, ['mode'], mode);
      if (mode === 'background') {
        setAt(d, ['runAs'], undefined);
        setAt(d, ['logFailures'], undefined);
        const trigger = valueAt(d, ['trigger']);
        if (isMapping(trigger)) {
          if (isMapping(trigger.create)) setAt(d, ['trigger', 'create'], 'true');
          if (isMapping(trigger.update) && 'stage' in trigger.update) setAt(d, ['trigger', 'update', 'stage'], undefined);
          if (isMapping(trigger.delete)) setAt(d, ['trigger', 'delete'], 'true');
        }
      } else {
        setAt(d, ['autoDelete'], undefined);
      }
    });

  return (
    <>
      <ChoicePicker
        label={t('studio.props.header.mode')}
        value={read('mode')}
        onChange={(mode) => (mode === 'realtime' && waits ? undefined : switchMode(mode))}
        required
        options={[
          ['realtime', t('studio.mode.realtime')],
          ['background', t('studio.mode.background')],
        ]}
      />
      {/* Real-time is refused while a wait is in the flow: the reader would refuse the document. */}
      <Text size={200} className={styles.hint}>
        {waits ? t('studio.props.mode.waitHint') : t('studio.props.mode.hint')}
      </Text>
      <ChoicePicker
        label={t('studio.props.header.scope')}
        value={read('scope') ?? 'user'}
        onChange={(scope) => setting('scope', scope, 'user')}
        options={WORKFLOW_SCOPES.map((scope) => [scope, t(`studio.props.scope.${scope}` as 'studio.props.scope.user')])}
      />
      {realtime && (
        <ChoicePicker
          label={t('studio.props.header.runAs')}
          value={read('runAs') ?? 'caller'}
          onChange={(runAs) => setting('runAs', runAs, 'caller')}
          options={[
            ['caller', t('studio.props.runAs.caller')],
            ['owner', t('studio.props.runAs.owner')],
          ]}
        />
      )}
      <TriggerEditor model={model} doc={doc} writer={writer} ctx={ctx} realtime={realtime} />
      <div className={styles.section}>
        {flag('onDemand', false)}
        {flag('childProcess', false)}
        {realtime ? flag('logFailures', true) : flag('autoDelete', false)}
      </div>
    </>
  );
}

/** The trigger events. An update trigger needs its columns: it is written with the first column picked. */
function TriggerEditor({ model, doc, writer, ctx, realtime }: HeaderFormProps & { realtime: boolean }) {
  const styles = useStyles();
  const trigger = valueAt(doc, ['trigger']);
  const events = isMapping(trigger) ? trigger : {};
  const update = isMapping(events.update) ? events.update : undefined;
  const columns = Array.isArray(update?.columns) ? update.columns.map(asText) : [];
  const [pendingUpdate, setPendingUpdate] = useState(false);
  const [typed, setTyped] = useState('');
  const showUpdate = update !== undefined || pendingUpdate;

  /** Sets `trigger.<event>`; a new trigger is written as a block mapping, an empty one is removed. */
  const setEvent = (event: string, value: unknown) =>
    writer.edit((d) => {
      const existed = valueAt(d, ['trigger']) !== undefined;
      setAt(d, ['trigger', event], value);
      const after = valueAt(d, ['trigger']);
      if (isMapping(after) && Object.keys(after).length === 0) setAt(d, ['trigger'], undefined);
      else if (!existed) blockStyle(d, ['trigger']);
    });
  const stageOf = (event: unknown) => (isMapping(event) && event.stage === 'pre' ? 'pre' : 'post');
  const addColumn = (column: string) => {
    if (!column || columns.includes(column)) return;
    if (update) writer.write(['trigger', 'update', 'columns'], [...columns, column]);
    else setEvent('update', { columns: [column] });
    setPendingUpdate(false);
    setTyped('');
  };
  const columnMeta = ctx.meta.entity(model.entity);

  return (
    <fieldset className={styles.section} aria-label={t('studio.props.header.trigger')}>
      <Text weight="semibold">{t('studio.props.header.trigger')}</Text>
      <Checkbox label={t('studio.canvas.trigger.create')} checked={events.create !== undefined} onChange={(_, d) => setEvent('create', d.checked ? 'true' : undefined)} />
      {events.create !== undefined && realtime && (
        <div className={styles.nested}>
          <ChoicePicker
            label={t('studio.props.trigger.createStage')}
            value={stageOf(events.create)}
            onChange={(stage) => setEvent('create', stage === 'pre' ? { stage: 'pre' } : 'true')}
            options={[
              ['pre', t('studio.props.stage.pre')],
              ['post', t('studio.props.stage.post')],
            ]}
          />
        </div>
      )}
      <Checkbox
        label={t('studio.canvas.trigger.updateAny')}
        checked={showUpdate}
        onChange={(_, d) => {
          if (d.checked) setPendingUpdate(true);
          else {
            setPendingUpdate(false);
            if (update) setEvent('update', undefined);
          }
        }}
      />
      {showUpdate && (
        <div className={styles.nested}>
          <Text>{t('studio.props.trigger.columns')}</Text>
          <div className={styles.chips} role="list" aria-label={t('studio.props.trigger.columns')}>
            {columns.map((column) => (
              <span role="listitem" key={column}>
                <Button
                  size="small"
                  icon={<DeleteIcon />}
                  iconPosition="after"
                  disabled={columns.length === 1}
                  aria-label={t('studio.props.trigger.removeColumn', { column })}
                  onClick={() => writer.write(['trigger', 'update', 'columns'], columns.filter((c) => c !== column))}
                >
                  {column}
                </Button>
              </span>
            ))}
          </div>
          {columns.length === 0 && <Text className={styles.hint}>{t('studio.props.trigger.needsColumn')}</Text>}
          {columnMeta ? (
            <ColumnPicker label={t('studio.props.trigger.addColumn')} value="" onChange={addColumn} ctx={ctx} table={model.entity} filter={(a) => !columns.includes(a.logicalName)} />
          ) : (
            <div className={styles.row}>
              <div className={styles.grow}>
                <TextInput label={t('studio.props.trigger.addColumn')} value={typed} onChange={setTyped} />
              </div>
              <Button icon={<NewIcon />} disabled={!typed} onClick={() => addColumn(typed)} aria-label={t('studio.props.trigger.addColumnButton')} />
            </div>
          )}
          {update && realtime && (
            <ChoicePicker
              label={t('studio.props.trigger.updateStage')}
              value={stageOf(update)}
              onChange={(stage) => writer.write(['trigger', 'update', 'stage'], stage === 'pre' ? 'pre' : undefined)}
              options={[
                ['pre', t('studio.props.stage.pre')],
                ['post', t('studio.props.stage.post')],
              ]}
            />
          )}
        </div>
      )}
      <Checkbox label={t('studio.canvas.trigger.delete')} checked={events.delete !== undefined} onChange={(_, d) => setEvent('delete', d.checked ? 'true' : undefined)} />
    </fieldset>
  );
}

/** The action's arguments: one box per argument; the list is a block list of one-line mappings, as the writer writes it. */
function ArgumentsEditor({ value, writer }: { value: unknown; writer: DocWriter }) {
  const styles = useStyles();
  const items = Array.isArray(value) ? value.map((item) => (isMapping(item) ? item : {})) : [];
  const at = (i: number, key: string, next: unknown) => writer.write(['arguments', i, key], next);
  const add = () => {
    const argument = { name: `Argument${items.length + 1}`, direction: 'input', type: 'string' };
    if (items.length === 0) writer.write(['arguments'], [argument], ['arguments']);
    else writer.write(['arguments', items.length], argument);
  };
  const remove = (i: number) => writer.write(items.length === 1 ? ['arguments'] : ['arguments', i], undefined);
  return (
    <div className={styles.section} role="group" aria-label={t('studio.props.header.arguments')}>
      <Text weight="semibold">{t('studio.props.header.arguments')}</Text>
      {items.map((item, i) => (
        <div key={i} className={styles.argument} role="group" aria-label={t('studio.props.argument.n', { n: i + 1 })}>
          <TextInput label={t('studio.props.argument.name')} value={asText(item.name)} onChange={(next) => at(i, 'name', next)} required />
          <ChoicePicker
            label={t('studio.props.argument.direction')}
            value={item.direction}
            onChange={(next) => at(i, 'direction', next)}
            required
            options={[
              ['input', t('studio.props.argument.input')],
              ['output', t('studio.props.argument.output')],
            ]}
          />
          <ChoicePicker label={t('studio.props.argument.type')} value={item.type} onChange={(next) => at(i, 'type', next)} required options={ARGUMENT_TYPES.map((type) => [type, type])} />
          {(item.type === 'entityReference' || item.entity !== undefined) && (
            <TextInput label={t('studio.props.argument.entity')} value={asText(item.entity)} onChange={(next) => at(i, 'entity', next === '' ? undefined : next)} />
          )}
          <Checkbox label={t('studio.props.argument.required')} checked={item.required === 'true'} onChange={(_, d) => at(i, 'required', d.checked ? 'true' : undefined)} />
          <TextInput label={t('studio.props.argument.description')} value={asText(item.description)} onChange={(next) => at(i, 'description', next === '' ? undefined : next)} />
          <div>
            <Button size="small" icon={<DeleteIcon />} onClick={() => remove(i)}>
              {t('studio.props.argument.remove')}
            </Button>
          </div>
        </div>
      ))}
      <div>
        <Button size="small" icon={<NewIcon />} onClick={add}>
          {t('studio.props.argument.add')}
        </Button>
      </div>
    </div>
  );
}
