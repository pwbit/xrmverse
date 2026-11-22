import {
  ArrowExportRegular,
  ArrowResetRegular,
  BranchRegular,
  ClockRegular,
  DocumentAddRegular,
  EditRegular,
  EraserRegular,
  ErrorCircleRegular,
  EyeRegular,
  FlashRegular,
  FlowRegular,
  LockClosedRegular,
  MailRegular,
  PersonArrowRightRegular,
  PuzzlePieceRegular,
  RecordStopRegular,
  TextAsteriskRegular,
  TextEditStyleRegular,
  ToggleLeftRegular,
  type FluentIcon,
} from '@fluentui/react-icons';
import { createContext, useContext, type MutableRefObject, type ReactNode } from 'react';
import type { Document } from 'yaml';
import type { en } from '../../shell/i18n/en';
import { moveProblem, processOf } from '../model/place';
import type { StepNode, StepPath, StepType, WorkflowModel } from '../model/types';

/** Where a slot or a card stands, for the step palette (`paletteFor`). */
export interface Place {
  insideIf: boolean;
  insideWait: boolean;
}

/** The problems a card shows on its badge. */
export interface CardProblems {
  count: number;
  error: boolean;
}

/** What the canvas hands its cards and slots. */
export interface CanvasContextValue {
  model: WorkflowModel;
  /** A diagram only (the code view's): no slots, menus, drag, selection or change. */
  readOnly: boolean;
  selected?: StepPath | 'trigger';
  select(selected: StepPath | 'trigger'): void;
  /**
   * Applies an `edit.ts` operation to the document. A refused operation changes nothing and shows `message` (or the
   * operation's own message); the result is then undefined. Any change ends a pending Cut.
   */
  run<T>(fn: (doc: Document.Parsed) => T, message?: string): T | undefined;
  /** The step that was cut and waits for Paste, while the document has not changed since. */
  clip?: StepNode;
  cut(step: StepNode): void;
  /** The step being dragged (a drop target cannot read the drag data while the pointer moves over it). */
  dragging: MutableRefObject<StepPath | undefined>;
  /** The problems by card (`cardKey`). */
  problems: Map<string, CardProblems>;
  /** Asks before deleting a step that has steps inside it. */
  askDelete(step: StepNode): void;
  /** Draws the steps of a branch, with its slots. */
  renderFlow(branch: StepPath, steps: StepNode[], place: Place): ReactNode;
}

export const CanvasContext = createContext<CanvasContextValue | undefined>(undefined);

export function useCanvas(): CanvasContextValue {
  const value = useContext(CanvasContext);
  if (!value) throw new Error('useCanvas is used outside the canvas.');
  return value;
}

/** The key of a card (and of its selection): the trigger, or the step's path. */
export const cardKey = (at: StepPath | 'trigger') => (at === 'trigger' ? 'trigger' : JSON.stringify(at));

export const samePlace = (a: StepPath | 'trigger' | undefined, b: StepPath | 'trigger') => a !== undefined && cardKey(a) === cardKey(b);

/** Whether `path` is `prefix` or inside it. */
export function within(path: StepPath, prefix: StepPath): boolean {
  return prefix.length <= path.length && prefix.every((key, i) => path[i] === key);
}

/** The step at `path` in the model. */
export function stepOf(model: WorkflowModel, path: StepPath): StepNode | undefined {
  const key = cardKey(path);
  const find = (steps: StepNode[]): StepNode | undefined => {
    for (const step of steps) {
      if (cardKey(step.path) === key) return step;
      for (const branch of step.branches) {
        const found = find(branch.steps);
        if (found) return found;
      }
    }
    return undefined;
  };
  return find(model.steps);
}

/**
 * Whether the step at `from` may move into the list `branch` at all: not into itself, and only where it and every step
 * inside it are valid (the palette's place rule, `place.ts`). The canvas offers no drop and no Paste anywhere else.
 */
export function canMoveTo(model: WorkflowModel, from: StepPath, branch: StepPath): boolean {
  if (within(branch, from)) return false;
  const step = stepOf(model, from);
  return step !== undefined && moveProblem(step, processOf(model), branch) === undefined;
}

/** The MIME type of a dragged card's path. */
export const DRAG_TYPE = 'application/x-xrmverse-step';

/** The palette label of each step type. */
export const STEP_LABEL = (type: StepType) => `studio.step.${type}` as keyof typeof en;

/** The Fluent icon of each step type (our own choice; no designer imagery). */
export const STEP_ICONS: Record<StepType, FluentIcon> = {
  create: DocumentAddRegular,
  update: EditRegular,
  assign: PersonArrowRightRegular,
  setState: ToggleLeftRegular,
  sendEmail: MailRegular,
  if: BranchRegular,
  wait: ClockRegular,
  stop: RecordStopRegular,
  startChild: FlowRegular,
  performAction: FlashRegular,
  customActivity: PuzzlePieceRegular,
  setOutput: ArrowExportRegular,
  setValue: TextEditStyleRegular,
  setDefault: ArrowResetRegular,
  clearValue: EraserRegular,
  showError: ErrorCircleRegular,
  setVisibility: EyeRegular,
  setLock: LockClosedRegular,
  setRequirement: TextAsteriskRegular,
};
