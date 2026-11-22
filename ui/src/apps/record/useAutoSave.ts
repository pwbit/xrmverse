import { useEffect, useRef, useState } from 'react';
import type { SaveOutcome } from '../../client-api/formContext';

/** Dataverse saves a changed record 30 seconds after the last change (Learn, "Manage auto-save"). */
export const AUTO_SAVE_DELAY_MS = 30_000;

/** What the form footer says about saving: nothing to say, changes not saved yet, an auto-save running, or saved. */
export type SaveStatus = 'none' | 'unsaved' | 'saving' | 'saved';

export interface AutoSaveOptions {
  /** `organization.isautosaveenabled`. */
  enabled: boolean;
  /** A create form never saves by itself: only after its first save (Dataverse). */
  creating: boolean;
  dirty: boolean;
  /** A request of the form runs (save, refresh, state change): the auto-save waits for the next change. */
  busy: boolean;
  /** Changes with every edit: the delay starts again from the last change. */
  changeKey: unknown;
  /** Saves with `SaveMode.AutoSave` (OnSave runs first and may prevent it). */
  autoSave(): Promise<SaveOutcome>;
}

export interface AutoSave {
  status: SaveStatus;
  /** Saves now when auto-save applies (leaving the form); `undefined` when it does not apply. */
  saveNow(): Promise<SaveOutcome> | undefined;
  /** A save outside the auto-save (the Save command) ended. */
  noteSaved(outcome: SaveOutcome): void;
}

/**
 * Auto-save of the model-driven form (#117, ruling R4): with the organization's switch on, an existing record with changes is
 * saved 30 seconds after the last change. A failed or prevented auto-save leaves the form dirty and waits for the next change:
 * there is no retry loop. Nothing is saved while another request of the form runs.
 */
export function useAutoSave({ enabled, creating, dirty, busy, changeKey, autoSave }: AutoSaveOptions): AutoSave {
  const active = enabled && !creating;
  const [saving, setSaving] = useState(false);
  const [saved, setSaved] = useState(false);
  const saveRef = useRef(autoSave);
  saveRef.current = autoSave;
  const busyRef = useRef(busy);
  busyRef.current = busy;

  const run = async () => {
    setSaving(true);
    try {
      const outcome = await saveRef.current();
      setSaved(outcome === 'saved');
      return outcome;
    } finally {
      setSaving(false);
    }
  };

  useEffect(() => {
    if (dirty) setSaved(false);
  }, [dirty, changeKey]);

  useEffect(() => {
    if (!active || !dirty) return;
    const timer = setTimeout(() => {
      if (!busyRef.current) void run();
    }, AUTO_SAVE_DELAY_MS);
    return () => clearTimeout(timer);
    // `run` reads the latest save through a ref.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [active, dirty, changeKey]);

  const status: SaveStatus = !active ? 'none' : saving ? 'saving' : dirty ? 'unsaved' : saved ? 'saved' : 'none';
  return {
    status,
    saveNow: () => (active && dirty && !busyRef.current ? run() : undefined),
    noteSaved: (outcome) => setSaved(outcome === 'saved'),
  };
}
