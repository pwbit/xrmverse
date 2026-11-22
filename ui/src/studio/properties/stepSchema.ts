/**
 * The form of each step type for the properties panel (spec §4.6): one `FieldSpec` per key of the step's body, from the same
 * mirror of the 2.21 format as the reader (`STEP_KEYS`). A test keeps every key of `STEP_KEYS` covered, so a new key of the
 * format must get a field here.
 */
import type { StepNode, StepPath, StepType, WorkflowModel } from '../model/types';

/**
 * How a key is edited:
 * - `record`: primary, `{ related: <lookup> }` or `{ created: <step id> }`;
 * - `table`: a table's logical name; `column`: a column of the primary table (business rule actions);
 * - `set`: a mapping of names to values (`set`, `inputs`, `outputs`);
 * - `value`: a value (literal or dynamic, see the value editor); `condition`: a condition (see the condition builder);
 * - `enum`: one of `options` (or of options the panel reads from the metadata or the header when `options` is absent);
 * - `text`; `bool`: true or false; `workflow`: a workflow's name or id; `message`: an action's message name;
 * - `branch`: a list of steps, edited on the canvas, not in the panel.
 *
 * `column` and `branch` extend the brief's list: a business rule names a column of its table, and `then`/`else`/... are
 * keys of the step that the canvas owns.
 */
export type FieldKind = 'record' | 'table' | 'set' | 'value' | 'condition' | 'enum' | 'text' | 'workflow' | 'message' | 'bool' | 'column' | 'branch';

export interface FieldSpec {
  key: string;
  kind: FieldKind;
  /** The key must be there (with a value that is not empty, but for a `value`, which may be `~`). */
  required?: boolean;
  options?: string[];
}

const description: FieldSpec = { key: 'description', kind: 'text' };
const id: FieldSpec = { key: 'id', kind: 'text' };

/** The fields of each step, in the order the panel shows them. */
export const STEP_SCHEMA: Record<StepType, FieldSpec[]> = {
  create: [{ key: 'entity', kind: 'table', required: true }, { key: 'set', kind: 'set', required: true }, id, description],
  update: [{ key: 'record', kind: 'record', required: true }, { key: 'set', kind: 'set', required: true }, description],
  assign: [{ key: 'record', kind: 'record', required: true }, { key: 'owner', kind: 'value', required: true }, description],
  setState: [
    { key: 'record', kind: 'record', required: true },
    { key: 'state', kind: 'enum', required: true },
    { key: 'status', kind: 'enum', required: true },
    description,
  ],
  sendEmail: [{ key: 'set', kind: 'set', required: true }, id, description],
  if: [
    { key: 'when', kind: 'condition', required: true },
    { key: 'then', kind: 'branch', required: true },
    { key: 'elseIf', kind: 'branch' },
    { key: 'else', kind: 'branch' },
    description,
  ],
  wait: [
    { key: 'until', kind: 'condition' },
    { key: 'timeout', kind: 'value' },
    { key: 'then', kind: 'branch', required: true },
    { key: 'onTimeout', kind: 'branch' },
    description,
  ],
  stop: [{ key: 'status', kind: 'enum', required: true, options: ['succeeded', 'canceled'] }, { key: 'message', kind: 'value' }, description],
  startChild: [{ key: 'workflow', kind: 'workflow', required: true }, { key: 'record', kind: 'record' }, description],
  performAction: [
    { key: 'message', kind: 'message', required: true },
    { key: 'target', kind: 'record' },
    { key: 'inputs', kind: 'set' },
    id,
    description,
  ],
  customActivity: [
    { key: 'type', kind: 'text', required: true },
    { key: 'assembly', kind: 'text', required: true },
    { key: 'inputs', kind: 'set' },
    { key: 'outputs', kind: 'set' },
    id,
    description,
  ],
  setOutput: [{ key: 'argument', kind: 'enum', required: true }, { key: 'value', kind: 'value', required: true }, description],
  setValue: [{ key: 'field', kind: 'column', required: true }, { key: 'value', kind: 'value', required: true }, description],
  setDefault: [{ key: 'field', kind: 'column', required: true }, { key: 'value', kind: 'value', required: true }, description],
  clearValue: [{ key: 'field', kind: 'column', required: true }, description],
  showError: [{ key: 'field', kind: 'column' }, { key: 'message', kind: 'text', required: true }, description],
  setVisibility: [{ key: 'field', kind: 'column', required: true }, { key: 'visible', kind: 'bool', required: true }, description],
  setLock: [{ key: 'field', kind: 'column', required: true }, { key: 'locked', kind: 'bool', required: true }, description],
  setRequirement: [
    { key: 'field', kind: 'column', required: true },
    { key: 'level', kind: 'enum', required: true, options: ['none', 'recommended', 'required'] },
    description,
  ],
};

/** The comparison operators of a condition (`WorkflowYaml.Operators`). */
export const OPERATORS = ['eq', 'ne', 'null', 'notNull', 'contains', 'notContains', 'beginsWith', 'endsWith', 'gt', 'ge', 'lt', 'le'] as const;
/** The operators that take no `value`. */
export const UNARY_OPERATORS: readonly string[] = ['null', 'notNull'];
/** The value operators (`WorkflowYaml.ValueOperators`). */
export const VALUE_OPERATORS = ['add', 'subtract', 'multiply', 'increment', 'decrement', 'set', 'concat', 'firstOf'] as const;
/** The argument types of an action (`WorkflowYaml.ArgumentTypes`). */
export const ARGUMENT_TYPES = ['boolean', 'dateTime', 'decimal', 'double', 'entityReference', 'integer', 'money', 'picklist', 'string'];
/** The scopes of a workflow (`WorkflowYaml.WorkflowScopes`); `user` is the default. */
export const WORKFLOW_SCOPES = ['user', 'businessUnit', 'parentChildBusinessUnits', 'organization'];

/** A step with an id that comes before another on its path: what `created` and `output of` may name. */
export interface EarlierStep {
  id: string;
  type: StepType;
  /** The table of the record it makes (create, sendEmail). */
  entity?: string;
}

const startsWith = (path: StepPath, prefix: StepPath) => prefix.length <= path.length && prefix.every((key, i) => path[i] === key);

/**
 * The steps with an id that run before the step at `path` on every run that reaches it: the steps before it in its own
 * list and before each of its ancestors in theirs. Steps of a sibling branch, steps inside an earlier step's branches and
 * later steps are left out.
 */
export function earlierSteps(model: WorkflowModel, path: StepPath): EarlierStep[] {
  const out: EarlierStep[] = [];
  let steps: StepNode[] = model.steps;
  for (;;) {
    const at = steps.findIndex((step) => startsWith(path, step.path));
    if (at < 0) return out;
    for (const step of steps.slice(0, at)) if (step.id !== undefined) out.push({ id: step.id, type: step.type, ...(step.entity ? { entity: step.entity } : {}) });
    const holder = steps[at];
    if (holder.path.length === path.length) return out;
    const branch = holder.branches.find((b) => startsWith(path, b.path));
    if (!branch) return out;
    steps = branch.steps;
  }
}

/** The step at `path` in the model, or undefined when there is none (after a delete or a move). */
export function findStep(model: WorkflowModel, path: StepPath): StepNode | undefined {
  let steps: StepNode[] = model.steps;
  for (;;) {
    const step = steps.find((s) => startsWith(path, s.path));
    if (!step) return undefined;
    if (step.path.length === path.length) return step;
    const branch = step.branches.find((b) => startsWith(path, b.path));
    if (!branch) return undefined;
    steps = branch.steps;
  }
}
