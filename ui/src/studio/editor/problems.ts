import type { ApplyResult } from '../api/studioApi';
import type { ModelProblem, StepNode, StepPath, WorkflowModel } from '../model/types';

/** A problem of the editor's document, as the problems pane, the code view and the canvas show it. Lines are 1-based. */
export interface EditorProblem {
  severity: 'error' | 'warning';
  message: string;
  line?: number;
  column?: number;
  /** Where it comes from: the client's reader (`local`) or the server's dry run or save (`server`). */
  source?: 'local' | 'server';
}

/**
 * The document's problems: the local ones (the reader's) when there are any, else the server's errors and its assumed shapes
 * (warnings, no position). A server answer is about a text the reader accepted, so it is not shown next to local problems.
 */
export function collectProblems(local: ModelProblem[], server: ApplyResult | undefined): EditorProblem[] {
  if (local.length > 0) return local.map((p) => ({ severity: 'error', message: p.message, line: p.line, column: p.column, source: 'local' }));
  if (!server) return [];
  return [
    ...server.errors.map((e): EditorProblem => ({ severity: 'error', message: e.message, line: e.at?.line, column: e.at?.column, source: 'server' })),
    ...server.assumedShapes.map((s): EditorProblem => ({ severity: 'warning', message: s, source: 'server' })),
  ];
}

/** The problems that have a line, by line (for the canvas's badges). */
export function problemsByLine(problems: EditorProblem[]): Map<number, EditorProblem[]> {
  const byLine = new Map<number, EditorProblem[]>();
  for (const p of problems) {
    if (p.line === undefined) continue;
    byLine.set(p.line, [...(byLine.get(p.line) ?? []), p]);
  }
  return byLine;
}

/**
 * The card that holds `line`: the deepest step whose lines (from its own line to the line before the next step at its level,
 * or to the end of its parent's) contain it; `trigger` for a line before the first step (the header and the trigger), and
 * undefined for a model without steps there.
 */
export function stepAtLine(model: WorkflowModel, line: number): StepPath | 'trigger' | undefined {
  if (model.steps.length === 0 || line < model.steps[0].line) return 'trigger';
  return within(model.steps, line, Number.POSITIVE_INFINITY);
}

function within(steps: StepNode[], line: number, end: number): StepPath | undefined {
  for (let i = steps.length - 1; i >= 0; i--) {
    const step = steps[i];
    const stepEnd = i + 1 < steps.length ? steps[i + 1].line : end;
    if (line < step.line || line >= stepEnd) continue;
    // A branch ends where the next branch (in the text) starts, or at the step's end.
    const branches = step.branches.filter((b) => b.steps.length > 0).sort((a, b) => a.steps[0].line - b.steps[0].line);
    for (let b = 0; b < branches.length; b++) {
      const inner = within(branches[b].steps, line, b + 1 < branches.length ? branches[b + 1].steps[0].line : stepEnd);
      if (inner) return inner;
    }
    return step.path;
  }
  return undefined;
}
