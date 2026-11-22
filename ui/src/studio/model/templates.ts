/**
 * The skeleton of each step the canvas inserts: the step's required keys only, so that the local reader accepts it and the
 * canvas can draw it at once. A value the author must choose (a table, a column, an argument) is the empty text `''`, which
 * the server then refuses with its line until the properties panel fills it; `~` is the format's empty value.
 */
import type { StepType } from './types';

const TEMPLATES: Record<StepType, Record<string, unknown>> = {
  create: { entity: '', set: {} },
  update: { record: 'primary', set: {} },
  assign: { record: 'primary', owner: '' },
  setState: { record: 'primary', state: '', status: '' },
  sendEmail: { set: {} },
  if: { when: { all: [] }, then: [] },
  // As the wait example of docs/workflow-authoring.md: a condition, no timeout (the format has no relative duration, and a
  // timeout of `{ now: true }` would fire at once). The properties panel sets a timeout; `addOnTimeout` needs one.
  wait: { until: { all: [] }, then: [] },
  stop: { status: 'succeeded' },
  startChild: { workflow: '' },
  performAction: { message: '' },
  customActivity: { type: '', assembly: '' },
  setOutput: { argument: '', value: null },
  setValue: { field: '', value: null },
  setDefault: { field: '', value: null },
  clearValue: { field: '' },
  showError: { message: '' },
  setVisibility: { field: '', visible: 'true' },
  setLock: { field: '', locked: 'true' },
  setRequirement: { field: '', level: 'required' },
};

/**
 * The steps whose body the 2.21 writer prints as a flow mapping on one line (`- stop: { status: canceled }`); the others
 * have a block body (`WorkflowYaml.Write`, `StepBody`).
 */
export const FLOW_BODY: ReadonlySet<StepType> = new Set<StepType>([
  'assign',
  'setState',
  'stop',
  'startChild',
  'setOutput',
  'setValue',
  'setDefault',
  'clearValue',
  'showError',
  'setVisibility',
  'setLock',
  'setRequirement',
]);

/** A new step `{ <type>: { ... } }`, a fresh object on every call. */
export function stepTemplate(type: StepType): unknown {
  return { [type]: JSON.parse(JSON.stringify(TEMPLATES[type])) as unknown };
}
