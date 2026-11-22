import { createContext, useContext, useEffect, useRef } from 'react';

/**
 * Runs after the record itself was saved and published, with the record's id (the new one after a create). A step that
 * fails shows its error in its own card and resolves; a step that throws has its message shown in the page's error bar.
 * The record stays saved either way.
 */
export type AfterSave = (id: string) => Promise<void>;

export interface DesignerHooks {
  /** Adds a step run after every save, in registration order; returns the unregister function. */
  registerAfterSave: (key: string, run: AfterSave) => () => void;
  /** Marks the page changed for state a card keeps outside the design: Back then asks first. */
  markDirty: () => void;
}

export const DesignerContext = createContext<DesignerHooks>({
  registerAfterSave: () => () => undefined,
  markDirty: () => undefined,
});

/** Registers `run` (its latest version) as an after-save step of the designer while the calling card is mounted. */
export function useAfterSave(key: string, run: AfterSave): void {
  const { registerAfterSave } = useContext(DesignerContext);
  const latest = useRef(run);
  latest.current = run;
  useEffect(() => registerAfterSave(key, (id) => latest.current(id)), [key, registerAfterSave]);
}

/** The designer's hooks, for a card that needs `markDirty`. */
export const useDesignerHooks = (): DesignerHooks => useContext(DesignerContext);
