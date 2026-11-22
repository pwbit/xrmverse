/**
 * The one place rule of the canvas, shared by insert (the palette, `insertStep`) and move (drag and drop, Cut → Paste,
 * `moveStep`): a step may stand where the reader accepts it (`placeProblem`), and so may every step inside it once it has
 * moved. The canvas uses it to offer only valid targets; `edit.ts` uses it to refuse an operation whose result the reader
 * would refuse, so no canvas operation writes such a document.
 */
import { isMap, isScalar, isSeq } from 'yaml';
import type { Document, YAMLMap } from 'yaml';
import { STEP_KEYS } from './keys';
import { placeProblem } from './read';
import type { Branch, ProcessKind, StepPath, StepType, WorkflowModel } from './types';

/** The process a step goes into: its kind, and whether it is a background workflow (the only place a `wait` may be). */
export interface ProcessPlace {
  kind: ProcessKind;
  background: boolean;
}

/** Where a branch list stands: inside an `if` branch (where a business rule's actions go), inside a `wait`. */
export interface BranchPlace {
  insideIf: boolean;
  insideWait: boolean;
}

/** A step and the steps of its branches: a `StepNode` of the model, or the shape of a step node of the document. */
export interface StepShape {
  type: StepType;
  branches: { label: Branch['label']; steps: StepShape[] }[];
}

const isStepType = (key: unknown): key is StepType => typeof key === 'string' && Object.prototype.hasOwnProperty.call(STEP_KEYS, key);

/** The process of a model. */
export function processOf(model: Pick<WorkflowModel, 'kind' | 'mode'>): ProcessPlace {
  return { kind: model.kind, background: model.kind === 'workflow' && model.mode === 'background' };
}

/** The process of a document, from its `kind` and `mode` (undefined when the kind is not one the reader knows). */
export function processOfDocument(doc: Document): ProcessPlace | undefined {
  const text = (key: string) => {
    const node = doc.get(key, true);
    return isScalar(node) && node.value != null ? String(node.value) : undefined;
  };
  const kind = text('kind');
  if (kind !== 'workflow' && kind !== 'businessRule' && kind !== 'action') return undefined;
  return { kind, background: kind === 'workflow' && text('mode') === 'background' };
}

/**
 * The place of the branch list at `branch` (`['steps']`, `['steps', 0, 'if', 'then']`, ...): inside an `if` when a step on
 * its path is an `if` (its then, elseIf or else), inside a `wait` likewise.
 */
export function placeOf(branch: StepPath): BranchPlace {
  const typeAt = (type: StepType) => branch.some((key, i) => key === type && i > 0 && typeof branch[i - 1] === 'number');
  return { insideIf: typeAt('if'), insideWait: typeAt('wait') };
}

/** Why `step` (with everything inside it) cannot stand at `place` of `process`, or undefined when it can. */
export function stepPlaceProblem(step: StepShape, process: ProcessPlace, place: BranchPlace): string | undefined {
  const own = placeProblem(step.type, { ...process, insideIf: place.insideIf });
  if (own) return own;
  const inner: BranchPlace = step.type === 'if' ? { ...place, insideIf: true } : step.type === 'wait' ? { ...place, insideWait: true } : place;
  for (const branch of step.branches)
    for (const child of branch.steps) {
      const problem = stepPlaceProblem(child, process, inner);
      if (problem) return problem;
    }
  return undefined;
}

/** Why a new step of `type` cannot go into the list `branch`, or undefined when it can. */
export function insertProblem(type: StepType, process: ProcessPlace, branch: StepPath): string | undefined {
  return stepPlaceProblem({ type, branches: [] }, process, placeOf(branch));
}

/** Why `step` cannot move into the list `branch`, or undefined when it can. */
export function moveProblem(step: StepShape, process: ProcessPlace, branch: StepPath): string | undefined {
  return stepPlaceProblem(step, process, placeOf(branch));
}

/** The shape of a step node of the document (a mapping with one step key); undefined for a node that is not a step. */
export function shapeOfNode(node: unknown): StepShape | undefined {
  if (!isMap(node)) return undefined;
  const pair = node.items.find((item) => isStepType(isScalar(item.key) ? item.key.value : item.key));
  if (!pair) return undefined;
  const type = (isScalar(pair.key) ? pair.key.value : pair.key) as StepType;
  const body = pair.value;
  const branches: StepShape['branches'] = [];
  const list = (label: Branch['label'], value: unknown) => {
    if (isSeq(value)) branches.push({ label, steps: value.items.map(shapeOfNode).filter((s): s is StepShape => s !== undefined) });
  };
  if (isMap(body) && (type === 'if' || type === 'wait')) {
    list('then', body.get('then', true));
    if (type === 'if') {
      const elseIf = body.get('elseIf', true);
      if (isSeq(elseIf)) for (const item of elseIf.items) if (isMap(item)) list('elseIf', (item as YAMLMap).get('then', true));
      list('else', body.get('else', true));
    } else list('onTimeout', body.get('onTimeout', true));
  }
  return { type, branches };
}
