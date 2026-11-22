import { act, renderHook } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { XrmApi } from '../../shell/api/client';
import type { ApplyResult } from '../api/studioApi';
import { setAt } from '../model/edit';
import { insertId } from './EditorPage';
import { useEditorDocument } from './useEditorDocument';

const TEXT = ['kind: workflow', 'name: X', 'entity: account', 'mode: background', 'steps:', '  # keep me', '  - stop: { status: succeeded }', ''].join('\n');

const result = (over: Partial<ApplyResult> = {}): ApplyResult => ({
  id: 'id-1',
  name: 'X',
  action: 'unchanged',
  state: 'draft',
  stateChanged: false,
  assumedShapes: [],
  errors: [],
  ...over,
});

interface Pending {
  body: string;
  resolve(r: ApplyResult): void;
}

/** An api whose apply requests wait until the test answers them. */
function fakeApi() {
  const pending: Pending[] = [];
  const authoring = vi.fn(
    (_method: string, _path: string, opts?: { body?: string }) =>
      new Promise<Response>((resolve) => {
        pending.push({ body: opts?.body ?? '', resolve: (r) => resolve(new Response(JSON.stringify(r), { status: r.errors.length ? 400 : 200 })) });
      }),
  );
  return { api: { authoring } as unknown as XrmApi, pending, authoring };
}

async function flush() {
  await act(async () => {
    for (let i = 0; i < 10; i++) await Promise.resolve();
  });
}

beforeEach(() => {
  vi.useFakeTimers();
});
afterEach(() => {
  vi.useRealTimers();
});

