import { readdirSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { describe, expect, it } from 'vitest';
import { parseDocument, type YAMLSeq } from 'yaml';
import { phraseText } from './phrase';
import { parseText, readModel } from './read';
import type { StepNode } from './types';

/** A step with its summary as English text. */
const read = (step: StepNode) => ({ ...step, summary: phraseText(step.summary) });

const repo = join(__dirname, '../../../..');
const fixtures = join(repo, 'tests/fixtures/workflows/yaml');
const fixtureFiles = readdirSync(fixtures).filter((name) => name.endsWith('.yaml'));
const docExamples = [...readFileSync(join(repo, 'docs/workflow-authoring.md'), 'utf8').matchAll(/```yaml\n([\s\S]*?)```/g)].map(
  (match) => match[1],
);

function fixture(name: string): string {
  return readFileSync(join(fixtures, name), 'utf8');
}

describe('parseText over the 2.21 fixtures', () => {
  it('finds the five fixtures', () => {
    expect(fixtureFiles.sort()).toEqual(['business-rule.yaml', 'child.yaml', 'oncreate.yaml', 'onupdate.yaml', 'recalculate.yaml']);
  });

  it.each(fixtureFiles)('%s reads without problems and keeps its text', (name) => {
    const text = fixture(name);
    const { doc, model, problems } = parseText(text);
    expect(problems).toEqual([]);
    expect(model).toBeDefined();
    expect(doc.toString()).toBe(text);
  });

  it('reads oncreate: a real-time workflow with create and startChild', () => {
    const { model } = parseText(fixture('oncreate.yaml'));
    expect(model).toMatchObject({ kind: 'workflow', id: '3e1f0307-d8de-f111-84e8-002248872b66', name: 'OnCreate', entity: 'xrmv_order', mode: 'realtime' });
    expect(model!.trigger).toEqual({ create: 'true' });
    expect(model!.header).toMatchObject({ scope: 'organization' });
    expect(model!.steps.map((step) => step.type)).toEqual(['create', 'startChild']);
    expect(read(model!.steps[0])).toMatchObject({ path: ['steps', 0], line: 10, summary: 'Create record: task', branches: [] });
    expect(read(model!.steps[1])).toMatchObject({ path: ['steps', 1], line: 16, summary: 'Start child workflow: Child' });
  });

  it('reads onupdate: one wait with a sendEmail in then', () => {
    const { model } = parseText(fixture('onupdate.yaml'));
    expect(model).toMatchObject({ kind: 'workflow', mode: 'background' });
    expect(model!.steps).toHaveLength(1);
    const wait = model!.steps[0];
    expect(wait.type).toBe('wait');
    expect(phraseText(wait.summary)).toBe('Wait until xrmv_amount gt 100');
    expect(wait.branches).toHaveLength(1);
    const then = wait.branches[0];
    expect(then).toMatchObject({ label: 'then', path: ['steps', 0, 'wait', 'then'] });
    expect(then.when).toEqual({ field: 'xrmv_amount', op: 'gt', value: '100' });
    expect(then.steps.map((step) => step.type)).toEqual(['sendEmail']);
    expect(read(then.steps[0])).toMatchObject({ path: ['steps', 0, 'wait', 'then', 0], line: 14, summary: 'Send email' });
  });

  it('reads business-rule: an if with rule actions in then and else', () => {
    const { model } = parseText(fixture('business-rule.yaml'));
    expect(model).toMatchObject({ kind: 'businessRule', name: 'New business rule' });
    expect(model!.mode).toBeUndefined();
    expect(model!.header).toMatchObject({ description: 'Click to add description' });
    const condition = model!.steps[0];
    expect(read(condition)).toMatchObject({ type: 'if', description: 'New Condition', summary: 'If: xrmv_amount null' });
    expect(condition.branches.map((branch) => [branch.label, branch.path])).toEqual([
      ['then', ['steps', 0, 'if', 'then']],
      ['else', ['steps', 0, 'if', 'else']],
    ]);
    expect(condition.branches[0].when).toEqual({ field: 'xrmv_amount', op: 'null' });
    expect(read(condition.branches[0].steps[0])).toMatchObject({
      type: 'setValue',
      path: ['steps', 0, 'if', 'then', 0],
      description: 'New Action',
      summary: 'Set value: xrmv_status',
    });
    expect(condition.branches[1].steps[0]).toMatchObject({ type: 'showError', path: ['steps', 0, 'if', 'else', 0] });
  });

  it('reads child and recalculate', () => {
    expect(parseText(fixture('child.yaml')).model!.steps.map((step) => phraseText(step.summary))).toEqual(['Stop: canceled']);
    const recalculate = parseText(fixture('recalculate.yaml')).model!;
    expect(recalculate.kind).toBe('action');
    expect(recalculate.header).toMatchObject({ message: 'xrmv_Recalculate' });
    expect(recalculate.steps.map((step) => phraseText(step.summary))).toEqual(['Update record: primary', 'Set output: Total', 'If: Increment lt 0']);
  });
});

describe('canonical style', () => {
  it('writes flow sequences without padding and flow mappings with it, also after an edit', () => {
    const { doc } = parseText(fixture('onupdate.yaml'));
    (doc.getIn(['trigger', 'update', 'columns']) as YAMLSeq).add('xrmv_amount');
    const added = doc.createNode(['a', { field: 'b' }]);
    added.flow = true;
    doc.setIn(['trigger', 'update', 'extra'], added);
    const text = doc.toString();
    expect(text).toContain('  update: { columns: [xrmv_status, xrmv_amount], extra: [a, { field: b }] }\n');
    expect(text).toContain('transactioncurrencyid: { lookup: transactioncurrency, id: 692363f0-a3da-f111-84e8-3833c5c48caf, name: Euro }\n');
  });
});

describe('literals', () => {
  const text = [
    'kind: workflow',
    'name: X',
    'entity: account',
    'mode: realtime',
    'trigger:',
    '  create: true',
    'steps:',
    "  - create: { entity: task, set: { a: 0123, b: 1e3, c: +1, d: .5, e: 9007199254740993, f: ~, g: null, h: true, i: 'null', j: 2026-01-01, k: 0x1F } }",
    '',
  ].join('\n');

  it('keeps every literal as written through an unrelated edit', () => {
    const { doc, problems } = parseText(text);
    expect(problems).toEqual([]);
    doc.setIn(['name'], 'Y');
    expect(doc.toString()).toBe(text.replace('name: X', 'name: Y'));
  });

  it('holds the source text of every literal in the model', () => {
    const { doc } = parseText(text);
    expect(doc.getIn(['steps', 0, 'create', 'set'])).toBeDefined();
    const { model } = parseText(text.replace('steps:', 'onDemand: true\nsteps:'));
    expect(model!.header).toMatchObject({ onDemand: 'true', name: 'X' });
    expect(model!.trigger).toEqual({ create: 'true' });
  });

  it('reads an empty value by its text, not a quoted null', () => {
    expect(parseText('kind: workflow\nname: ~\nentity: a\nmode: realtime\nsteps: []\n').problems).toEqual([
      { message: "The workflow needs the key 'name'.", line: 1, column: 1 },
    ]);
    expect(parseText("kind: workflow\nname: 'null'\nentity: a\nmode: realtime\nsteps: []\n").problems).toEqual([]);
    expect(parseText("kind: workflow\nname: X\nentity: a\nmode: realtime\ntrigger: { create: 'true' }\nsteps: []\n").problems[0].message).toBe(
      "'create' must be true or { stage: pre | post }.",
    );
  });
});

describe('readModel', () => {
  it('reads a document parsed elsewhere with positions counted over its text', () => {
    const doc = parseDocument('kind: workflow\nname: X\nentity: a\nmode: realtime\nsteps:\n  - stop: { status: succeeded }\n  - bar: 1\n');
    const { model, problems } = readModel(doc);
    expect(model).toBeUndefined();
    expect(problems).toEqual([expect.objectContaining({ line: 7, column: 5 })]);
  });
});

describe('parseText over the documented examples', () => {
  it('finds the examples', () => {
    expect(docExamples.length).toBeGreaterThanOrEqual(4);
  });

  it.each(docExamples.map((text, index) => [index, text] as const))('example %i has no problems', (_, text) => {
    const { model, problems } = parseText(text);
    expect(problems).toEqual([]);
    expect(model).toBeDefined();
  });
});

describe('parseText shape problems', () => {
  it('reports an unknown key at its line and column', () => {
    const { model, problems } = parseText('kind: workflow\nname: X\nfoo: 1\nentity: account\nmode: realtime\nsteps: []\n');
    expect(problems).toHaveLength(1);
    expect(problems[0]).toMatchObject({ line: 3, column: 1 });
    expect(problems[0].message).toContain("'foo'");
    expect(model).toBeUndefined();
  });

  it('reports a key of another kind', () => {
    const { problems } = parseText('kind: businessRule\nname: X\nentity: account\nmode: realtime\nsteps: []\n');
    expect(problems).toEqual([{ message: "The key 'mode' is only valid in a workflow, not in a business rule.", line: 4, column: 1 }]);
  });

  it('refuses a wait in a real-time workflow', () => {
    const text = 'kind: workflow\nname: X\nentity: account\nmode: realtime\nsteps:\n  - wait:\n      timeout: 1\n      then: []\n';
    const { problems } = parseText(text);
    expect(problems).toHaveLength(1);
    expect(problems[0]).toMatchObject({ line: 6, column: 5 });
    expect(problems[0].message).toContain('background workflow');
  });

  it('refuses a rule action outside an if in a business rule', () => {
    const text = 'kind: businessRule\nname: X\nentity: account\nsteps:\n  - setValue: { field: a, value: 1 }\n';
    const { problems } = parseText(text);
    expect(problems).toHaveLength(1);
    expect(problems[0]).toMatchObject({ line: 5, column: 5 });
    expect(problems[0].message).toContain('inside an if');
  });

  it('refuses workflow steps in a business rule and rule actions elsewhere', () => {
    const rule = parseText('kind: businessRule\nname: X\nentity: account\nsteps:\n  - update: { record: primary, set: {} }\n');
    expect(rule.problems[0].message).toContain("'update' cannot be used in a business rule");
    const workflow = parseText('kind: workflow\nname: X\nentity: account\nmode: background\nsteps:\n  - clearValue: { field: a }\n  - setOutput: { argument: a, value: 1 }\n');
    expect(workflow.problems.map((problem) => problem.line)).toEqual([6, 7]);
  });

  it('refuses elseIf and else without a when branch', () => {
    const text = 'kind: action\nname: X\nentity: account\nmessage: x_Do\nsteps:\n  - if:\n      else:\n        - stop: { status: canceled }\n';
    const { problems } = parseText(text);
    expect(problems.some((problem) => problem.message.includes("'when'") && problem.line === 7)).toBe(true);
  });

  it('refuses unknown step keys and missing required keys', () => {
    const text = 'kind: workflow\nname: X\nentity: account\nmode: realtime\nsteps:\n  - create: { entity: task, colour: red }\n';
    const { problems } = parseText(text);
    expect(problems.map((problem) => problem.message)).toEqual([
      "Unknown key 'colour' in a create step (allowed: entity, set, id, description).",
      "A create step needs the key 'set'.",
    ]);
  });

  it('reports a YAML syntax error from the parser with its position', () => {
    const { model, problems } = parseText('kind: workflow\nname: [unclosed\nentity: account\n');
    expect(model).toBeUndefined();
    expect(problems.length).toBeGreaterThan(0);
    for (const problem of problems) {
      expect(problem.line).toBeGreaterThan(0);
      expect(problem.column).toBeGreaterThan(0);
      expect(problem.message).not.toContain('\n');
    }
  });

  it('reports a duplicate key', () => {
    const { problems } = parseText('kind: workflow\nname: X\nname: Y\nentity: a\nmode: realtime\nsteps: []\n');
    expect(problems).toHaveLength(1);
    expect(problems[0].line).toBe(3);
  });

  it('refuses anchors and aliases', () => {
    const { problems } = parseText('kind: workflow\nname: &n X\nentity: *n\nmode: realtime\nsteps: []\n');
    expect(problems.length).toBeGreaterThan(0);
    expect(problems[0].message).toContain('Anchors and aliases');
  });

  it('refuses an empty document', () => {
    expect(parseText('').problems).toEqual([{ message: 'The workflow is empty.', line: 1, column: 1 }]);
  });
});

describe('branch paths', () => {
  it('addresses elseIf branches and nested steps', () => {
    const text = [
      'kind: action',
      'name: X',
      'entity: account',
      'message: x_Do',
      'steps:',
      '  - stop: { status: succeeded }',
      '  - if:',
      '      when: { field: revenue, op: gt, value: 1000 }',
      '      then: []',
      '      elseIf:',
      '        - when: { field: revenue, op: gt, value: 10 }',
      '          then: []',
      '        - when: { all: [{ field: a, op: null }, { field: b, op: notNull }] }',
      '          then:',
      '            - stop: { status: canceled }',
      '      else: []',
      '',
    ].join('\n');
    const { model, problems } = parseText(text);
    expect(problems).toEqual([]);
    expect(phraseText(model!.steps[0].summary)).toBe('Stop: succeeded');
    const condition = model!.steps[1];
    expect(phraseText(condition.summary)).toBe('If: revenue gt 1000');
    expect(condition.branches.map((branch) => [branch.label, branch.path])).toEqual([
      ['then', ['steps', 1, 'if', 'then']],
      ['elseIf', ['steps', 1, 'if', 'elseIf', 0, 'then']],
      ['elseIf', ['steps', 1, 'if', 'elseIf', 1, 'then']],
      ['else', ['steps', 1, 'if', 'else']],
    ]);
    expect(condition.branches[2].steps[0]).toMatchObject({ path: ['steps', 1, 'if', 'elseIf', 1, 'then', 0], line: 15 });
  });

  it('addresses a wait onTimeout branch', () => {
    const text = [
      'kind: workflow',
      'name: X',
      'entity: account',
      'mode: background',
      'steps:',
      '  - wait:',
      '      timeout: { add: [{ now: true }, 3] }',
      '      then: []',
      '      onTimeout:',
      '        - stop: { status: canceled }',
      '',
    ].join('\n');
    const { model, problems } = parseText(text);
    expect(problems).toEqual([]);
    const wait = model!.steps[0];
    expect(phraseText(wait.summary)).toBe('Wait for a timeout');
    expect(wait.branches.map((branch) => [branch.label, branch.path, branch.when])).toEqual([
      ['then', ['steps', 0, 'wait', 'then'], undefined],
      ['onTimeout', ['steps', 0, 'wait', 'onTimeout'], undefined],
    ]);
  });
});
