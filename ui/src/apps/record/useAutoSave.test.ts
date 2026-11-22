import { act, renderHook } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { SaveOutcome } from '../../client-api/formContext';
import { AUTO_SAVE_DELAY_MS, useAutoSave, type AutoSaveOptions } from './useAutoSave';

function setup(over: Partial<AutoSaveOptions> = {}, outcome: SaveOutcome = 'saved') {
  const autoSave = vi.fn(async () => outcome);
  const props: AutoSaveOptions = { enabled: true, creating: false, dirty: true, busy: false, changeKey: 1, autoSave, ...over };
  const hook = renderHook((p: AutoSaveOptions) => useAutoSave(p), { initialProps: props });
  return { autoSave, hook, props };
}

describe('useAutoSave', () => {
  beforeEach(() => vi.useFakeTimers());
  afterEach(() => vi.useRealTimers());

  it('saves_30_seconds_after_the_last_change', async () => {
    const { autoSave, hook, props } = setup();
    expect(hook.result.current.status).toBe('unsaved');
    await act(() => vi.advanceTimersByTimeAsync(AUTO_SAVE_DELAY_MS - 1000));
    // Another change starts the delay again.
    hook.rerender({ ...props, changeKey: 2 });
    await act(() => vi.advanceTimersByTimeAsync(AUTO_SAVE_DELAY_MS - 1000));
    expect(autoSave).not.toHaveBeenCalled();
    await act(() => vi.advanceTimersByTimeAsync(1000));
    expect(autoSave).toHaveBeenCalledTimes(1);
    hook.rerender({ ...props, changeKey: 2, dirty: false });
    expect(hook.result.current.status).toBe('saved');
  });

  it('never_on_a_create_form_or_when_disabled_or_clean', async () => {
    for (const over of [{ creating: true }, { enabled: false }, { dirty: false }]) {
      const { autoSave, hook } = setup(over);
      await act(() => vi.advanceTimersByTimeAsync(AUTO_SAVE_DELAY_MS * 2));
      expect(autoSave).not.toHaveBeenCalled();
      expect(hook.result.current.status).toBe('none');
      expect(hook.result.current.saveNow()).toBeUndefined();
      hook.unmount();
    }
  });

  it('no_save_while_busy_and_no_retry_after_a_failure', async () => {
    const busy = setup({ busy: true });
    await act(() => vi.advanceTimersByTimeAsync(AUTO_SAVE_DELAY_MS * 2));
    expect(busy.autoSave).not.toHaveBeenCalled();
    busy.hook.unmount();

    const failed = setup({}, 'failed');
    await act(() => vi.advanceTimersByTimeAsync(AUTO_SAVE_DELAY_MS));
    await act(() => vi.advanceTimersByTimeAsync(AUTO_SAVE_DELAY_MS * 3));
    expect(failed.autoSave).toHaveBeenCalledTimes(1);
    expect(failed.hook.result.current.status).toBe('unsaved');
    // The next change tries again.
    failed.hook.rerender({ ...failed.props, changeKey: 2 });
    await act(() => vi.advanceTimersByTimeAsync(AUTO_SAVE_DELAY_MS));
    expect(failed.autoSave).toHaveBeenCalledTimes(2);
  });

  it('save_now_saves_at_once_and_reports_saving', async () => {
    let finish: (o: SaveOutcome) => void = () => undefined;
    const autoSave = vi.fn(() => new Promise<SaveOutcome>((resolve) => (finish = resolve)));
    const { hook } = setup({ autoSave });
    let pending: Promise<SaveOutcome> | undefined;
    act(() => {
      pending = hook.result.current.saveNow();
    });
    expect(hook.result.current.status).toBe('saving');
    await act(async () => {
      finish('prevented');
      await pending;
    });
    await expect(pending).resolves.toBe('prevented');
    expect(hook.result.current.status).toBe('unsaved');
  });
});
