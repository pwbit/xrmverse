import { describe, expect, it } from 'vitest';
import { addElse, addElseIf, addOnTimeout, blockStyle, insertStep, keyOrder, moveStep, PlainText, removeStep, setAt, StudioEditError } from './edit';
import { STEP_KEYS } from './keys';
import { parseText } from './read';
import { stepTemplate } from './templates';
import type { StepType } from './types';

function lines(...text: string[]): string {
  return [...text, ''].join('\n');
}

const BACKGROUND = lines(
  'kind: workflow',
  'name: X',
  'entity: account',
  'mode: background',
  'trigger: { update: { columns: [a] } }',
  'steps:',
  '  # c1',
  '  - update:',
  '      record: primary # the primary record',
  '      set: { name: x }',
  '  - if:',
  '      when: { field: a, op: null }',
  '      then: []',
);

function parsed(text: string) {
  const result = parseText(text);
  expect(result.problems).toEqual([]);
  return result.doc;
}

/** The text of `doc` read back: problems must be none, and the model is returned. */
function reread(text: string) {
  const { model, problems } = parseText(text);
  expect(problems).toEqual([]);
  return model!;
}

describe('insertStep', () => {
  it('keeps comments and flow style', () => {
    const doc = parsed(BACKGROUND);
    const path = insertStep(doc, ['steps'], 1, 'stop');
    expect(path).toEqual(['steps', 1]);
    const text = doc.toString();
    expect(text).toContain('  # c1\n  - update:\n      record: primary # the primary record\n');
    expect(text).toContain('trigger: { update: { columns: [a] } }');
    expect(text).toContain('      set: { name: x }\n  - stop: { status: succeeded }\n  - if:\n');
    expect(reread(text).steps.map((step) => step.type)).toEqual(['update', 'stop', 'if']);
  });

  it('writes a step into an empty flow list as a block list', () => {
    const doc = parsed(BACKGROUND);
    expect(insertStep(doc, ['steps', 1, 'if', 'then'], 0, 'update')).toEqual(['steps', 1, 'if', 'then', 0]);
    expect(doc.toString()).toContain(lines('      then:', '        - update:', '            record: primary', '            set: {}'));
    expect(reread(doc.toString()).steps[1].branches[0].steps[0].type).toBe('update');
  });

  it('creates a missing branch list', () => {
    const doc = parsed(BACKGROUND);
    setAt(doc, ['steps', 1, 'if', 'then'], null);
    expect(doc.toString()).toContain('      then: ~\n');
    const path = insertStep(doc, ['steps', 1, 'if', 'then'], 0, 'stop');
    expect(path).toEqual(['steps', 1, 'if', 'then', 0]);
    expect(doc.toString()).toContain(lines('      then:', '        - stop: { status: succeeded }'));
  });

  it('refuses an index outside the list', () => {
    const doc = parsed(BACKGROUND);
    expect(() => insertStep(doc, ['steps'], 3, 'stop')).toThrow(StudioEditError);
    expect(() => insertStep(doc, ['steps', 0], 0, 'stop')).toThrow(StudioEditError);
  });
});

describe('removeStep', () => {
  it('removes the step and writes an emptied list as []', () => {
    const doc = parsed(BACKGROUND);
    insertStep(doc, ['steps', 1, 'if', 'then'], 0, 'stop');
    removeStep(doc, ['steps', 1, 'if', 'then', 0]);
    expect(doc.toString()).toBe(BACKGROUND);
    removeStep(doc, ['steps', 0]);
    expect(reread(doc.toString()).steps.map((step) => step.type)).toEqual(['if']);
  });

  it('refuses a path that is not a step', () => {
    const doc = parsed(BACKGROUND);
    expect(() => removeStep(doc, ['steps', 5])).toThrow(StudioEditError);
    expect(() => removeStep(doc, ['trigger'])).toThrow(StudioEditError);
  });
});

