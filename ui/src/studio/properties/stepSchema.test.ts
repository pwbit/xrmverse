import { describe, expect, it } from 'vitest';
import { STEP_KEYS } from '../model/keys';
import { parseText } from '../model/read';
import type { StepType } from '../model/types';
import { earlierSteps, findStep, STEP_SCHEMA } from './stepSchema';

const TEXT = [
  'kind: workflow',
  'name: Flow',
  'entity: account',
  'mode: background',
  'steps:',
  '  - create: { id: first, entity: task, set: { subject: A } }', //   steps.0
  '  - performAction: { id: act, message: new_Do }', //                steps.1
  '  - if:', //                                                         steps.2
  '      when: { field: name, op: notNull }',
  '      then:',
  '        - create: { id: inThen, entity: contact, set: { lastname: B } }', // steps.2.if.then.0
  '        - update: { record: primary, set: { name: C } }', //                steps.2.if.then.1
  '      else:',
  '        - sendEmail: { id: inElse, set: { subject: D } }', //               steps.2.if.else.0
  '  - update: { record: primary, set: { name: E } }', //               steps.3
  '  - create: { id: last, entity: task, set: { subject: F } }', //    steps.4
  '',
].join('\n');

describe('STEP_SCHEMA', () => {
  it.each(Object.keys(STEP_KEYS) as StepType[])('%s has a field for every key of the format, and no other', (type) => {
    expect(STEP_SCHEMA[type].map((spec) => spec.key).sort()).toEqual([...STEP_KEYS[type]].sort());
  });

  it('marks the required keys and lists the enums', () => {
    expect(STEP_SCHEMA.create.filter((spec) => spec.required).map((spec) => spec.key)).toEqual(['entity', 'set']);
    expect(STEP_SCHEMA.stop.find((spec) => spec.key === 'status')).toEqual({ key: 'status', kind: 'enum', required: true, options: ['succeeded', 'canceled'] });
    expect(STEP_SCHEMA.setRequirement.find((spec) => spec.key === 'level')?.options).toEqual(['none', 'recommended', 'required']);
    expect(STEP_SCHEMA.if.find((spec) => spec.key === 'when')?.kind).toBe('condition');
  });
});

describe('earlierSteps', () => {
  const model = parseText(TEXT).model!;

  it('lists the steps with an id before the step on its path', () => {
    expect(earlierSteps(model, ['steps', 3])).toEqual([
      { id: 'first', type: 'create', entity: 'task' },
      { id: 'act', type: 'performAction' },
    ]);
  });

  it('leaves out the steps of sibling branches and the later steps', () => {
    // Inside then: the steps before the if, and the earlier step of its own branch; not the else branch, not later steps.
    expect(earlierSteps(model, ['steps', 2, 'if', 'then', 1]).map((s) => s.id)).toEqual(['first', 'act', 'inThen']);
    expect(earlierSteps(model, ['steps', 2, 'if', 'else', 0]).map((s) => s.id)).toEqual(['first', 'act']);
    // After the if: nothing from inside its branches (they may not have run).
    expect(earlierSteps(model, ['steps', 4]).map((s) => s.id)).toEqual(['first', 'act']);
    expect(earlierSteps(model, ['steps', 0])).toEqual([]);
    expect(earlierSteps(model, ['steps', 2, 'if', 'else', 0]).find((s) => s.id === 'inElse')).toBeUndefined();
  });

  it('reads sendEmail as a created email', () => {
    const other = parseText(TEXT.replace('  - update: { record: primary, set: { name: E } }', '  - sendEmail: { id: mail, set: {} }')).model!;
    expect(earlierSteps(other, ['steps', 4]).at(-1)).toEqual({ id: 'mail', type: 'sendEmail', entity: 'email' });
  });

  it('findStep finds a step by its path, or nothing', () => {
    expect(findStep(model, ['steps', 2, 'if', 'else', 0])?.type).toBe('sendEmail');
    expect(findStep(model, ['steps', 9])).toBeUndefined();
    expect(findStep(model, ['steps', 2, 'if', 'then', 5])).toBeUndefined();
  });
});
