/**
 * The value editor of the properties panel (spec §4.6): a literal typed by its destination, or a dynamic value (`field` of
 * the primary record, `related` via a lookup, `created` by an earlier step, `output of` an earlier action step, `arg`, `now`,
 * a `lookup` literal picked from the records, a record reference) or a value operator over operands. Only the earlier steps
 * on the path to the card are offered (`ValueContext.earlier`).
 */
import { Button, Field, Input, makeStyles, Select, Text, tokens } from '@fluentui/react-components';
import { EntityPicker } from '../../admin/common/EntityPicker';
import type { AttributeMeta, EntityMeta } from '../../admin/tables/metadata';
import { t } from '../../shell/i18n/t';
import { DeleteIcon, NewIcon } from '../../shell/icons';
import { VALUE_OPERATORS, type EarlierStep } from './stepSchema';
import {
  isValueOperator,
  literalTypeOf,
  operandsOf,
  sourceOf,
  startValue,
  tableOfField,
  type LiteralType,
  type Source,
  type ValueContext,
} from './valueModel';

const useStyles = makeStyles({
  group: { display: 'flex', flexDirection: 'column', rowGap: tokens.spacingVerticalXS, minWidth: 0 },
  nested: {
    paddingLeft: tokens.spacingHorizontalM,
    borderLeft: `${tokens.strokeWidthThick} solid ${tokens.colorNeutralStroke2}`,
    display: 'flex',
    flexDirection: 'column',
    rowGap: tokens.spacingVerticalXS,
  },
  row: { display: 'flex', alignItems: 'flex-start', columnGap: tokens.spacingHorizontalXS },
  grow: { flexGrow: 1, minWidth: 0 },
  hint: { color: tokens.colorNeutralForeground3 },
});

const asText = (value: unknown): string => (typeof value === 'string' ? value : value == null ? '' : String(value));
const isMapping = (value: unknown): value is Record<string, unknown> => !!value && typeof value === 'object' && !Array.isArray(value);

/** The columns a picker offers: readable, not a companion of another one, not hidden; by display name. */
function pickable(meta: EntityMeta, filter?: (a: AttributeMeta) => boolean): AttributeMeta[] {
  return meta.attributes
    .filter((a) => !a.attributeOf && !a.hidden && a.kind !== 'unsupported' && (filter ? filter(a) : true))
    .sort((a, b) => a.displayName.localeCompare(b.displayName));
}

// -------------------------------------------------------------------- pickers

export interface PickerProps {
  label: string;
  value: unknown;
  onChange(value: string): void;
  ctx: ValueContext;
  required?: boolean;
}

/** A column of `table`: a list from the metadata, or the logical name typed when the table's metadata is not there. */
export function ColumnPicker({ label, value, onChange, ctx, table, filter, required }: PickerProps & { table: string | undefined; filter?: (a: AttributeMeta) => boolean }) {
  const meta = ctx.meta.entity(table);
  const text = asText(value);
  if (!meta) return <TextInput label={label} value={text} onChange={onChange} required={required} hint={t('studio.props.logicalName')} />;
  const columns = pickable(meta, filter);
  return (
    <Field label={label} required={required}>
      <Select value={text} onChange={(_, d) => onChange(d.value)}>
        <option value="">{t('studio.props.chooseColumn')}</option>
        {text && !columns.some((c) => c.logicalName === text) && <option value={text}>{text}</option>}
        {columns.map((c) => (
          <option key={c.logicalName} value={c.logicalName}>
            {`${c.displayName} (${c.logicalName})`}
          </option>
        ))}
      </Select>
    </Field>
  );
}

/** A table: a list of every table, or the logical name typed while it is not there. */
export function TablePicker({ label, value, onChange, ctx, required }: PickerProps) {
  const tables = ctx.meta.tables();
  const text = asText(value);
  if (!tables) return <TextInput label={label} value={text} onChange={onChange} required={required} hint={t('studio.props.logicalName')} />;
  return (
    <Field label={label} required={required}>
      <Select value={text} onChange={(_, d) => onChange(d.value)}>
        <option value="">{t('studio.props.chooseTable')}</option>
        {text && !tables.some((e) => e.logicalName === text) && <option value={text}>{text}</option>}
        {tables.map((e) => (
          <option key={e.logicalName} value={e.logicalName}>
            {`${e.displayName} (${e.logicalName})`}
          </option>
        ))}
      </Select>
    </Field>
  );
}