describe('moveStep', () => {
  it('insert/remove/move round-trip', () => {
    const doc = parsed(BACKGROUND);
    const moved = moveStep(doc, ['steps', 0], ['steps', 1, 'if', 'then'], 0);
    // Removing steps[0] moves the if to steps[0], so the returned path follows it.
    expect(moved).toEqual(['steps', 0, 'if', 'then', 0]);
    const inside = reread(doc.toString());
    expect(inside.steps.map((step) => step.type)).toEqual(['if']);
    expect(inside.steps[0].branches[0].steps[0].type).toBe('update');
    expect(doc.toString()).toContain('# the primary record');

    expect(moveStep(doc, moved, ['steps'], 0)).toEqual(['steps', 0]);
    expect(doc.toString()).toBe(BACKGROUND);

    const inserted = insertStep(doc, ['steps'], 2, 'stop');
    removeStep(doc, inserted);
    expect(doc.toString()).toBe(BACKGROUND);
  });

  it('moves down within the same list by the slot index before the move', () => {
    const doc = parsed(BACKGROUND);
    insertStep(doc, ['steps'], 2, 'stop');
    // Slot 2 is between the if and the stop: the update lands after the if.
    expect(moveStep(doc, ['steps', 0], ['steps'], 2)).toEqual(['steps', 1]);
    expect(reread(doc.toString()).steps.map((step) => step.type)).toEqual(['if', 'update', 'stop']);
    expect(moveStep(doc, ['steps', 2], ['steps'], 0)).toEqual(['steps', 0]);
    expect(reread(doc.toString()).steps.map((step) => step.type)).toEqual(['stop', 'if', 'update']);
  });

  it('moveStep follows the place rule: a business rule action cannot leave its if (the document is unchanged)', () => {
    const rule = lines(
      'kind: businessRule',
      'name: R',
      'entity: account',
      'steps:',
      '  - if:',
      '      when: { field: name, op: null }',
      '      then:',
      '        - setValue: { field: name, value: X }',
      '      else: []',
    );
    const doc = parsed(rule);
    expect(() => moveStep(doc, ['steps', 0, 'if', 'then', 0], ['steps'], 0)).toThrow(StudioEditError);
    expect(() => moveStep(doc, ['steps', 0, 'if', 'then', 0], ['steps'], 1)).toThrow(StudioEditError);
    expect(doc.toString()).toBe(rule);
    // A valid move (into the else of the same if) still works, and the reader takes the result.
    expect(moveStep(doc, ['steps', 0, 'if', 'then', 0], ['steps', 0, 'if', 'else'], 0)).toEqual(['steps', 0, 'if', 'else', 0]);
    expect(reread(doc.toString()).steps[0].branches.map((b) => b.steps.map((step) => step.type))).toEqual([[], ['setValue']]);
    // An if moves anywhere with the actions inside it.
    insertStep(doc, ['steps'], 1, 'if');
    expect(moveStep(doc, ['steps', 0], ['steps', 1, 'if', 'then'], 0)).toEqual(['steps', 0, 'if', 'then', 0]);
    expect(reread(doc.toString()).steps[0].branches[0].steps[0].branches[1].steps[0].type).toBe('setValue');
  });

  it('moveStep refuses a wait moved into a real-time workflow place, insertStep a step the place does not take', () => {
    const doc = parsed(lines('kind: workflow', 'name: X', 'entity: account', 'mode: realtime', 'trigger: { create: true }', 'steps: []'));
    const before = doc.toString();
    expect(() => insertStep(doc, ['steps'], 0, 'wait')).toThrow(StudioEditError);
    expect(() => insertStep(doc, ['steps'], 0, 'setValue')).toThrow(StudioEditError);
    expect(doc.toString()).toBe(before);
  });

  it('moveStep refuses moving a step into its own branch', () => {
    const doc = parsed(BACKGROUND);
    const before = doc.toString();
    expect(() => moveStep(doc, ['steps', 1], ['steps', 1, 'if', 'then'], 0)).toThrow(StudioEditError);
    insertStep(doc, ['steps', 1, 'if', 'then'], 0, 'if');
    expect(() => moveStep(doc, ['steps', 1], ['steps', 1, 'if', 'then', 0, 'if', 'then'], 0)).toThrow(StudioEditError);
    removeStep(doc, ['steps', 1, 'if', 'then', 0]);
    expect(doc.toString()).toBe(before);
  });
});

