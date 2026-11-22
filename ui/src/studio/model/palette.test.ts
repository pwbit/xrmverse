import { afterEach, describe, expect, it } from 'vitest';
import { setUiLanguage, t } from '../../shell/i18n/t';
import { RULE_ACTIONS } from './keys';
import { paletteFor, type PaletteEntry } from './palette';
import type { StepType } from './types';

const top = { insideIf: false, insideWait: false };
const inIf = { insideIf: true, insideWait: false };

function types(entries: PaletteEntry[]): StepType[] {
  return entries.flatMap((entry) => (entry.enabled ? [entry.type] : []));
}

const RECORD_STEPS: StepType[] = ['create', 'update', 'assign', 'setState', 'sendEmail', 'if', 'stop', 'startChild', 'performAction', 'customActivity'];

describe('paletteFor', () => {
  afterEach(() => setUiLanguage(undefined));

  it('realtime workflow → no wait', () => {
    expect(types(paletteFor('workflow', 'realtime', top))).toEqual(RECORD_STEPS);
    expect(types(paletteFor('workflow', 'realtime', inIf))).toEqual(RECORD_STEPS);
  });

  it('background workflow → wait, also inside a wait', () => {
    const background = types(paletteFor('workflow', 'background', top));
    expect(background).toContain('wait');
    expect(background).not.toContain('setOutput');
    expect(types(paletteFor('workflow', 'background', { insideIf: true, insideWait: true }))).toEqual(background);
  });

  it('business rule top level → only if', () => {
    expect(types(paletteFor('businessRule', undefined, top))).toEqual(['if']);
  });

  it('inside if → rule actions', () => {
    expect(types(paletteFor('businessRule', undefined, inIf))).toEqual(['if', ...RULE_ACTIONS]);
    expect(RULE_ACTIONS).toEqual(['setValue', 'setDefault', 'clearValue', 'showError', 'setVisibility', 'setLock', 'setRequirement']);
  });

  it('action → setOutput present', () => {
    expect(types(paletteFor('action', undefined, top))).toEqual([...RECORD_STEPS, 'setOutput']);
    expect(types(paletteFor('action', undefined, inIf))).toContain('setOutput');
  });

  it('workflow → setOutput absent', () => {
    expect(types(paletteFor('workflow', 'background', inIf))).not.toContain('setOutput');
    expect(types(paletteFor('workflow', 'realtime', top))).not.toContain('setOutput');
  });

  it('every list includes the disabled entries with the alternative', () => {
    const places = [top, inIf];
    const lists = [
      ...places.map((place) => paletteFor('workflow', 'realtime', place)),
      ...places.map((place) => paletteFor('workflow', 'background', place)),
      ...places.map((place) => paletteFor('businessRule', undefined, place)),
      ...places.map((place) => paletteFor('action', undefined, place)),
    ];
    for (const list of lists) {
      const disabled = list.flatMap((entry) => (entry.enabled ? [] : [entry]));
      expect(disabled.map((entry) => t(entry.label))).toEqual(['Send email from template', 'Recommendation', 'Dialog']);
      for (const entry of disabled) expect(t(entry.reason)).toMatch(/webhook \(roadmap 3\.2\).*custom workflow activity/);
    }
  });

  it('translates the disabled entries', () => {
    setUiLanguage(1040);
    const disabled = paletteFor('workflow', 'realtime', top).flatMap((entry) => (entry.enabled ? [] : [entry]));
    expect(disabled.map((entry) => t(entry.label))).toEqual(['Invia e-mail da modello', 'Raccomandazione', 'Finestra di dialogo']);
    for (const entry of disabled) expect(t(entry.reason)).toMatch(/webhook \(roadmap 3\.2\).*attività del flusso di lavoro personalizzata/);
  });
});
