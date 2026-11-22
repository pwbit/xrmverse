/**
 * The keys of the workflow YAML format (plan 2.21), mirrored from `WorkflowYaml` (src/Xrmd.Schema/Workflows). A unit test
 * keeps `REFERENCE_KEYS` equal to the generated table of docs/workflow-authoring.md, so a new key of the format breaks the
 * studio's tests until it is handled here.
 */
import type { ProcessKind, StepType } from './types';

/** The top-level keys every kind takes. */
export const TOP_KEYS: string[] = ['kind', 'id', 'name', 'description', 'entity', 'steps'];

/** The top-level keys of one kind only, in addition to `TOP_KEYS`. */
export const KIND_KEYS: Record<ProcessKind, string[]> = {
  workflow: ['mode', 'scope', 'runAs', 'logFailures', 'autoDelete', 'trigger', 'onDemand', 'childProcess'],
  businessRule: ['scope'],
  action: ['message', 'arguments'],
};

/** The keys of the `trigger` mapping of a workflow. */
export const TRIGGER_KEYS: string[] = ['create', 'update', 'delete'];

/** The keys of one action argument. */
export const ARGUMENT_KEYS: string[] = ['name', 'direction', 'type', 'entity', 'required', 'description'];

/** The keys of each step's body mapping (`- <step>: { ... }`). */
export const STEP_KEYS: Record<StepType, string[]> = {
  create: ['entity', 'set', 'id', 'description'],
  update: ['record', 'set', 'description'],
  assign: ['record', 'owner', 'description'],
  setState: ['record', 'state', 'status', 'description'],
  sendEmail: ['set', 'id', 'description'],
  if: ['when', 'then', 'elseIf', 'else', 'description'],
  wait: ['until', 'timeout', 'then', 'onTimeout', 'description'],
  stop: ['status', 'message', 'description'],
  startChild: ['workflow', 'record', 'description'],
  performAction: ['message', 'target', 'inputs', 'id', 'description'],
  customActivity: ['type', 'assembly', 'inputs', 'outputs', 'id', 'description'],
  setOutput: ['argument', 'value', 'description'],
  setValue: ['field', 'value', 'description'],
  setDefault: ['field', 'value', 'description'],
  clearValue: ['field', 'description'],
  showError: ['field', 'message', 'description'],
  setVisibility: ['field', 'visible', 'description'],
  setLock: ['field', 'locked', 'description'],
  setRequirement: ['field', 'level', 'description'],
};

const RECORD_STEPS: ProcessKind[] = ['workflow', 'action'];
const RULE_ACTION: ProcessKind[] = ['businessRule'];

/**
 * The kinds of process each step may appear in. Placement rules finer than the kind are checked by the reader: `wait` only
 * in a background workflow, rule actions only inside an `if`.
 */
export const STEP_KINDS: Record<StepType, ProcessKind[]> = {
  create: RECORD_STEPS,
  update: RECORD_STEPS,
  assign: RECORD_STEPS,
  setState: RECORD_STEPS,
  sendEmail: RECORD_STEPS,
  if: ['workflow', 'businessRule', 'action'],
  wait: ['workflow'],
  stop: RECORD_STEPS,
  startChild: RECORD_STEPS,
  performAction: RECORD_STEPS,
  customActivity: RECORD_STEPS,
  setOutput: ['action'],
  setValue: RULE_ACTION,
  setDefault: RULE_ACTION,
  clearValue: RULE_ACTION,
  showError: RULE_ACTION,
  setVisibility: RULE_ACTION,
  setLock: RULE_ACTION,
  setRequirement: RULE_ACTION,
};

/** The business rule actions: valid only inside an `if` of a business rule. */
export const RULE_ACTIONS: StepType[] = (Object.keys(STEP_KINDS) as StepType[]).filter((type) => STEP_KINDS[type] === RULE_ACTION);

/** The `Key` column of the generated reference of docs/workflow-authoring.md (`WorkflowYaml.Keys`). */
export const REFERENCE_KEYS: string[] = [
  'kind',
  'id',
  'name',
  'description',
  'entity',
  'steps',
  'mode',
  'scope',
  'runAs',
  'logFailures',
  'autoDelete',
  'onDemand',
  'childProcess',
  'trigger.create',
  'trigger.update',
  'trigger.delete',
  'message',
  'arguments',
  'steps[].create',
  'steps[].update',
  'steps[].assign',
  'steps[].setState',
  'steps[].sendEmail',
  'steps[].if',
  'steps[].wait',
  'steps[].stop',
  'steps[].startChild',
  'steps[].performAction',
  'steps[].customActivity',
  'steps[].setOutput',
  'steps[].setValue',
  'steps[].setDefault',
  'steps[].clearValue',
  'steps[].showError',
  'steps[].setVisibility',
  'steps[].setLock',
  'steps[].setRequirement',
  'steps[].*.description',
  'record',
  'value',
  'condition',
];

/**
 * The key order of the 2.21 writer (`WorkflowYaml.Write`): a key the studio adds goes to its canonical place, so a document
 * built with the panel reads like an export. The block steps write `id` and `description` first; the one-line (flow) steps
 * write `description` last.
 */
export const HEADER_ORDER: string[] = [
  'kind',
  'id',
  'name',
  'description',
  'entity',
  'mode',
  'scope',
  'runAs',
  'logFailures',
  'autoDelete',
  'onDemand',
  'childProcess',
  'trigger',
  'message',
  'arguments',
  'steps',
];

export const STEP_ORDER: Record<StepType, string[]> = {
  create: ['id', 'description', 'entity', 'set'],
  update: ['description', 'record', 'set'],
  sendEmail: ['id', 'description', 'set'],
  if: ['description', 'when', 'then', 'elseIf', 'else'],
  wait: ['description', 'until', 'timeout', 'then', 'onTimeout'],
  performAction: ['id', 'description', 'message', 'target', 'inputs'],
  customActivity: ['id', 'description', 'type', 'assembly', 'inputs', 'outputs'],
  assign: ['record', 'owner', 'description'],
  setState: ['record', 'state', 'status', 'description'],
  stop: ['status', 'message', 'description'],
  startChild: ['workflow', 'record', 'description'],
  setOutput: ['argument', 'value', 'description'],
  setValue: ['field', 'value', 'description'],
  setDefault: ['field', 'value', 'description'],
  clearValue: ['field', 'description'],
  showError: ['field', 'message', 'description'],
  setVisibility: ['field', 'visible', 'description'],
  setLock: ['field', 'locked', 'description'],
  setRequirement: ['field', 'level', 'description'],
};

/** The order of the update trigger's keys and of an `elseIf` branch's. */
export const UPDATE_TRIGGER_ORDER: string[] = ['columns', 'stage'];
export const ELSE_IF_ORDER: string[] = ['when', 'then'];