describe('setAt', () => {
  it('setAt deletes on undefined', () => {
    const doc = parsed(BACKGROUND);
    setAt(doc, ['steps', 0, 'update', 'set', 'name'], undefined);
    expect(doc.toString()).toContain('      set: {}\n');
    setAt(doc, ['trigger'], undefined);
    expect(doc.toString()).not.toContain('trigger');
    // Deleting a key that is not there is a no-op.
    setAt(doc, ['steps', 0, 'update', 'description'], undefined);
    expect(reread(doc.toString()).steps[0].type).toBe('update');
  });

  it('keeps a comment and the flow style of the value it replaces', () => {
    const doc = parsed(BACKGROUND);
    setAt(doc, ['steps', 0, 'update', 'record'], { related: 'parentaccountid' });
    expect(doc.toString()).toContain('      record: { related: parentaccountid } # the primary record\n');
    setAt(doc, ['steps', 0, 'update', 'set', 'name'], 'y');
    setAt(doc, ['steps', 0, 'update', 'set', 'description'], { field: 'name' });
    expect(doc.toString()).toContain('      set: { name: y, description: { field: name } }\n');
    const text = parseText(lines('kind: workflow', 'name: X # the name', 'entity: account', 'mode: realtime', 'steps: []'));
    setAt(text.doc, ['name'], 'Y');
    expect(text.doc.toString()).toContain('name: Y # the name\n');
  });

  it('writes null as the empty value and numbers and booleans as written', () => {
    const doc = parsed(BACKGROUND);
    setAt(doc, ['steps', 0, 'update', 'set'], { name: null, revenue: 10, donotemail: true });
    expect(doc.toString()).toContain('      set: { name: ~, revenue: 10, donotemail: true }\n');
  });

  // The failsafe schema keeps every scalar as text; a string the 2.21 server's writer quotes (YamlText.IsPlainSafe) must be
  // quoted here too, or it reads back as the empty value or as other text.
  it.each([
    ['', "''"],
    ['null', "'null'"],
    ['~', "'~'"],
    ['NULL', "'NULL'"],
    ['a: b', "'a: b'"],
    ['#x', "'#x'"],
    ['- x', "'- x'"],
    [' lead', "' lead'"],
    ['trail ', "'trail '"],
    ['back\\slash', "'back\\slash'"],
    ["it's", "it's"],
    ["'q'", "'''q'''"],
    ['a, b', "'a, b'"],
    ['x #y', "'x #y'"],
    ['---', "'---'"],
    ['two\nlines', '"two\\nlines"'],
    ['tab\there', '"tab\\there"'],
    ['true', 'true'],
    ['false', 'false'],
    ['1', '1'],
    ['0123', '0123'],
    ['1e3', '1e3'],
    ['2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z'],
    ['https://example.com/a', 'https://example.com/a'],
  ])('writes %j as %s and reads it back as the same text', (value, written) => {
    const doc = parsed(BACKGROUND);
    setAt(doc, ['steps', 0, 'update', 'set', 'name'], value);
    setAt(doc, ['steps', 0, 'update', 'description'], value);
    const text = doc.toString();
    expect(text).toContain(`      set: { name: ${written} }\n`);
    const back = parseText(text);
    expect(back.doc.getIn(['steps', 0, 'update', 'set', 'name'])).toBe(value);
    expect(back.doc.getIn(['steps', 0, 'update', 'description'])).toBe(value);
    expect(back.problems).toEqual([]);
  });

  it('creates the missing mappings on the way', () => {
    const doc = parsed(BACKGROUND);
    setAt(doc, ['steps', 0, 'update', 'set'], undefined);
    setAt(doc, ['steps', 0, 'update', 'set', 'name'], 'x');
    expect(doc.toString()).toContain('      record: primary # the primary record\n      set: { name: x }\n');
    setAt(doc, ['steps', 0, 'update', 'set'], null);
    setAt(doc, ['steps', 0, 'update', 'set', 'a', 'b'], 'c');
    expect(doc.toString()).toContain('      set: { a: { b: c } }\n');
    // Deleting under a missing mapping is a no-op.
    setAt(doc, ['steps', 1, 'if', 'missing', 'x'], undefined);
    expect(reread(doc.toString()).steps.map((step) => step.type)).toEqual(['update', 'if']);
  });

  it('writes a new key with the quoting rule', () => {
    const doc = parsed(BACKGROUND);
    setAt(doc, ['steps', 0, 'update', 'set', 'null'], 'x');
    setAt(doc, ['steps', 0, 'update', 'set', 'a: b'], '~');
    expect(doc.toString()).toContain("      set: { name: x, 'null': x, 'a: b': '~' }\n");
    const back = parseText(doc.toString());
    expect(back.problems).toEqual([]);
    expect(back.doc.getIn(['steps', 0, 'update', 'set', 'null'])).toBe('x');
  });

  it('puts a new key at its place in the writer order', () => {
    const doc = parsed(BACKGROUND);
    setAt(doc, ['steps', 0, 'update', 'description'], 'First');
    setAt(doc, ['description'], 'About');
    setAt(doc, ['scope'], 'organization');
    setAt(doc, ['onDemand'], 'true');
    setAt(doc, ['trigger', 'create'], 'true');
    setAt(doc, ['trigger', 'update', 'stage'], 'pre');
    const text = doc.toString();
    expect(text).toContain(
      lines(
        'kind: workflow',
        'name: X',
        'description: About',
        'entity: account',
        'mode: background',
        'scope: organization',
        'onDemand: true',
        'trigger: { create: true, update: { columns: [a], stage: pre } }',
        'steps:',
        '  # c1',
        '  - update:',
        '      description: First',
        '      record: primary # the primary record',
      ),
    );
    expect(keyOrder(['steps', 1, 'if', 'then', 0, 'stop'])).toEqual(['status', 'message', 'description']);
    expect(keyOrder(['steps', 0, 'update', 'set'])).toBeUndefined();
  });

  it('appends to a list at the index after its end, and no further', () => {
    const doc = parsed(BACKGROUND);
    setAt(doc, ['trigger', 'update', 'columns', 1], 'b');
    expect(doc.toString()).toContain('trigger: { update: { columns: [a, b] } }');
    expect(() => setAt(doc, ['trigger', 'update', 'columns', 3], 'c')).toThrow(StudioEditError);
  });

  it('writes a PlainText as written: the operator null', () => {
    const doc = parsed(BACKGROUND);
    setAt(doc, ['steps', 1, 'if', 'when'], { field: 'name', op: new PlainText('null') });
    setAt(doc, ['steps', 0, 'update', 'set', 'name'], 'null');
    const text = doc.toString();
    expect(text).toContain("      when: { field: name, op: null }\n");
    expect(text).toContain("set: { name: 'null' }");
    setAt(doc, ['steps', 1, 'if', 'when', 'op'], new PlainText('null'));
    expect(doc.toString()).toBe(text);
  });

  it('blockStyle writes a set as a block mapping, or {} when empty', () => {
    const doc = parsed(BACKGROUND);
    blockStyle(doc, ['steps', 0, 'update', 'set']);
    expect(doc.toString()).toContain(lines('      record: primary # the primary record', '      set:', '        name: x'));
    setAt(doc, ['steps', 0, 'update', 'set', 'name'], undefined);
    blockStyle(doc, ['steps', 0, 'update', 'set']);
    expect(doc.toString()).toContain('      set: {}\n');
    // Inside a flow collection a block is not possible: the style stays.
    blockStyle(doc, ['trigger', 'update', 'columns']);
    expect(doc.toString()).toContain('trigger: { update: { columns: [a] } }');
  });

  it('refuses a path through a list index that does not exist', () => {
    const doc = parsed(BACKGROUND);
    expect(() => setAt(doc, ['steps', 4, 'update', 'record'], 'primary')).toThrow(StudioEditError);
    expect(doc.toString()).toBe(BACKGROUND);
  });
});

