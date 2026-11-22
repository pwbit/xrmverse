import { forEachDiagnostic } from '@codemirror/lint';
import { EditorView } from '@codemirror/view';
import { act, render } from '@testing-library/react';
import { createRef } from 'react';
import { describe, expect, it, vi } from 'vitest';
import { CodeView, type CodeViewHandle } from './CodeView';

const TEXT = ['kind: workflow', 'name: X', 'entity: account', 'mode: background', 'steps:', '  - stop: { status: succeeded }', ''].join('\n');

function viewOf(container: HTMLElement): EditorView {
  const dom = container.querySelector('.cm-editor');
  expect(dom).not.toBeNull();
  return EditorView.findFromDOM(dom as HTMLElement)!;
}

function diagnostics(view: EditorView) {
  const found: { line: number; message: string; severity: string }[] = [];
  forEachDiagnostic(view.state, (d, from) => found.push({ line: view.state.doc.lineAt(from).number, message: d.message, severity: d.severity }));
  return found;
}

describe('CodeView', () => {
  it('shows the text in a CodeMirror editor', () => {
    const { container } = render(<CodeView text={TEXT} onChange={() => undefined} problems={[]} />);
    expect(viewOf(container).state.doc.toString()).toBe(TEXT);
  });

  it('turns a server error at line 4 into a lint diagnostic on line 4', () => {
    const { container, rerender } = render(<CodeView text={TEXT} onChange={() => undefined} problems={[]} />);
    const view = viewOf(container);
    expect(diagnostics(view)).toEqual([]);
    rerender(<CodeView text={TEXT} onChange={() => undefined} problems={[{ severity: 'error', message: "The mode 'x' is unknown.", line: 4, column: 7 }]} />);
    expect(diagnostics(view)).toEqual([{ line: 4, message: "The mode 'x' is unknown.", severity: 'error' }]);
  });

  it('calls onChange when the user types', () => {
    const onChange = vi.fn();
    const { container } = render(<CodeView text={TEXT} onChange={onChange} problems={[]} />);
    const view = viewOf(container);
    act(() => view.dispatch({ changes: { from: 6, to: 14, insert: 'action' }, userEvent: 'input.type' }));
    expect(onChange).toHaveBeenLastCalledWith(TEXT.replace('workflow', 'action'));
  });

  it('takes a new text from outside without calling onChange', () => {
    const onChange = vi.fn();
    const { container, rerender } = render(<CodeView text={TEXT} onChange={onChange} problems={[]} />);
    rerender(<CodeView text={TEXT.replace('name: X', 'name: Y')} onChange={onChange} problems={[]} />);
    expect(viewOf(container).state.doc.toString()).toContain('name: Y');
    expect(onChange).not.toHaveBeenCalled();
  });

  it('goTo moves the cursor to the line and column', () => {
    const ref = createRef<CodeViewHandle>();
    const { container } = render(<CodeView ref={ref} text={TEXT} onChange={() => undefined} problems={[]} />);
    act(() => ref.current!.goTo(4, 7));
    const view = viewOf(container);
    const head = view.state.selection.main.head;
    const line = view.state.doc.lineAt(head);
    expect([line.number, head - line.from + 1]).toEqual([4, 7]);
  });
});