/** One of `options` (each `[value, label]`); a current value that is not among them is kept as an option. */
export function ChoicePicker({ label, value, onChange, options, required, placeholder }: Omit<PickerProps, 'ctx'> & { options: [string, string][]; placeholder?: string }) {
  const text = asText(value);
  return (
    <Field label={label} required={required}>
      <Select value={text} onChange={(_, d) => onChange(d.value)}>
        {(placeholder !== undefined || text === '') && <option value="">{placeholder ?? ''}</option>}
        {text && !options.some(([v]) => v === text) && <option value={text}>{text}</option>}
        {options.map(([v, l]) => (
          <option key={v} value={v}>
            {l}
          </option>
        ))}
      </Select>
    </Field>
  );
}

export function TextInput({ label, value, onChange, required, hint, placeholder, type }: { label: string; value: string; onChange(value: string): void; required?: boolean; hint?: string; placeholder?: string; type?: 'text' | 'number' }) {
  return (
    <Field label={label} required={required} hint={hint}>
      <Input type={type} value={value} placeholder={placeholder} onChange={(_, d) => onChange(d.value)} />
    </Field>
  );
}

/** An earlier step's id. */
function StepPicker({ label, value, onChange, steps }: { label: string; value: unknown; onChange(value: string): void; steps: EarlierStep[] }) {
  return (
    <ChoicePicker
      label={label}
      value={value}
      onChange={onChange}
      required
      placeholder={t('studio.props.chooseStep')}
      options={steps.map((s) => [s.id, `${s.id} (${t(`studio.step.${s.type}`)})`])}
    />
  );
}

const createdSteps = (ctx: ValueContext) => ctx.earlier.filter((s) => s.type === 'create' || s.type === 'sendEmail');
const actionSteps = (ctx: ValueContext) => ctx.earlier.filter((s) => s.type === 'performAction' || s.type === 'customActivity');
const isLookup = (a: AttributeMeta) => a.kind === 'lookup';

/**
 * A record: the primary record, the record a lookup of the primary record points to, or a record an earlier step created.
 * `optional` adds "none" (the key is left out: the step's default).
 */
export function RecordEditor({ label, value, onChange, ctx, optional, required }: { label: string; value: unknown; onChange(value: unknown): void; ctx: ValueContext; optional?: string; required?: boolean }) {
  const styles = useStyles();
  const kind = value === undefined ? 'none' : value === 'primary' ? 'primary' : isMapping(value) && 'related' in value ? 'related' : isMapping(value) && 'created' in value ? 'created' : 'other';
  const created = createdSteps(ctx);
  const choose = (next: string) => {
    if (next === 'none') onChange(undefined);
    else if (next === 'primary') onChange('primary');
    else if (next === 'related') onChange({ related: '' });
    else if (next === 'created') onChange({ created: created[0]?.id ?? '' });
  };
  const options: [string, string][] = [
    ...(optional ? ([['none', optional]] as [string, string][]) : []),
    ['primary', t('studio.props.record.primary')],
    ['related', t('studio.props.record.related')],
    ...(created.length > 0 || kind === 'created' ? ([['created', t('studio.props.record.created')]] as [string, string][]) : []),
    ...(kind === 'other' ? ([['other', t('studio.props.other')]] as [string, string][]) : []),
  ];
  return (
    <div className={styles.group} role="group" aria-label={label}>
      <ChoicePicker label={label} value={kind} onChange={choose} options={options} required={required} />
      <div className={styles.nested}>
        {kind === 'related' && isMapping(value) && (
          <ColumnPicker
            label={t('studio.props.lookup')}
            value={value.related}
            onChange={(related) => onChange({ related })}
            ctx={ctx}
            table={ctx.entity}
            filter={isLookup}
            required
          />
        )}
        {kind === 'created' && isMapping(value) && <StepPicker label={t('studio.props.step')} value={value.created} onChange={(id) => onChange({ created: id })} steps={created} />}
        {kind === 'other' && <Text className={styles.hint}>{t('studio.props.otherHint')}</Text>}
      </div>
    </div>
  );
}

// -------------------------------------------------------------------- literals

