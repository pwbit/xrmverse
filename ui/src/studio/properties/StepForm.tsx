/**
 * The form of one step (spec §4.6), generated from `STEP_SCHEMA`: required fields marked, enums as lists, tables, columns
 * and choices from the metadata, values through the value editor and conditions through the condition builder. Every change
 * is written at its YAML path (`setAt`); the branches are the canvas's.
 */
import { Button, Field, Input, makeStyles, Text, tokens } from '@fluentui/react-components';
import { useEffect, useId, useState, type ReactNode } from 'react';
import type { Document } from 'yaml';
import type { AttributeMeta } from '../../admin/tables/metadata';
import type { XrmApi } from '../../shell/api/client';
import { t } from '../../shell/i18n/t';
import { DeleteIcon, NewIcon } from '../../shell/icons';
import { listWorkflows } from '../api/studioApi';
import { setAt } from '../model/edit';
import type { StepNode, StepPath } from '../model/types';
import { valueAt } from '../model/values';
import { ConditionBuilder } from './ConditionBuilder';
import { STEP_SCHEMA, type FieldSpec } from './stepSchema';
import { ChoicePicker, ColumnPicker, RecordEditor, TablePicker, TextInput, ValueEditor } from './ValueEditor';
import { conditionForYaml, literalTypeOfArgument, tableOfRecord, type LiteralType, type ValueContext } from './valueModel';

/** How the forms change the document: `write` sets one value (`setAt`), `edit` runs several changes as one. */
export interface DocWriter {
  /** Sets the value at `path` (`undefined` deletes it); `block` then gives the collection at that path the writer's style. */
  write(path: (string | number)[], value: unknown, block?: (string | number)[]): void;
  edit(fn: (doc: Document) => void): void;
}

const useStyles = makeStyles({
  form: { display: 'flex', flexDirection: 'column', rowGap: tokens.spacingVerticalM, minWidth: 0 },
  set: { display: 'flex', flexDirection: 'column', rowGap: tokens.spacingVerticalS },
  row: { display: 'flex', alignItems: 'flex-start', columnGap: tokens.spacingHorizontalXS },
  grow: { flexGrow: 1, minWidth: 0 },
  hint: { color: tokens.colorNeutralForeground3 },
  optional: { display: 'flex', alignItems: 'center', columnGap: tokens.spacingHorizontalS },
});

const isMapping = (value: unknown): value is Record<string, unknown> => !!value && typeof value === 'object' && !Array.isArray(value);
const asText = (value: unknown): string => (typeof value === 'string' ? value : value == null ? '' : String(value));

/** The label of a key of a step: `studio.props.key.<key>`. */
const keyLabel = (key: string) => t(`studio.props.key.${key}` as 'studio.props.key.entity');

export interface StepFormProps {
  step: StepNode;
  doc: Document;
  writer: DocWriter;
  ctx: ValueContext;
  api?: XrmApi;
}

export function StepForm({ step, doc, writer, ctx, api }: StepFormProps) {
  const styles = useStyles();
  const body: StepPath = [...step.path, step.type];
  const read = (key: string) => valueAt(doc, [...body, key]);
  const fields = STEP_SCHEMA[step.type].filter((spec) => spec.kind !== 'branch');
  return (
    <div className={styles.form}>
      {fields.map((spec) => (
        <StepField key={spec.key} spec={spec} step={step} value={read(spec.key)} read={read} body={body} writer={writer} ctx={ctx} api={api} />
      ))}
      {step.type === 'if' && <Text className={styles.hint}>{t('studio.props.branchesOnCanvas')}</Text>}
    </div>
  );
}

interface FieldProps {
  spec: FieldSpec;
  step: StepNode;
  value: unknown;
  read(key: string): unknown;
  body: StepPath;
  writer: DocWriter;
  ctx: ValueContext;
  api?: XrmApi;
}

