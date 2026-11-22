import { describe, expect, it } from 'vitest';
import { parseText } from '../model/read';
import { collectProblems, problemsByLine, stepAtLine } from './problems';

const TEXT = [
  'kind: workflow', // 1
  'name: X', // 2
  'entity: account', // 3
  'mode: background', // 4
  'trigger: { create: true }', // 5
  'steps:', // 6
  '  - update:', // 7
  '      record: primary', // 8
  '      set: { name: x }', // 9
  '  - if:', // 10
  '      when: { field: name, op: null }', // 11
  '      then:', // 12
  '        - stop: { status: succeeded }', // 13
  '      else:', // 14
  '        - update:', // 15
  '            record: primary', // 16
  '            set: { name: y }', // 17
  '  - stop: { status: canceled }', // 18
  '',
].join('\n');

describe('stepAtLine', () => {
  const { model, problems } = parseText(TEXT);

  it('reads the fixture', () => expect(problems).toEqual([]));

  it.each([
    [2, 'trigger'],
    [5, 'trigger'],
    [8, ['steps', 0]],
    [11, ['steps', 1]],
    [13, ['steps', 1, 'if', 'then', 0]],
    [16, ['steps', 1, 'if', 'else', 0]],
    [18, ['steps', 2]],
  ])('line %s is in %j', (line, expected) => {
    expect(stepAtLine(model!, line)).toEqual(expected);
  });
});

describe('collectProblems', () => {
  const server = {
    id: 'x',
    name: 'X',
    action: 'unchanged' as const,
    state: 'draft' as const,
    stateChanged: false,
    assumedShapes: ['a.b as money'],
    errors: [{ message: 'bad', at: { line: 3, column: 2 } }, { message: 'nowhere' }],
  };

  it('lists the server errors and the assumed shapes as warnings', () => {
    const all = collectProblems([], server);
    expect(all).toEqual([
      { severity: 'error', message: 'bad', line: 3, column: 2, source: 'server' },
      { severity: 'error', message: 'nowhere', line: undefined, column: undefined, source: 'server' },
      { severity: 'warning', message: 'a.b as money', source: 'server' },
    ]);
    expect([...problemsByLine(all).keys()]).toEqual([3]);
  });

  it('shows only the local problems while there are some', () => {
    expect(collectProblems([{ message: 'syntax', line: 1, column: 1 }], server)).toEqual([
      { severity: 'error', message: 'syntax', line: 1, column: 1, source: 'local' },
    ]);
  });
});
