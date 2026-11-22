import { createContext, useContext, type ReactNode } from 'react';
import { normaliseId, type FormPresentation, type PresentationModel, type ViewPresentation } from '../../parsers/presentationJson';

export { normaliseId };

const PresentationCtx = createContext<PresentationModel | undefined>(undefined);
const ModernLookCtx = createContext(false);

/**
 * Makes the parsed overlay available to the lists below. Pass no model for the classic look; `modern` tells the components the
 * modern look shares with the classic one (a form's subgrid) that the modern look is in use, with or without an overlay.
 */
export function PresentationProvider({ model, modern = false, children }: { model?: PresentationModel; modern?: boolean; children: ReactNode }) {
  return (
    <ModernLookCtx.Provider value={modern}>
      <PresentationCtx.Provider value={model}>{children}</PresentationCtx.Provider>
    </ModernLookCtx.Provider>
  );
}

/** Whether the modern look is in use (false without a provider). */
export function useModernLook(): boolean {
  return useContext(ModernLookCtx);
}

/** The overlay's presentation of one saved view (braces and case of the id do not matter), if any. */
export function useViewPresentation(viewId: string | undefined): ViewPresentation | undefined {
  const model = useContext(PresentationCtx);
  if (!model || !viewId) return undefined;
  return model.views[normaliseId(viewId)];
}

/** The overlay's presentation of one main form (braces and case of the id do not matter), if any. */
export function useFormPresentation(formId: string | undefined): FormPresentation | undefined {
  const model = useContext(PresentationCtx);
  if (!model || !formId) return undefined;
  return model.forms[normaliseId(formId)];
}

/** The whole overlay model (choices and theme included), or undefined without one. */
export function usePresentationModel(): PresentationModel | undefined {
  return useContext(PresentationCtx);
}
