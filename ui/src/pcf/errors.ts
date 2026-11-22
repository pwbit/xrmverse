/** Why a PCF control cannot run; the form then shows the standard control with a notice (ruling R7). */
export type PcfFailure = 'notFound' | 'manifest' | 'platformLibrary' | 'script' | 'timeout';

/**
 * A control that cannot be loaded. `code` says why; `message` is the reason in English, for the notice "Custom control
 * '{name}' could not be loaded: {reason}". `transient` marks a failure a later attempt may not repeat (a request that
 * failed, a script that did not load, a registration that came too late).
 */
export class PcfLoadError extends Error {
  readonly transient: boolean;

  constructor(
    readonly code: PcfFailure,
    message: string,
    options: { transient?: boolean } = {},
  ) {
    super(message);
    this.name = 'PcfLoadError';
    this.transient = options.transient ?? (code === 'script' || code === 'timeout');
  }
}

/** Whether a load failure stays the same for the session: a missing control, an invalid manifest, an unsupported library. */
export function isPermanentFailure(e: unknown): boolean {
  return e instanceof PcfLoadError && !e.transient;
}
