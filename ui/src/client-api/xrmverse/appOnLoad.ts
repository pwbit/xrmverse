import type { AppModel } from '../../model/app';
import { t } from '../../shell/i18n/t';
import { ASYNC_HANDLER_TIMEOUT_MS } from '../events';
import { createExecutionContext } from '../executionContext';
import { createScriptHost, type ScriptHost } from '../scriptHost';

export interface AppOnLoadDeps {
  /** Puts `Xrm` and `XrmVerse` on the app script host window before the libraries load. */
  install: (win: Window) => void;
  /** The app's global context (`Xrm.Utility.getGlobalContext()`), what the execution context's `getContext()` returns. */
  globalContext: unknown;
  /** Shows an app notification (level 2, error). */
  notify: (n: { level: 2; message: string }) => void;
  /** Aborted when the app is closed: the script host is disposed and nothing more is reported. */
  signal: AbortSignal;
}

const messageOf = (e: unknown): string =>
  typeof e === 'object' && e !== null && 'message' in e ? String((e as { message: unknown }).message) : String(e);

const isThenable = (value: unknown): value is PromiseLike<unknown> =>
  !!value && (typeof value === 'object' || typeof value === 'function') && typeof (value as PromiseLike<unknown>).then === 'function';

/**
 * Runs the app's OnLoad handlers (the `xrmverse_AppOnLoad` setting): one app script host holds every library until `signal`
 * aborts; the handlers are called in order, each with an execution context (`getContext()` is the global context,
 * `getEventSource()` is null) and a returned promise is awaited up to 10 seconds. A failure shows one notification and the
 * next handler still runs. After an abort nothing is reported.
 */
export async function runAppOnLoad(setting: AppModel['appOnLoad'], deps: AppOnLoadDeps): Promise<void> {
  const { signal } = deps;
  const notify = (message: string) => {
    if (!signal.aborted) deps.notify({ level: 2, message });
  };
  if (!setting || signal.aborted) return;
  if ('error' in setting) return notify(t('appOnLoad.invalid', { error: setting.error }));
  if (setting.handlers.length === 0) return;

  const libraries: string[] = [];
  for (const { library } of setting.handlers) {
    if (!libraries.some((l) => l.toLowerCase() === library.toLowerCase())) libraries.push(library);
  }
  let host: ScriptHost;
  try {
    host = await createScriptHost(libraries, deps.install, undefined, signal);
  } catch (e) {
    if (!signal.aborted) notify(t('appOnLoad.hostFailed', { message: messageOf(e) }));
    return;
  }
  if (signal.aborted) {
    host.dispose();
    return;
  }
  signal.addEventListener('abort', () => host.dispose(), { once: true });

  const context = createExecutionContext({ formContext: null, eventSource: null, globalContext: () => deps.globalContext });
  for (const handler of setting.handlers) {
    if (signal.aborted) return;
    const report = (message: string) => notify(t('appOnLoad.failed', { library: handler.library, function: handler.function, message }));
    if (host.failed.some((f) => f.toLowerCase() === handler.library.toLowerCase())) {
      report(t('appOnLoad.libraryFailed'));
      continue;
    }
    const fn = host.resolve(handler.function);
    if (!fn) {
      report(t('appOnLoad.notFound'));
      continue;
    }
    let result: unknown;
    try {
      result = fn(context);
    } catch (e) {
      report(messageOf(e));
      continue;
    }
    if (!isThenable(result)) continue;
    let timer: ReturnType<typeof setTimeout> | undefined;
    let onAbort: (() => void) | undefined;
    const outcome = await Promise.race([
      Promise.resolve(result).then(
        () => undefined,
        (e: unknown) => ({ message: messageOf(e) }),
      ),
      new Promise<{ message: string }>((resolve) => {
        timer = setTimeout(() => resolve({ message: t('appOnLoad.timeout', { seconds: String(ASYNC_HANDLER_TIMEOUT_MS / 1000) }) }), ASYNC_HANDLER_TIMEOUT_MS);
      }),
      new Promise<undefined>((resolve) => {
        onAbort = () => resolve(undefined);
        signal.addEventListener('abort', onAbort, { once: true });
      }),
    ]);
    clearTimeout(timer);
    if (onAbort) signal.removeEventListener('abort', onAbort);
    if (outcome) report(outcome.message);
  }
}
