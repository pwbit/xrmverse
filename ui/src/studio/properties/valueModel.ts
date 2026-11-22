/**
 * The shapes of values and conditions of the 2.21 format (docs/workflow-authoring.md, "Values and conditions") as the value
 * editor and the condition builder read and build them. Values are plain JS as `valueAt` reads them: `null` is the empty
 * value `~`, a string a literal, a mapping a dynamic value.
 */
import type { AttributeMeta } from '../../admin/tables/metadata';
import { PlainText } from '../model/edit';
import type { ProcessKind } from '../model/types';
import { UNARY_OPERATORS, VALUE_OPERATORS, type EarlierStep } from './stepSchema';
import type { MetadataSource } from './useStudioMetadata';

export type ValueOperator = (typeof VALUE_OPERATORS)[number];

/** Where a value comes from: the choice at the head of the value editor. `other` is a shape the editor does not know. */
export type Source = 'literal' | 'empty' | 'field' | 'related' | 'created' | 'record' | 'output' | 'arg' | 'now' | 'lookup' | ValueOperator | 'other';

/** What the editors of a step know besides the value itself. */
export interface ValueContext {
  kind: ProcessKind;
  /** The workflow's primary table. */
  entity?: string;
  meta: MetadataSource;
  /** The steps with an id before the edited step on its path (`earlierSteps`). */
  earlier: EarlierStep[];
  /** The action's input arguments (`{ arg }`). */
  inputArgs: string[];
  /** The action's output arguments and their types (`setOutput`). */
  outputArgs: { name: string; type: string }[];
}

const isMapping = (value: unknown): value is Record<string, unknown> => !!value && typeof value === 'object' && !Array.isArray(value);
const OWN_KEYS = ['record', 'arg', 'output', 'now', 'lookup', ...VALUE_OPERATORS] as const;

export function sourceOf(value: unknown): Source {
  if (value === null) return 'empty';
  if (value === undefined || typeof value === 'string') return 'literal';
  if (!isMapping(value)) return 'other';
  if ('field' in value) return 'related' in value ? 'related' : 'created' in value ? 'created' : 'field';
  return OWN_KEYS.find((key) => key in value) ?? 'other';
}

export const isValueOperator = (source: Source): source is ValueOperator => (VALUE_OPERATORS as readonly string[]).includes(source);

/** The operands of a value operator (`{ add: [a, b] }`). */
export function operandsOf(value: unknown): unknown[] {
  if (!isMapping(value)) return [];
  const source = sourceOf(value);
  const operands = isValueOperator(source) ? value[source] : undefined;
  return Array.isArray(operands) ? operands : [];
}

/**
 * How a literal is typed by its destination: by the column (`set`, `field`) or the argument (`setOutput`, `inputs`).
 * `reference` is a lookup: it takes a record or a lookup literal, never text.
 */
export type LiteralType = 'text' | 'number' | 'whole' | 'bool' | 'choice' | 'datetime' | 'reference';

export function literalTypeOf(column: AttributeMeta | undefined): LiteralType {
  switch (column?.kind) {
    case 'int':
    case 'bigint':
      return 'whole';
    case 'decimal':
    case 'double':
    case 'money':
      return 'number';
    case 'bool':
      return 'bool';
    case 'choice':
      return 'choice';
    case 'datetime':
      return 'datetime';
    case 'lookup':
    case 'partylist':
      return 'reference';
    default:
      return 'text';
  }
}

/** The literal type of an action argument's type (`ARGUMENT_TYPES`). */
export function literalTypeOfArgument(type: string | undefined): LiteralType {
  switch (type) {
    case 'boolean':
      return 'bool';
    case 'dateTime':
      return 'datetime';
    case 'decimal':
    case 'double':
    case 'money':
      return 'number';
    case 'integer':
    case 'picklist':
      return 'whole';
    case 'entityReference':
      return 'reference';
    default:
      return 'text';
  }
}

/** The table a lookup column of `table` points to, when it points to one table only. */
export function lookupTarget(ctx: ValueContext, table: string | undefined, lookup: unknown): string | undefined {
  if (typeof lookup !== 'string') return undefined;
  const column = ctx.meta.entity(table)?.attributes.find((a) => a.logicalName === lookup);
  return column?.targets.length === 1 ? column.targets[0] : undefined;
}

/** The table of a record (`primary`, `{ related }`, `{ created }`), when the panel can tell. */
export function tableOfRecord(record: unknown, ctx: ValueContext): string | undefined {
  if (record === 'primary') return ctx.entity;
  if (!isMapping(record)) return undefined;
  if ('related' in record) return lookupTarget(ctx, ctx.entity, record.related);
  if ('created' in record) return ctx.earlier.find((step) => step.id === record.created)?.entity;
  return undefined;
}

/** The table a `field` value reads from: the primary record's, the lookup's target, or the created record's. */
export function tableOfField(value: Record<string, unknown>, ctx: ValueContext): string | undefined {
  if ('related' in value) return lookupTarget(ctx, ctx.entity, value.related);
  if ('created' in value) return ctx.earlier.find((step) => step.id === value.created)?.entity;
  return ctx.entity;
}

