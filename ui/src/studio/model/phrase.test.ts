import { afterEach, describe, expect, it } from 'vitest';
import { ITALIAN_LCID, setUiLanguage } from '../../shell/i18n/t';
import { phraseText } from './phrase';
import { conditionText, parseText } from './read';

const TEXT = [
  'kind: workflow',
  'name: Flow',
  'entity: account',
  'mode: background',
  'steps:',
  '  - update: { record: { related: parentaccountid }, set: { name: X } }',
  '  - if:',
  '      when: { field: revenue, op: gt, value: 1000 }',
  '      then:',
  '        - create: { entity: task, set: { subject: Call } }',
  '      else:',
  '        - stop: { status: canceled }',
  '  - wait:',
  '      until: { all: [{ field: name, op: notNull }, { not: { field: revenue, op: null } }] }',
  '      timeout: { now: true }',
  '      then: []',
  '',
].join('\n');

const summaries = () => {
  const { model } = parseText(TEXT);
  return [model!.steps[0], model!.steps[1], model!.steps[1].branches[0].steps[0], model!.steps[1].branches[1].steps[0], model!.steps[2]].map((s) =>
    phraseText(s.summary),
  );
};

afterEach(() => setUiLanguage(undefined));

describe('step summaries', () => {
  it('read in English', () => {
    expect(summaries()).toEqual([
      'Update record: related parentaccountid',
      'If: revenue gt 1000',
      'Create record: task',
      'Stop: canceled',
      'Wait until all of 2 conditions or for a timeout',
    ]);
    expect(phraseText(conditionText({ not: { field: 'revenue', op: 'null' } }))).toBe('not (revenue null)');
    expect(phraseText(conditionText({ field: 'createdon', op: 'lt', value: { now: true } }))).toBe('createdon lt now');
  });

  it('read in Italian; names, values and YAML literals stay as written', () => {
    setUiLanguage(ITALIAN_LCID);
    expect(summaries()).toEqual([
      'Aggiorna record: correlato parentaccountid',
      'Se: revenue gt 1000',
      'Crea record: task',
      'Arresta: canceled',
      'Attendi finché tutte le 2 condizioni o per un timeout',
    ]);
    expect(phraseText(conditionText({ not: { field: 'revenue', op: 'null' } }))).toBe('non (revenue null)');
    expect(phraseText(conditionText({ field: 'createdon', op: 'lt', value: { now: true } }))).toBe('createdon lt adesso');
  });
});