describe('branches', () => {
  it('addElseIf and addElse add branches in the format order', () => {
    const doc = parsed(BACKGROUND);
    expect(addElse(doc, ['steps', 1])).toEqual(['steps', 1, 'if', 'else']);
    expect(addElseIf(doc, ['steps', 1])).toEqual(['steps', 1, 'if', 'elseIf', 0, 'then']);
    expect(addElseIf(doc, ['steps', 1])).toEqual(['steps', 1, 'if', 'elseIf', 1, 'then']);
    expect(doc.toString()).toContain(
      lines(
        '  - if:',
        '      when: { field: a, op: null }',
        '      then: []',
        '      elseIf:',
        '        - when: { all: [] }',
        '          then: []',
        '        - when: { all: [] }',
        '          then: []',
        '      else: []',
      ),
    );
    insertStep(doc, ['steps', 1, 'if', 'elseIf', 1, 'then'], 0, 'stop');
    const model = reread(doc.toString());
    expect(model.steps[1].branches.map((branch) => [branch.label, branch.path])).toEqual([
      ['then', ['steps', 1, 'if', 'then']],
      ['elseIf', ['steps', 1, 'if', 'elseIf', 0, 'then']],
      ['elseIf', ['steps', 1, 'if', 'elseIf', 1, 'then']],
      ['else', ['steps', 1, 'if', 'else']],
    ]);
    // An else that is already there is returned as it is.
    expect(addElse(doc, ['steps', 1])).toEqual(['steps', 1, 'if', 'else']);
  });

  it('addOnTimeout adds the branch after then', () => {
    const doc = parsed(BACKGROUND);
    insertStep(doc, ['steps'], 0, 'wait');
    expect(() => addOnTimeout(doc, ['steps', 0])).toThrow(StudioEditError);
    setAt(doc, ['steps', 0, 'wait', 'description'], 'Wait');
    setAt(doc, ['steps', 0, 'wait', 'timeout'], { field: 'lastusedincampaign' });
    expect(addOnTimeout(doc, ['steps', 0])).toEqual(['steps', 0, 'wait', 'onTimeout']);
    expect(doc.toString()).toContain(
      lines(
        '  - wait:',
        '      description: Wait',
        '      until: { all: [] }',
        '      timeout: { field: lastusedincampaign }',
        '      then: []',
        '      onTimeout: []',
      ),
    );
    expect(reread(doc.toString()).steps[0].branches.map((branch) => branch.label)).toEqual(['then', 'onTimeout']);
  });

  it('refuses a step of the wrong type', () => {
    const doc = parsed(BACKGROUND);
    expect(() => addElse(doc, ['steps', 0])).toThrow(StudioEditError);
    expect(() => addOnTimeout(doc, ['steps', 1])).toThrow(StudioEditError);
  });
});

