import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { describe, expect, it } from 'vitest';
import { KIND_KEYS, REFERENCE_KEYS, STEP_KEYS, STEP_KINDS, TOP_KEYS } from './keys';

const docs = readFileSync(join(__dirname, '../../../../docs/workflow-authoring.md'), 'utf8');

/** The first-column keys of the generated reference table of docs/workflow-authoring.md. */
function documentedKeys(): string[] {
  const start = docs.indexOf('<!-- workflow-yaml:start -->');
  const end = docs.indexOf('<!-- workflow-yaml:end -->');
  expect(start).toBeGreaterThan(0);
  expect(end).toBeGreaterThan(start);
  return docs
    .slice(start, end)
    .split('\n')
    .map((line) => /^\| `([^`]+)` \|/.exec(line)?.[1])
    .filter((key): key is string => key !== undefined);
}

describe('workflow YAML keys', () => {
  it('matches the generated reference', () => {
    const keys = documentedKeys();
    expect(keys.length).toBeGreaterThan(30);
    expect([...REFERENCE_KEYS].sort()).toEqual([...keys].sort());
  });

  it('has a step key list and a kind list for every step of the reference', () => {
    const steps = REFERENCE_KEYS.filter((key) => /^steps\[\]\.[a-zA-Z]+$/.test(key)).map((key) => key.slice('steps[].'.length));
    expect(Object.keys(STEP_KEYS).sort()).toEqual([...steps].sort());
    expect(Object.keys(STEP_KINDS).sort()).toEqual([...steps].sort());
    for (const keys of Object.values(STEP_KEYS)) expect(keys).toContain('description');
  });

  it('has every top-level key of the reference in TOP_KEYS or KIND_KEYS', () => {
    const topLevel = new Set(
      REFERENCE_KEYS.filter((key) => !key.startsWith('steps[]') && !['record', 'value', 'condition'].includes(key)).map(
        (key) => key.split('.')[0],
      ),
    );
    const ours = new Set([...TOP_KEYS, ...Object.values(KIND_KEYS).flat()]);
    expect([...ours].sort()).toEqual([...topLevel].sort());
  });

  it('places steps by kind as the format says', () => {
    expect(STEP_KINDS.wait).toEqual(['workflow']);
    expect(STEP_KINDS.setOutput).toEqual(['action']);
    expect([...STEP_KINDS.if].sort()).toEqual(['action', 'businessRule', 'workflow']);
    expect(STEP_KINDS.setValue).toEqual(['businessRule']);
    expect(STEP_KINDS.update).not.toContain('businessRule');
  });
});