/** A literal typed by its destination: a number field for a number column, the column's choices, true / false. */
export function LiteralInput({ label, value, onChange, type, column }: { label: string; value: string; onChange(value: string): void; type: LiteralType; column?: AttributeMeta }) {
  if ((type === 'whole' || type === 'number') && (value === '' || /^[-+]?\d*(\.\d*)?([eE][-+]?\d+)?$/.test(value)))
    return <TextInput label={label} type="number" value={value} onChange={onChange} />;
  if (type === 'bool')
    return (
      <ChoicePicker
        label={label}
        value={value}
        onChange={onChange}
        options={[
          ['true', column?.options.find((o) => o.value === 1)?.label ?? t('studio.props.true')],
          ['false', column?.options.find((o) => o.value === 0)?.label ?? t('studio.props.false')],
        ]}
      />
    );
  if (type === 'choice' && column && column.options.length > 0)
    return <ChoicePicker label={label} value={value} onChange={onChange} placeholder={t('studio.props.chooseOption')} options={column.options.map((o) => [String(o.value), `${o.label} (${o.value})`])} />;
  if (type === 'datetime') return <TextInput label={label} value={value} onChange={onChange} placeholder="2026-01-01T00:00:00Z" hint={t('studio.props.dateHint')} />;
  return <TextInput label={label} value={value} onChange={onChange} />;
}

/** A lookup literal `{ lookup, id, name }`: the table, then a record picked from it (or its id typed). */
function LookupLiteral({ value, onChange, ctx, column }: { value: Record<string, unknown>; onChange(value: unknown): void; ctx: ValueContext; column?: AttributeMeta }) {
  const table = asText(value.lookup);
  const meta = ctx.meta.entity(table || undefined);
  const write = (next: Record<string, unknown>) => {
    const out: Record<string, unknown> = { lookup: next.lookup, id: next.id };
    if (next.name !== undefined && next.name !== '') out.name = next.name;
    onChange(out);
  };
  return (
    <>
      {column && column.targets.length > 0 ? (
        <ChoicePicker label={t('studio.props.table')} value={table} onChange={(lookup) => write({ lookup, id: '' })} options={column.targets.map((target) => [target, target])} required />
      ) : (
        <TablePicker label={t('studio.props.table')} value={table} onChange={(lookup) => write({ lookup, id: '' })} ctx={ctx} required />
      )}
      {meta?.entitySetName && meta.primaryNameAttribute && (
        <EntityPicker
          layout="list"
          entitySet={meta.entitySetName}
          entityName={table}
          idAttribute={meta.primaryIdAttribute}
          nameAttribute={meta.primaryNameAttribute}
          label={t('studio.props.lookup.search', { table: meta.displayName })}
          selected={typeof value.id === 'string' && value.id ? { id: value.id, name: asText(value.name) || value.id } : undefined}
          onSelect={(picked) => write({ lookup: table, id: picked.id, name: picked.name })}
        />
      )}
      <TextInput label={t('studio.props.lookup.id')} value={asText(value.id)} onChange={(id) => write({ lookup: table, id, name: value.name })} required />
    </>
  );
}

// -------------------------------------------------------------------- the editor

export interface ValueEditorProps {
  label: string;
  /** The value as `valueAt` reads it: `null` is the empty value, a string a literal, a mapping a dynamic value. */
  value: unknown;
  onChange(value: unknown): void;
  ctx: ValueContext;
  /** The destination column: it types a literal and offers its choices; a lookup takes a record, not text. */
  column?: AttributeMeta;
  /** The literal type without a column (an argument's type, a timeout's date). */
  literal?: LiteralType;
  /** The left operand of a comparison: a dynamic value only (no literal, empty value or concat). */
  left?: boolean;
  required?: boolean;
}

/** The sources the editor offers for this place; the current one is always among them. */
function sourcesFor(props: ValueEditorProps, current: Source, type: LiteralType): Source[] {
  const { ctx, left } = props;
  const sources: Source[] = [];
  if (!left && type !== 'reference') sources.push('literal');
  if (!left) sources.push('empty');
  sources.push('field', 'related');
  if (createdSteps(ctx).length > 0) sources.push('created');
  if (actionSteps(ctx).length > 0) sources.push('output');
  if (ctx.kind === 'action') sources.push('arg');
  sources.push('now');
  if (!left) sources.push('record', 'lookup');
  for (const op of VALUE_OPERATORS) if (!(left && op === 'concat')) sources.push(op);
  if (!sources.includes(current)) sources.push(current);
  return sources;
}

