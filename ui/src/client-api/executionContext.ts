import { Stage, stub } from './notImplemented';

/** The execution context a handler receives first when it has `passExecutionContext`. */
export interface ExecutionContext {
  getFormContext(): unknown;
  /** The formContext for OnLoad, the attribute for OnChange. */
  getEventSource(): unknown;
  /** `null` except for OnSave. */
  getEventArgs(): unknown;
  getDepth(): number;
  getContext(): unknown;
  getSharedVariable(key: string): unknown;
  setSharedVariable(key: string, value: unknown): void;
}

export interface ExecutionContextInit {
  formContext: unknown;
  eventSource?: unknown;
  eventArgs?: unknown;
  /** The app shell's global context (`Xrm.Utility.getGlobalContext()`); a form outside an app shell has none. */
  globalContext?: () => unknown;
}

/**
 * One context for one event run: every handler of the run gets it, so shared variables set by one are read by the next.
 * `getEventSource()` defaults to the formContext.
 */
export function createExecutionContext(init: ExecutionContextInit): ExecutionContext {
  const shared = new Map<string, unknown>();
  return {
    getFormContext: () => init.formContext,
    getEventSource: () => (init.eventSource === undefined ? init.formContext : init.eventSource),
    getEventArgs: () => init.eventArgs ?? null,
    getDepth: () => 1,
    getContext: () => {
      const context = init.globalContext?.();
      // Only a form outside an app shell (no Xrm) gets here.
      if (context === undefined || context === null) return stub('executionContext.getContext', Stage.later)();
      return context;
    },
    getSharedVariable: (key) => shared.get(key),
    setSharedVariable: (key, value) => void shared.set(key, value),
  };
}
