/**
 * The step palette of the canvas: the steps valid for the kind of process and the place of the slot, with the placement
 * rules of the reader that moves use too (`place.ts`), and, disabled, what the step library lacks with the alternative.
 */
import type { en } from '../../shell/i18n/en';
import { STEP_KEYS } from './keys';
import { stepPlaceProblem } from './place';
import type { ProcessKind, StepType } from './types';

/** A key of the UI catalogue, rendered with `t()` by the palette. */
type MessageKey = keyof typeof en;

export type PaletteEntry = { type: StepType; enabled: true } | { label: MessageKey; enabled: false; reason: MessageKey };

/** What the step library lacks (docs/workflow-authoring.md, "What is refused and why"), listed disabled in every palette. */
const MISSING: PaletteEntry[] = [
  { label: 'studio.palette.sendEmailFromTemplate', enabled: false, reason: 'studio.palette.sendEmailFromTemplate.reason' },
  { label: 'studio.palette.recommendation', enabled: false, reason: 'studio.palette.recommendation.reason' },
  { label: 'studio.palette.dialog', enabled: false, reason: 'studio.palette.dialog.reason' },
];

/**
 * The palette of a slot: the steps allowed there, in the order of the format's reference, then the missing steps. `mode`
 * is the workflow's mode (undefined for a business rule or an action). `insideWait` changes nothing in the 2.21 format (a
 * wait may hold any step of a background workflow, another wait included); it is part of the place for the canvas.
 */
export function paletteFor(
  kind: ProcessKind,
  mode: 'realtime' | 'background' | undefined,
  place: { insideIf: boolean; insideWait: boolean },
): PaletteEntry[] {
  const process = { kind, background: kind === 'workflow' && mode === 'background' };
  const steps = (Object.keys(STEP_KEYS) as StepType[]).filter((type) => stepPlaceProblem({ type, branches: [] }, process, place) === undefined);
  return [...steps.map((type): PaletteEntry => ({ type, enabled: true })), ...MISSING.map((entry) => ({ ...entry }))];
}