describe('useEditorDocument', () => {
  it('debounces 800 ms', async () => {
    const { api, authoring } = fakeApi();
    const { result: hook } = renderHook(() => useEditorDocument(api, TEXT));
    act(() => hook.current.setText(TEXT.replace('name: X', 'name: A')));
    act(() => vi.advanceTimersByTime(300));
    act(() => hook.current.setText(TEXT.replace('name: X', 'name: AB')));
    act(() => vi.advanceTimersByTime(300));
    act(() => hook.current.setText(TEXT.replace('name: X', 'name: ABC')));
    act(() => vi.advanceTimersByTime(799));
    expect(authoring).not.toHaveBeenCalled();
    act(() => vi.advanceTimersByTime(1));
    expect(authoring).toHaveBeenCalledTimes(1);
    expect(authoring.mock.calls[0][1]).toContain('dryRun=true');
    expect(authoring.mock.calls[0][2]?.body).toContain('name: ABC');
  });

  it('drops stale dry-run responses', async () => {
    const { api, pending } = fakeApi();
    const { result: hook } = renderHook(() => useEditorDocument(api, TEXT));
    act(() => hook.current.setText(TEXT.replace('name: X', 'name: First')));
    act(() => vi.advanceTimersByTime(800));
    act(() => hook.current.setText(TEXT.replace('name: X', 'name: Second')));
    act(() => vi.advanceTimersByTime(800));
    expect(pending.map((p) => /name: (\w+)/.exec(p.body)?.[1])).toEqual(['First', 'Second']);
    // The second answer arrives first, the first one last: the first one is stale and dropped.
    pending[1].resolve(result({ errors: [{ message: 'second', at: { line: 2, column: 7 } }] }));
    await flush();
    pending[0].resolve(result({ errors: [{ message: 'first', at: { line: 2, column: 7 } }] }));
    await flush();
    expect(hook.current.serverResult?.errors.map((e) => e.message)).toEqual(['second']);
  });

  it('skips the dry run while the text has local problems', async () => {
    const { api, authoring } = fakeApi();
    const { result: hook } = renderHook(() => useEditorDocument(api, TEXT));
    act(() => hook.current.setText('kind: workflow\nname: [unclosed\n'));
    act(() => vi.advanceTimersByTime(2000));
    expect(authoring).not.toHaveBeenCalled();
    expect(hook.current.localProblems.length).toBeGreaterThan(0);
    expect(hook.current.model).toBeUndefined();
    expect(hook.current.dirty).toBe(true);
  });

  it('mutate edits the document, keeps the comments and re-derives the model', () => {
    const { api } = fakeApi();
    const { result: hook } = renderHook(() => useEditorDocument(api, TEXT));
    expect(hook.current.dirty).toBe(false);
    act(() => hook.current.mutate((doc) => setAt(doc, ['name'], 'Renamed')));
    expect(hook.current.text).toContain('name: Renamed');
    expect(hook.current.text).toContain('# keep me');
    expect(hook.current.model?.name).toBe('Renamed');
    expect(hook.current.dirty).toBe(true);
    act(() => hook.current.markSaved(hook.current.text));
    expect(hook.current.dirty).toBe(false);
  });

  it('markSaved keeps an edit made while the save was in flight, and stays dirty', () => {
    const { api } = fakeApi();
    const { result: hook } = renderHook(() => useEditorDocument(api, TEXT));
    act(() => hook.current.setText(TEXT.replace('name: X', 'name: Sent')));
    const sent = hook.current.text;
    // The save is in flight: the canvas changes the document.
    act(() => hook.current.mutate((doc) => setAt(doc, ['name'], 'Later')));
    act(() => {
      hook.current.markSaved(sent);
    });
    expect(hook.current.text).toContain('name: Later');
    expect(hook.current.dirty).toBe(true);
    // Back to the saved text: clean.
    act(() => hook.current.setText(sent));
    expect(hook.current.dirty).toBe(false);
  });

  it('markSaved applies the change of a first save to the saved text and to the current one', () => {
    const { api } = fakeApi();
    const { result: hook } = renderHook(() => useEditorDocument(api, TEXT));
    const sent = hook.current.text;
    let out: { text: string; saved: string } | undefined;
    act(() => {
      out = hook.current.markSaved(sent, (doc) => setAt(doc, ['id'], 'id-1'));
    });
    expect(hook.current.text).toContain('id: id-1');
    expect(hook.current.dirty).toBe(false);
    expect(out).toEqual({ text: hook.current.text, saved: hook.current.text });

    act(() => hook.current.mutate((doc) => setAt(doc, ['name'], 'Later')));
    const second = hook.current.text;
    act(() => hook.current.mutate((doc) => setAt(doc, ['description'], 'more')));
    act(() => {
      out = hook.current.markSaved(second, (doc) => setAt(doc, ['id'], 'id-2'));
    });
    expect(hook.current.text).toContain('id: id-2');
    expect(hook.current.text).toContain('description: more');
    expect(hook.current.dirty).toBe(true);
    expect(out!.saved).toContain('id: id-2');
    expect(out!.saved).not.toContain('description: more');
  });

  it('a first save whose id the kept text cannot take keeps it waiting, dirty and visible, then adds it when it can', () => {
    const { api } = fakeApi();
    const { result: hook } = renderHook(() => useEditorDocument(api, TEXT));
    const sent = hook.current.text;
    // The save is in flight: the text becomes one whose root is not a mapping, which cannot take the id.
    const broken = '- not a workflow\n';
    act(() => hook.current.setText(broken));
    let out: ReturnType<typeof hook.current.markSaved> | undefined;
    act(() => {
      out = hook.current.markSaved(sent, (doc) => insertId(doc, 'id-1'));
    });
    // Not dropped: the text is kept, stays dirty, and the id waits (Save is blocked on changePending).
    expect(hook.current.text).toBe(broken);
    expect(hook.current.changePending).toBe(true);
    expect(hook.current.dirty).toBe(true);
    expect(out!.saved).toContain('id: id-1');
    expect(out!.pending).toBeDefined();
    // Once the text can take it, the id goes in.
    act(() => hook.current.setText(TEXT.replace('name: X', 'name: Fixed')));
    expect(hook.current.changePending).toBe(false);
    expect(hook.current.text).toContain('kind: workflow\nid: id-1\nname: Fixed');
  });

  it('a waiting id handed over with the text is added at once when the text can take it', () => {
    const { api } = fakeApi();
    const { result: hook } = renderHook(() => useEditorDocument(api, TEXT, TEXT, (doc) => insertId(doc, 'id-9')));
    expect(hook.current.changePending).toBe(false);
    expect(hook.current.text).toContain('id: id-9');
  });

  it('starts dirty when the saved text it is given differs from the text', () => {
    const { api } = fakeApi();
    const { result: hook } = renderHook(() => useEditorDocument(api, TEXT.replace('name: X', 'name: Y'), TEXT));
    expect(hook.current.dirty).toBe(true);
  });

  it('after a save that kept a later edit, the kept text gets a fresh dry run', async () => {
    const { api, pending } = fakeApi();
    const { result: hook } = renderHook(() => useEditorDocument(api, TEXT));
    act(() => hook.current.setText(TEXT.replace('name: X', 'name: Sent')));
    const sent = hook.current.text;
    // The save is in flight; the canvas edits; the edit's dry run is due.
    act(() => hook.current.mutate((doc) => setAt(doc, ['name'], 'Later')));
    act(() => vi.advanceTimersByTime(800));
    expect(pending.map((p) => /name: (\w+)/.exec(p.body)?.[1])).toEqual(['Later']);
    // The save answers: its result is shown (the edit's dry run is now stale) and the sent text is saved.
    act(() => {
      hook.current.showResult(result({ assumedShapes: ['about the sent text'] }));
      hook.current.markSaved(sent);
    });
    pending[0].resolve(result());
    await flush();
    // The save's answer was about the sent text: it is not shown against the kept one.
    expect(hook.current.serverResult).toBeUndefined();
    act(() => vi.advanceTimersByTime(800));
    expect(pending.map((p) => /name: (\w+)/.exec(p.body)?.[1])).toEqual(['Later', 'Later']);
    pending[1].resolve(result({ errors: [{ message: 'later', at: { line: 2, column: 7 } }] }));
    await flush();
    expect(hook.current.serverResult?.errors.map((e) => e.message)).toEqual(['later']);
  });
});
