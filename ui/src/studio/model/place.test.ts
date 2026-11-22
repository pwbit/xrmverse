import { describe, expect, it } from 'vitest';
import { insertProblem, moveProblem, placeOf, processOfDocument, shapeOfNode } from './place';
import { parseText } from './read';

describe('place', () => {
  it('placeOf reads the place from the branch path', () => {
    expect(placeOf(['steps'])).toEqual({ insideIf: false, insideWait: false });
    expect(placeOf(['steps', 0, 'if', 'then'])).toEqual({ insideIf: true, insideWait: false });
    expect(placeOf(['steps', 0, 'if', 'elseIf', 1, 'then'])).toEqual({ insideIf: true, insideWait: false });
    expect(placeOf(['steps', 2, 'wait', 'onTimeout', 0, 'if', 'else'])).toEqual({ insideIf: true, insideWait: true });
  });

  it('a business rule action needs an if; an if holding actions may stand anywhere', () => {
    const rule = { kind: 'businessRule' as const, background: false };
    expect(insertProblem('setValue', rule, ['steps'])).toBeDefined();
    expect(insertProblem('setValue', rule, ['steps', 0, 'if', 'then'])).toBeUndefined();
    const ifWithAction = { type: 'if' as const, branches: [{ label: 'then' as const, steps: [{ type: 'setValue' as const, branches: [] }] }] };
    expect(moveProblem(ifWithAction, rule, ['steps'])).toBeUndefined();
  });

  it('checks the steps inside the moved one, and reads the document the way the reader does', () => {
    const { doc } = parseText(
      ['kind: workflow', 'name: X', 'entity: account', 'mode: background', 'steps:', '  - wait:', '      until: { all: [] }', '      then:', '        - stop: { status: succeeded }', ''].join('\n'),
    );
    const process = processOfDocument(doc)!;
    expect(process).toEqual({ kind: 'workflow', background: true });
    const shape = shapeOfNode(doc.getIn(['steps', 0], true))!;
    expect(shape).toEqual({ type: 'wait', branches: [{ label: 'then', steps: [{ type: 'stop', branches: [] }] }] });
    expect(moveProblem(shape, process, ['steps'])).toBeUndefined();
    expect(moveProblem(shape, { kind: 'workflow', background: false }, ['steps'])).toBeDefined();
  });
});
