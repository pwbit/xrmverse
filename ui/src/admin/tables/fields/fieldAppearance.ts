import { createContext, useContext } from 'react';

/**
 * How the field editors draw their boxes: `outline` (the web admin, the default) or `filled-darker`, the soft gray fill with
 * no border the model-driven app forms use (the owner's choice, 2026-11-22). A disabled editor always keeps its outline, so
 * the fields the user cannot change stand apart from the ones they can.
 */
export type FieldAppearance = 'outline' | 'filled-darker';

const FieldAppearanceContext = createContext<FieldAppearance>('outline');

export const FieldAppearanceProvider = FieldAppearanceContext.Provider;

/** The appearance an editor's Fluent input takes: the provided one, or the outline when the editor is disabled. */
export function useFieldAppearance(disabled: boolean): FieldAppearance {
  const appearance = useContext(FieldAppearanceContext);
  return disabled ? 'outline' : appearance;
}