/** The column a dynamic value reads (a `field`), to type the other side of a comparison. */
export function columnOf(value: unknown, ctx: ValueContext): AttributeMeta | undefined {
  if (!isMapping(value) || typeof value.field !== 'string') return undefined;
  return ctx.meta.entity(tableOfField(value, ctx))?.attributes.find((a) => a.logicalName === value.field);
}

/** The value a new choice of source starts with; an operator keeps the operands of the operator it replaces. */
export function startValue(source: Source, previous: unknown, ctx: ValueContext, target?: string): unknown {
  const created = ctx.earlier.find((step) => step.type === 'create' || step.type === 'sendEmail');
  const action = ctx.earlier.find((step) => step.type === 'performAction' || step.type === 'customActivity');
  switch (source) {
    case 'empty':
      return null;
    case 'field':
      return { field: '' };
    case 'related':
      return { field: '', related: '' };
    case 'created':
      return { field: '', created: created?.id ?? '' };
    case 'record':
      return { record: 'primary' };
    case 'output':
      return { output: '', of: action?.id ?? '' };
    case 'arg':
      return { arg: ctx.inputArgs[0] ?? '' };
    case 'now':
      return { now: 'true' };
    case 'lookup':
      return { lookup: target ?? '', id: '' };
    case 'literal':
    case 'other':
      return '';
    default: {
      const kept = isValueOperator(sourceOf(previous)) ? operandsOf(previous) : previous === undefined || previous === null ? [''] : [previous];
      return { [source]: kept.length > 0 ? kept : [''] };
    }
  }
}

// -------------------------------------------------------------------- conditions

export type ConditionKind = 'compare' | 'all' | 'any' | 'not';

export function conditionKind(condition: unknown): ConditionKind {
  if (isMapping(condition)) {
    if ('all' in condition) return 'all';
    if ('any' in condition) return 'any';
    if ('not' in condition) return 'not';
  }
  return 'compare';
}

/** The conditions of a group (`all`, `any`). */
export function childrenOf(condition: unknown): unknown[] {
  if (!isMapping(condition)) return [];
  const list = condition.all ?? condition.any;
  return Array.isArray(list) ? list : [];
}

export interface Comparison {
  /** The left operand: the value's own keys (`{ field, related? }`, `{ arg }`, ...). */
  left: Record<string, unknown>;
  op: string;
  /** The right operand; absent for `null` and `notNull`. */
  value?: unknown;
}

/** A comparison's parts. `op: null` written plain reads as the empty value (`valueAt`) and is the operator `null`. */
export function splitComparison(condition: unknown): Comparison {
  const map = isMapping(condition) ? condition : {};
  const left: Record<string, unknown> = {};
  for (const [key, item] of Object.entries(map)) if (key !== 'op' && key !== 'value') left[key] = item;
  const op = map.op === null ? 'null' : map.op === undefined ? 'eq' : String(map.op);
  return UNARY_OPERATORS.includes(op) ? { left, op } : { left, op, value: 'value' in map ? map.value : '' };
}

/** `{ ...left, op, value? }`, in the writer's order. */
export function joinComparison({ left, op, value }: Comparison): Record<string, unknown> {
  return UNARY_OPERATORS.includes(op) ? { ...left, op } : { ...left, op, value: value === undefined ? '' : value };
}

/** A new comparison, the condition builder's new row. */
export const newComparison = (): Record<string, unknown> => ({ field: '', op: 'eq', value: '' });

/**
 * The condition with each operator as text: `op: null` written plain reads as the empty value (`valueAt`) and is the
 * operator `null`.
 */
export function normalizeCondition(condition: unknown): unknown {
  if (!isMapping(condition)) return condition;
  if (Array.isArray(condition.all)) return { ...condition, all: condition.all.map(normalizeCondition) };
  if (Array.isArray(condition.any)) return { ...condition, any: condition.any.map(normalizeCondition) };
  if ('not' in condition) return { ...condition, not: normalizeCondition(condition.not) };
  return condition.op === null ? { ...condition, op: 'null' } : condition;
}

/**
 * The condition as `setAt` must write it: each operator a `PlainText`, so that the operator `null` is written `op: null`
 * as the server writes it (a quoted `'null'` reads the same, but is not the canonical form).
 */
export function conditionForYaml(condition: unknown): unknown {
  if (!isMapping(condition)) return condition;
  if (Array.isArray(condition.all)) return { all: condition.all.map(conditionForYaml) };
  if (Array.isArray(condition.any)) return { any: condition.any.map(conditionForYaml) };
  if ('not' in condition) return { not: conditionForYaml(condition.not) };
  if (condition.op === null) return { ...condition, op: new PlainText('null') };
  return typeof condition.op === 'string' ? { ...condition, op: new PlainText(condition.op) } : condition;
}