describe('stepTemplate', () => {
  const host: Record<string, string> = {
    workflow: lines('kind: workflow', 'name: X', 'entity: account', 'mode: background', 'steps: []'),
    action: lines('kind: action', 'name: X', 'entity: account', 'message: x_Do', 'steps: []'),
    businessRule: lines('kind: businessRule', 'name: X', 'entity: account', 'steps:', '  - if:', '      when: { all: [] }', '      then: []'),
  };

  it.each(Object.keys(STEP_KEYS) as StepType[])('the %s template reads without problems', (type) => {
    const rule = ['setValue', 'setDefault', 'clearValue', 'showError', 'setVisibility', 'setLock', 'setRequirement'].includes(type);
    const { doc } = parseText(rule ? host.businessRule : type === 'setOutput' ? host.action : host.workflow);
    const path = rule ? insertStep(doc, ['steps', 0, 'if', 'then'], 0, type) : insertStep(doc, ['steps'], 0, type);
    const model = reread(doc.toString());
    const step = rule ? model.steps[0].branches[0].steps[0] : model.steps[0];
    expect(step).toMatchObject({ type, path });
    expect(Object.keys(stepTemplate(type) as object)).toEqual([type]);
  });

  it('writes the canonical skeletons', () => {
    expect(stepTemplate('update')).toEqual({ update: { record: 'primary', set: {} } });
    expect(stepTemplate('if')).toEqual({ if: { when: { all: [] }, then: [] } });
    expect(stepTemplate('wait')).toEqual({ wait: { until: { all: [] }, then: [] } });
    // A fresh object each time: a caller may change it.
    expect(stepTemplate('if')).not.toBe(stepTemplate('if'));
  });
});
