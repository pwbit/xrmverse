import type { Handler } from '../model/form';
import type { FormNotification } from '../renderers/classic/form/FormNotifications';
import { t } from '../shell/i18n/t';
import type { ExecutionContext } from './executionContext';
import type { ScriptHost } from './scriptHost';

/** The `parameters` attribute as JavaScript values: it is read as the inside of a JSON array. Throws when it does not parse. */
export function parseParameters(text: string): unknown[] {
  if (text.trim() === '') return [];
  const parsed: unknown = JSON.parse(`[${text}]`);
  return parsed as unknown[];
}

const messageOf = (e: unknown): string =>
  typeof e === 'object' && e !== null && 'message' in e ? String((e as { message: unknown }).message) : String(e);

let counter = 0;
const error = (text: string): FormNotification => ({ key: `script-${++counter}`, intent: 'error', text });

/** How long the form waits for the promise of one handler when the app awaits them (Learn, Form OnLoad and OnSave events). */
export const ASYNC_HANDLER_TIMEOUT_MS = 10_000;

/** What one handler of a run is told and reports: the OnSave event arguments keep these per handler. */
export interface HandlerSlot {
  /** The handler called `disableAsyncTimeout()` before it returned. */
  timeoutDisabled(): boolean;
  /** The handler threw, its promise rejected or it timed out. */
  failed(): void;
}

/** How a run treats the promises its handlers return. */
export interface RunMode {
  /** The app setting for this event is on: each returned promise is awaited, in order, before the next handler runs. */
  wait?: boolean;
  /** Defaults to {@link ASYNC_HANDLER_TIMEOUT_MS}. */
  timeoutMs?: number;
  /** Called before each handler runs. */
  slot?: () => HandlerSlot;
}

const isThenable = (value: unknown): value is PromiseLike<unknown> =>
  !!value && (typeof value === 'object' || typeof value === 'function') && typeof (value as PromiseLike<unknown>).then === 'function';

/**
 * Calls one handler. A throw or a rejection reports a form notification. Without `mode.wait` a returned promise is not
 * awaited; with it the result settles when the promise does, or after the timeout unless the handler disabled it.
 */
function invoke(call: () => unknown, functionName: string, notify: (n: FormNotification) => void, mode: RunMode): Promise<void> | undefined {
  const slot = mode.slot?.();
  let done = false;
  const report = (e: unknown) => {
    if (done) return;
    done = true;
    slot?.failed();
    notify(error(t('script.error', { functionName, message: messageOf(e) })));
  };
  let result: unknown;
  try {
    result = call();
  } catch (e) {
    report(e);
    return undefined;
  }
  if (!isThenable(result)) return undefined;
  if (!mode.wait) {
    result.then(undefined, report);
    return undefined;
  }
  const settled = Promise.resolve(result).then(() => void (done = true), report);
  if (slot?.timeoutDisabled()) return settled;
  const timeoutMs = mode.timeoutMs ?? ASYNC_HANDLER_TIMEOUT_MS;
  let timer: ReturnType<typeof setTimeout> | undefined;
  const timedOut = new Promise<void>((resolve) => {
    timer = setTimeout(() => {
      if (!done) {
        done = true;
        slot?.failed();
        notify(error(t('script.timeout', { functionName, seconds: String(timeoutMs / 1000) })));
      }
      resolve();
    }, timeoutMs);
  });
  return Promise.race([settled, timedOut]).finally(() => clearTimeout(timer));
}

/**
 * Runs `handlers` in order. A disabled handler is skipped; one that cannot run or throws reports a form notification and the next
 * still runs. A returned promise is awaited only with `mode.wait` (a rejection is always reported). One execution context
 * serves the whole run, built on first use, so shared variables pass from handler to handler. A handler of a library that
 * failed to load is skipped: the load failure was reported already. Without `mode.wait` every handler has run when this returns.
 */
export async function runHandlers(
  host: ScriptHost,
  handlers: Handler[],
  makeContext: () => ExecutionContext,
  notify: (n: FormNotification) => void,
  mode: RunMode = {},
): Promise<void> {
  let context: ExecutionContext | undefined;
  for (const handler of handlers) {
    if (!handler.enabled) continue;
    const { functionName, libraryName } = handler;
    if (host.failed.some((f) => f.toLowerCase() === libraryName.toLowerCase())) continue;
    const fn = host.resolve(functionName);
    if (!fn) {
      notify(error(t('script.notFound', { functionName, libraryName })));
      continue;
    }
    let parameters: unknown[];
    try {
      parameters = parseParameters(handler.parameters);
    } catch (e) {
      notify(error(t('script.badParameters', { functionName, message: messageOf(e) })));
      continue;
    }
    const pending = invoke(() => fn(...(handler.passExecutionContext ? [(context ??= makeContext()), ...parameters] : parameters)), functionName, notify, mode);
    if (pending) await pending;
  }
}

/**
 * Runs functions a script added to an event (`addOnChange`, `addOnSave`, `addPreSearch`, ...) in order, each with the
 * execution context first, as Learn describes them. One that throws or whose promise rejects reports a form notification
 * named after the function, and the next still runs. Promises are awaited as {@link runHandlers} does.
 */
export async function runFunctions(
  fns: readonly ((...args: unknown[]) => unknown)[],
  makeContext: () => ExecutionContext,
  notify: (n: FormNotification) => void,
  mode: RunMode = {},
): Promise<void> {
  for (const fn of fns) {
    const pending = invoke(() => fn(makeContext()), fn.name || 'anonymous', notify, mode);
    if (pending) await pending;
  }
}
