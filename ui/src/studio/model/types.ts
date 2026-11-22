/** The typed model of a workflow YAML document (plan 2.21 format), derived from the `yaml` Document for the canvas. */
import type { en } from '../../shell/i18n/en';

/**
 * A text of the model the UI shows in its own language: a message key of the UI catalogue and its arguments. An argument is a
 * name, a value or a YAML literal written as is (a string or a number), or another phrase. `phraseText` renders it.
 */
export interface Phrase {
  key: keyof typeof en;
  args?: Record<string, Words>;
}

/** A literal written as is, or a phrase. */
export type Words = string | number | Phrase;

export type ProcessKind = 'workflow' | 'businessRule' | 'action';

export type StepType =
  | 'create'
  | 'update'
  | 'assign'
  | 'setState'
  | 'sendEmail'
  | 'if'
  | 'wait'
  | 'stop'
  | 'startChild'
  | 'performAction'
  | 'customActivity'
  | 'setOutput'
  | 'setValue'
  | 'setDefault'
  | 'clearValue'
  | 'showError'
  | 'setVisibility'
  | 'setLock'
  | 'setRequirement';

/**
 * The YAML path of a node: a step is the path of its mapping (`['steps', 2, 'if', 'then', 0]`), a branch the path of its
 * sequence (`['steps', 2, 'if', 'elseIf', 1, 'then']`, `['steps', 0, 'wait', 'onTimeout']`).
 */
export type StepPath = (string | number)[];

export interface StepNode {
  type: StepType;
  path: StepPath;
  description?: string;
  /** The step's `id` (create, sendEmail, performAction, customActivity), by which later steps refer to it. */
  id?: string;
  /** The table of the record the step makes: the `entity` of a create, `email` for a sendEmail. */
  entity?: string;
  /** A short one-line text for the canvas card ("Update record: primary"), in words the UI translates (`phraseText`). */
  summary: Phrase;
  branches: Branch[];
  /** The 1-based line of the step's mapping. */
  line: number;
}

export interface Branch {
  label: 'then' | 'elseIf' | 'else' | 'onTimeout';
  /** The path of the branch's step sequence. */
  path: StepPath;
  steps: StepNode[];
  /** The condition as plain JS: `when` of an `if` or `elseIf` branch, `until` of a wait's `then`. */
  when?: unknown;
}

export interface WorkflowModel {
  kind: ProcessKind;
  id?: string;
  name: string;
  entity?: string;
  mode?: 'realtime' | 'background';
  /** Every top-level key but `trigger` and `steps`, as plain JS, in document order. */
  header: Record<string, unknown>;
  trigger?: unknown;
  steps: StepNode[];
}

/** A problem of the document, 1-based. */
export interface ModelProblem {
  message: string;
  line: number;
  column: number;
}