function StepField({ spec, step, value, read, body, writer, ctx, api }: FieldProps) {
  const styles = useStyles();
  const { key, kind, required } = spec;
  const label = keyLabel(key);
  const path = [...body, key];
  const set = (next: unknown) => writer.write(path, next);
  /** An optional text that is emptied is left out. */
  const setText = (next: string) => set(next === '' && !required ? undefined : next);
  const recordTable = tableOfRecord(read('record') ?? (step.type === 'startChild' ? 'primary' : undefined), ctx);
  const recordColumns = ctx.meta.entity(recordTable)?.attributes;

  switch (kind) {
    case 'text':
      return <TextInput label={label} value={asText(value)} onChange={setText} required={required} />;
    case 'message':
      return <TextInput label={label} value={asText(value)} onChange={setText} required={required} hint={t('studio.props.messageHint')} />;
    case 'workflow':
      return <WorkflowInput label={label} value={asText(value)} onChange={setText} api={api} />;
    case 'table':
      return <TablePicker label={label} value={value} onChange={setText} ctx={ctx} required={required} />;
    case 'column':
      return <ColumnPicker label={label} value={value} onChange={setText} ctx={ctx} table={ctx.entity} required={required} />;
    case 'bool':
      return (
        <ChoicePicker
          label={label}
          value={value}
          onChange={set}
          required={required}
          options={[
            ['true', t('studio.props.true')],
            ['false', t('studio.props.false')],
          ]}
        />
      );
    case 'record':
      return (
        <RecordEditor
          label={label}
          value={value}
          onChange={set}
          ctx={ctx}
          required={required}
          optional={required ? undefined : t(step.type === 'startChild' ? 'studio.props.record.default' : 'studio.props.record.none')}
        />
      );
    case 'enum':
      return <EnumField spec={spec} label={label} value={value} set={set} step={step} read={read} ctx={ctx} recordColumns={recordColumns} writer={writer} body={body} />;
    case 'set':
      return <SetEditor label={label} path={path} value={value} writer={writer} ctx={ctx} required={required} step={step} table={setTable(step, read, ctx)} />;
    case 'condition':
      return (
        <Optional label={label} present={value !== undefined} required={required} canRemove={removable(step, key, read)} add={() => set({ all: [] })} remove={() => set(undefined)}>
          <ConditionBuilder label={label} value={value} onChange={(next) => set(conditionForYaml(next))} ctx={ctx} required={required} />
        </Optional>
      );
    case 'value': {
      const { column, literal } = destination(step, read, ctx, recordColumns);
      return (
        <Optional label={label} present={value !== undefined} required={required} canRemove={removable(step, key, read)} add={() => set('')} remove={() => set(undefined)}>
          <ValueEditor label={label} value={value} onChange={set} ctx={ctx} column={column} literal={literal} required={required} />
        </Optional>
      );
    }
    case 'branch':
      return <Text className={styles.hint}>{t('studio.props.branchesOnCanvas')}</Text>;
  }
}

/**
 * Whether an optional key may be removed: a wait keeps `until` or `timeout` (it needs one), and its `timeout` while it has
 * an `onTimeout` branch.
 */
function removable(step: StepNode, key: string, read: (key: string) => unknown): string | undefined {
  if (step.type !== 'wait') return undefined;
  if (key === 'until' && read('timeout') === undefined) return t('studio.props.wait.needsOne');
  if (key === 'timeout' && read('until') === undefined) return t('studio.props.wait.needsOne');
  if (key === 'timeout' && read('onTimeout') !== undefined) return t('studio.props.wait.timeoutUsed');
  return undefined;
}

/** An optional key: "Add" while it is not there, its editor and "Remove" once it is. */
function Optional({ label, present, required, canRemove, add, remove, children }: { label: string; present: boolean; required?: boolean; canRemove?: string; add(): void; remove(): void; children: ReactNode }) {
  const styles = useStyles();
  if (required) return <>{children}</>;
  if (!present)
    return (
      <div className={styles.optional}>
        <Button size="small" icon={<NewIcon />} onClick={add}>
          {t('studio.props.addKey', { label })}
        </Button>
      </div>
    );
  return (
    <div className={styles.set}>
      {children}
      <div className={styles.optional}>
        <Button size="small" icon={<DeleteIcon />} disabled={canRemove !== undefined} onClick={remove}>
          {t('studio.props.removeKey', { label })}
        </Button>
        {canRemove && <Text className={styles.hint}>{canRemove}</Text>}
      </div>
    </div>
  );
}

/** The column or literal type that types a step's value. */
function destination(step: StepNode, read: (key: string) => unknown, ctx: ValueContext, recordColumns?: AttributeMeta[]): { column?: AttributeMeta; literal?: LiteralType } {
  switch (step.type) {
    case 'assign':
      return { column: recordColumns?.find((a) => a.logicalName === 'ownerid'), literal: 'reference' };
    case 'wait':
      return { literal: 'datetime' };
    case 'stop':
      return { literal: 'text' };
    case 'setOutput': {
      const argument = ctx.outputArgs.find((a) => a.name === read('argument'));
      return { literal: literalTypeOfArgument(argument?.type) };
    }
    case 'setValue':
    case 'setDefault':
      return { column: ctx.meta.entity(ctx.entity)?.attributes.find((a) => a.logicalName === read('field')) };
    default:
      return {};
  }
}

/** The table whose columns a `set` names: the created table, the updated record's, `email`; none for `inputs`/`outputs`. */
function setTable(step: StepNode, read: (key: string) => unknown, ctx: ValueContext): string | undefined {
  if (step.type === 'create') return asText(read('entity')) || undefined;
  if (step.type === 'update') return tableOfRecord(read('record'), ctx);
  if (step.type === 'sendEmail') return 'email';
  return undefined;
}