export function ValueEditor(props: ValueEditorProps) {
  const { label, value, onChange, ctx, column, required } = props;
  const styles = useStyles();
  const type = props.literal ?? literalTypeOf(column);
  const source = sourceOf(value);
  const map = isMapping(value) ? value : {};
  const sources = sourcesFor(props, source, type);
  const choose = (next: Source) => onChange(startValue(next, value, ctx, column?.targets[0]));

  return (
    <div className={styles.group} role="group" aria-label={label}>
      <Field label={label} required={required}>
        <Select value={source} onChange={(_, d) => choose(d.value as Source)}>
          {sources.map((s) => (
            <option key={s} value={s}>
              {t(`studio.props.source.${s}`)}
            </option>
          ))}
        </Select>
      </Field>
      <div className={styles.nested}>
        {source === 'literal' && <LiteralInput label={t('studio.props.literal', { label })} value={asText(value)} onChange={onChange} type={type} column={column} />}
        {(source === 'field' || source === 'related' || source === 'created') && <FieldValue value={map} onChange={onChange} ctx={ctx} source={source} />}
        {source === 'record' && <RecordEditor label={t('studio.props.record')} value={map.record} onChange={(record) => onChange({ record: record ?? 'primary' })} ctx={ctx} required />}
        {source === 'output' && (
          <>
            <StepPicker label={t('studio.props.step')} value={map.of} onChange={(of) => onChange({ output: map.output ?? '', of })} steps={actionSteps(ctx)} />
            <TextInput label={t('studio.props.outputName')} value={asText(map.output)} onChange={(output) => onChange({ output, of: map.of ?? '' })} required />
          </>
        )}
        {source === 'arg' &&
          (ctx.inputArgs.length > 0 ? (
            <ChoicePicker label={t('studio.props.argument')} value={map.arg} onChange={(arg) => onChange({ arg })} options={ctx.inputArgs.map((a) => [a, a])} required />
          ) : (
            <TextInput label={t('studio.props.argument')} value={asText(map.arg)} onChange={(arg) => onChange({ arg })} required />
          ))}
        {source === 'lookup' && <LookupLiteral value={map} onChange={onChange} ctx={ctx} column={column} />}
        {isValueOperator(source) && <Operands value={value} op={source} onChange={onChange} ctx={ctx} column={column} literal={props.literal} />}
        {source === 'other' && <Text className={styles.hint}>{t('studio.props.otherHint')}</Text>}
      </div>
    </div>
  );
}

/** `{ field }`, `{ field, related }` or `{ field, created }`: the record, then its column. */
function FieldValue({ value, onChange, ctx, source }: { value: Record<string, unknown>; onChange(value: unknown): void; ctx: ValueContext; source: 'field' | 'related' | 'created' }) {
  const field = value.field ?? '';
  const table = tableOfField(value, ctx);
  return (
    <>
      {source === 'related' && (
        <ColumnPicker
          label={t('studio.props.lookup')}
          value={value.related}
          onChange={(related) => onChange({ field, related })}
          ctx={ctx}
          table={ctx.entity}
          filter={isLookup}
          required
        />
      )}
      {source === 'created' && <StepPicker label={t('studio.props.step')} value={value.created} onChange={(created) => onChange({ field, created })} steps={createdSteps(ctx)} />}
      <ColumnPicker
        label={t('studio.props.column')}
        value={field}
        onChange={(next) => onChange(source === 'field' ? { field: next } : { field: next, [source]: value[source] ?? '' })}
        ctx={ctx}
        table={table}
        required
      />
    </>
  );
}

/** The operands of a value operator, each a value of its own. */
function Operands({ value, op, onChange, ctx, column, literal }: { value: unknown; op: string; onChange(value: unknown): void; ctx: ValueContext; column?: AttributeMeta; literal?: LiteralType }) {
  const styles = useStyles();
  const operands = operandsOf(value);
  const write = (next: unknown[]) => onChange({ [op]: next });
  return (
    <>
      {operands.map((operand, i) => (
        <div key={i} className={styles.row}>
          <div className={styles.grow}>
            <ValueEditor
              label={t('studio.props.operand', { n: i + 1 })}
              value={operand}
              onChange={(next) => write(operands.map((o, j) => (j === i ? next : o)))}
              ctx={ctx}
              column={op === 'concat' ? undefined : column}
              literal={op === 'concat' ? 'text' : literal}
            />
          </div>
          <Button
            appearance="subtle"
            icon={<DeleteIcon />}
            aria-label={t('studio.props.removeOperand', { n: i + 1 })}
            disabled={operands.length <= 1}
            onClick={() => write(operands.filter((_, j) => j !== i))}
          />
        </div>
      ))}
      <div>
        <Button size="small" icon={<NewIcon />} onClick={() => write([...operands, ''])}>
          {t('studio.props.addOperand')}
        </Button>
      </div>
    </>
  );
}