function EnumField({ spec, label, value, set, step, read, ctx, recordColumns, writer, body }: { spec: FieldSpec; label: string; value: unknown; set(value: unknown): void; step: StepNode; read(key: string): unknown; ctx: ValueContext; recordColumns?: AttributeMeta[]; writer: DocWriter; body: StepPath }) {
  if (spec.options) return <ChoicePicker label={label} value={value} onChange={set} required={spec.required} options={spec.options.map((o) => [o, t(`studio.props.enum.${o}` as 'studio.props.enum.none')])} />;
  if (step.type === 'setOutput') {
    return <ChoicePicker label={label} value={value} onChange={set} required placeholder={t('studio.props.chooseArgument')} options={ctx.outputArgs.map((a) => [a.name, a.name])} />;
  }
  // setState: the state and status reason by their options on the record's table (a status of the chosen state only).
  const column = recordColumns?.find((a) => a.logicalName === (spec.key === 'state' ? 'statecode' : 'statuscode'));
  if (!column || column.options.length === 0) return <TextInput label={label} type="number" value={asText(value)} onChange={set} required hint={t('studio.props.numberHint')} />;
  const state = read('state');
  const options = column.options.filter((o) => spec.key === 'state' || o.state === undefined || state === undefined || String(o.state) === state);
  return (
    <ChoicePicker
      label={label}
      value={value}
      onChange={(next) => {
        // A new state starts with its default status reason, as in Dataverse.
        const status = spec.key === 'state' ? column.options.find((o) => String(o.value) === next)?.defaultStatus : undefined;
        if (status === undefined) set(next);
        else
          writer.edit((doc) => {
            setAt(doc, [...body, 'state'], next);
            setAt(doc, [...body, 'status'], String(status));
          });
      }}
      required
      placeholder={t('studio.props.chooseOption')}
      options={options.map((o) => [String(o.value), `${o.label} (${o.value})`])}
    />
  );
}

/** A workflow's name or id, with the workflows of the environment as suggestions. */
function WorkflowInput({ label, value, onChange, api }: { label: string; value: string; onChange(value: string): void; api?: XrmApi }) {
  const id = useId();
  const [names, setNames] = useState<string[]>([]);
  useEffect(() => {
    if (!api) return;
    let live = true;
    listWorkflows(api, { kind: 'workflow' }).then(
      (rows) => live && setNames(rows.map((r) => r.name).sort((a, b) => a.localeCompare(b))),
      () => undefined,
    );
    return () => {
      live = false;
    };
  }, [api]);
  return (
    <Field label={label} required hint={t('studio.props.workflowHint')}>
      <Input value={value} list={id} onChange={(_, d) => onChange(d.value)} />
      <datalist id={id}>
        {names.map((name) => (
          <option key={name} value={name} />
        ))}
      </datalist>
    </Field>
  );
}

/**
 * A `set`, `inputs` or `outputs` mapping: one row per name with its value, typed by the column of `table` when there is one;
 * a row is added by picking a column (or typing a name) and removed with its button. `outputs` maps a property to a name.
 */
function SetEditor({ label, path, value, writer, ctx, required, step, table }: { label: string; path: (string | number)[]; value: unknown; writer: DocWriter; ctx: ValueContext; required?: boolean; step: StepNode; table?: string }) {
  const styles = useStyles();
  const entries = isMapping(value) ? Object.entries(value) : [];
  const meta = ctx.meta.entity(table);
  const outputs = path[path.length - 1] === 'outputs';
  const [typed, setTyped] = useState('');
  const columnOf = (name: string) => meta?.attributes.find((a) => a.logicalName === name);
  const add = (name: string) => {
    if (!name || entries.some(([n]) => n === name)) return;
    writer.write([...path, name], outputs ? '' : null, path);
    setTyped('');
  };
  const remove = (name: string) => {
    if (entries.length === 1 && !required) writer.write(path, undefined);
    else writer.write([...path, name], undefined, path);
  };
  const forCreate = step.type === 'create' || step.type === 'sendEmail';
  return (
    <Field label={label} required={required}>
      <div className={styles.set} role="group" aria-label={label}>
        {entries.length === 0 && <Text className={styles.hint}>{t('studio.props.set.empty')}</Text>}
        {entries.map(([name, item]) => {
          const column = columnOf(name);
          const rowLabel = column ? `${column.displayName} (${name})` : name;
          return (
            <div key={name} className={styles.row}>
              <div className={styles.grow}>
                {outputs ? (
                  <TextInput label={rowLabel} value={asText(item)} onChange={(next) => writer.write([...path, name], next)} />
                ) : (
                  <ValueEditor label={rowLabel} value={item} onChange={(next) => writer.write([...path, name], next)} ctx={ctx} column={column} />
                )}
              </div>
              <Button appearance="subtle" icon={<DeleteIcon />} aria-label={t('studio.props.set.remove', { name })} onClick={() => remove(name)} />
            </div>
          );
        })}
        {meta ? (
          <ColumnPicker
            label={t('studio.props.set.add')}
            value=""
            onChange={add}
            ctx={ctx}
            table={table}
            filter={(a) => !entries.some(([n]) => n === a.logicalName) && !a.isPrimaryId && (forCreate ? a.validForCreate : a.validForUpdate) && a.kind !== 'file' && a.kind !== 'image'}
          />
        ) : (
          <div className={styles.row}>
            <div className={styles.grow}>
              <TextInput label={t(outputs || !table ? 'studio.props.set.addName' : 'studio.props.set.add')} value={typed} onChange={setTyped} />
            </div>
            <Button icon={<NewIcon />} disabled={!typed} onClick={() => add(typed)} aria-label={t('studio.props.set.addButton', { label })} />
          </div>
        )}
      </div>
    </Field>
  );
}
