import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { createScriptHost, type ScriptHost } from '../../client-api/scriptHost';
import type { RibbonCommand, RibbonParameter, RibbonRule } from '../../model/ribbon';
import { formCommands } from '../../parsers/ribbonDiffXml';
import type { FormNotification } from '../../renderers/classic/form/FormNotifications';
import { runActions } from '../../ribbon/actions';
import { evaluate, parameterValues, UnsupportedRibbonParameter, type RibbonEnv } from '../../ribbon/rules';
import { t } from '../../shell/i18n/t';
import type { RecordForm } from './useRecordForm';

/** A custom command the command bar shows: `busy` while its actions (or a save of the form) run; `run` is its click. */
export interface RibbonButton {
  command: RibbonCommand;
  busy: boolean;
  run(): void;
}

export interface RibbonCommandsInput {
  /** The table logical name. */
  entity: string;
  /** The user's language, for the `LocLabels`. */
  lcid: number;
  form: RecordForm;
  /** The form's formContext; rules wait for it (a `PrimaryControl` parameter and a `ValueRule` read it). */
  formContext?: unknown;
  formId: string;
  /** A Refresh of the form: the diffs are read again. */
  refreshKey: number;
  /** Shows a warning (an unavailable command, a diff that could not be read) or an action error on the form. */
  notify(n: FormNotification): void;
  /** Removes the notification `key` (a warning of the previous load, the error of a command's previous run). */
  clear(key: string): void;
  /** True while the page saves (OnSave handlers included): a click then does nothing. */
  saving?: () => boolean;
  /** Puts `Xrm` on the ribbon script host window before its libraries load (the app shell's `XrmHost.install`). */
  install?: (win: Window) => void;
  /** `Xrm.Utility.getGlobalContext()`: the organization's language and unique name. */
  globalContext?: unknown;
}

export interface RibbonCommands {
  /** The custom commands whose display and enable rules hold, in command bar order. */
  visible: RibbonButton[];
  /** Evaluates the rules again (`formContext.ui.refreshRibbon()`). */
  refresh(): void;
}

const installNothing = (): void => undefined;
/** An error's message, also for an error of the script host's window (another realm, so not `instanceof Error`). */
const messageOf = (e: unknown): string =>
  typeof e === 'object' && e !== null && 'message' in e ? String((e as { message: unknown }).message) : String(e);

/** A library a Refresh brought after the form's ribbon host was created with the libraries known then. */
const LIBRARY_ADDED_LATER = 'its library was added after the form opened; open the form again';

function rulesLibraries(rules: RibbonRule[], out: Set<string>): void {
  for (const r of rules) {
    if (r.kind === 'custom') out.add(r.library);
    else if (r.kind === 'or') for (const g of r.groups) rulesLibraries(g, out);
  }
}

/** The web resources a command's rules and actions call. */
function librariesOf(c: RibbonCommand): string[] {
  const out = new Set<string>();
  rulesLibraries(c.displayRules, out);
  rulesLibraries(c.enableRules, out);
  for (const a of c.actions) out.add(a.library);
  return [...out];
}

function rulesParameters(rules: RibbonRule[], out: RibbonParameter[][]): void {
  for (const r of rules) {
    if (r.kind === 'custom') out.push(r.parameters);
    else if (r.kind === 'or') for (const g of r.groups) rulesParameters(g, out);
  }
}

/** Why a command cannot run here because of a `CrmParameter` the host does not know; undefined when every one is known. */
function parameterProblem(c: RibbonCommand, env: RibbonEnv): string | undefined {
  const lists: RibbonParameter[][] = [];
  rulesParameters(c.displayRules, lists);
  rulesParameters(c.enableRules, lists);
  for (const a of c.actions) lists.push(a.parameters);
  for (const params of lists) {
    try {
      parameterValues(
        params.filter((p) => p.kind === 'crm'),
        env,
      );
    } catch (e) {
      if (e instanceof UnsupportedRibbonParameter) return e.message;
      throw e;
    }
  }
  return undefined;
}

/**
 * The text a `ValueRule` compares with (ruling E3): a choice is its number, a two-options value `true` or `false`, null
 * `null`, a lookup its id without braces in lower case, a multi-select choice its numbers joined by commas.
 */
function valueText(v: unknown): string {
  if (v === null || v === undefined) return 'null';
  if (Array.isArray(v)) {
    const first = v[0] as { id?: unknown } | undefined;
    if (first && typeof first === 'object' && typeof first.id === 'string') return first.id.replace(/^\{|\}$/g, '').toLowerCase();
    return v.map(String).join(',');
  }
  if (v instanceof Date) return v.toISOString();
  return String(v);
}

interface AttributeLike {
  getValue(): unknown;
}
interface ControlLike {
  getDisabled?: () => boolean;
  getAttribute?: () => unknown;
}
interface FormContextLike {
  getAttribute?: (name: string) => AttributeLike | null | undefined;
  ui?: { controls?: { get?: () => ControlLike[] } };
}

/** The response of `GET /Ribbon/{entity}` (Task 1). */
interface RibbonResponse {
  diffs?: { solution: string; xml: string }[];
}

async function readDiffs(entity: string, signal: AbortSignal): Promise<string[]> {
  const response = await fetch(`/Ribbon/${encodeURIComponent(entity)}`, { credentials: 'same-origin', signal });
  if (!response.ok) {
    let message = `${response.status} ${response.statusText}`.trim();
    try {
      const body = (await response.json()) as { detail?: unknown; title?: unknown; error?: { message?: unknown } };
      const text = body.detail ?? body.error?.message ?? body.title;
      if (typeof text === 'string' && text) message = text;
    } catch {
      // Not JSON: the status line says it.
    }
    throw new Error(message);
  }
  const body = (await response.json()) as RibbonResponse;
  return (body.diffs ?? []).map((d) => d.xml);
}

/**
 * The custom commands of a form (plan UI-1e, Task 4): the entity's `RibbonDiffXml` is read once per form load (and again
 * on `refreshKey`), its form commands are evaluated after the load, after each save, after an attribute change and on
 * {@link RibbonCommands.refresh} (ruling E5, the last evaluation wins), and the libraries load lazily into ONE ribbon script
 * host for this form, disposed when the form is left (ruling E4). A command the host cannot honour is not shown, with one
 * warning; one whose rules are false is hidden ("Disabled is hidden").
 */
export function useRibbonCommands(input: RibbonCommandsInput): RibbonCommands {
  const { entity, form, formContext, refreshKey } = input;
  const live = useRef(input);
  live.current = input;
  const [commands, setCommands] = useState<RibbonCommand[]>([]);
  const commandsRef = useRef(commands);
  commandsRef.current = commands;
  const [visibleIds, setVisibleIds] = useState<ReadonlySet<string>>(new Set());
  const [running, setRunning] = useState<ReadonlySet<string>>(new Set());
  const runningRef = useRef(new Set<string>());
  const [tick, setTick] = useState(0);
  /** Commands found unavailable while evaluating (a library that did not load): hidden and reported once per load. */
  const lateUnavailable = useRef(new Set<string>());
  /** The notification keys the current load set; the next load clears them first. */
  const loadKeys = useRef(new Set<string>());
  /** False once the form is left: nothing more is shown or set. */
  const alive = useRef(true);

  // The ribbon script host: created on first need for the libraries of every command, disposed on unmount.
  const lifetime = useRef<AbortController>();
  const host = useRef<{ promise: Promise<ScriptHost>; libraries: string[] }>();
  useEffect(() => {
    const controller = new AbortController();
    lifetime.current = controller;
    alive.current = true;
    return () => {
      alive.current = false;
      controller.abort();
      const current = host.current;
      host.current = undefined;
      current?.promise.then(
        (h) => h.dispose(),
        () => undefined,
      );
    };
  }, []);

  const ensureHost = useCallback((): { promise: Promise<ScriptHost>; libraries: string[] } => {
    if (!host.current) {
      const libraries = [...new Set(commandsRef.current.flatMap(librariesOf))];
      const signal = lifetime.current?.signal;
      const promise = createScriptHost(libraries, live.current.install ?? installNothing, undefined, signal);
      promise.then(
        (h) => signal?.aborted && h.dispose(),
        () => undefined,
      );
      host.current = { promise, libraries };
    }
    return host.current;
  }, []);

  /** Why `library` cannot be called from the form's ribbon host; undefined when it loaded. */
  const libraryProblem = useCallback(
    async (library: string): Promise<string | undefined> => {
      const { promise, libraries } = ensureHost();
      if (!libraries.includes(library)) return LIBRARY_ADDED_LATER;
      try {
        const h = await promise;
        return h.failed.includes(library) ? `the library ${library} could not be loaded` : undefined;
      } catch (e) {
        return `the script host could not be started (${messageOf(e)})`;
      }
    },
    [ensureHost],
  );

  const env = useCallback((): RibbonEnv => {
    const { form, formContext, entity, formId, lcid, globalContext } = live.current;
    const fc = formContext as FormContextLike | undefined;
    const org = (globalContext as { organizationSettings?: { uniqueName?: unknown; languageId?: unknown } } | null | undefined)?.organizationSettings;
    const id = form.latest().id;
    const fields = (() => {
      try {
        return (fc?.ui?.controls?.get?.() ?? []).filter((c) => typeof c.getAttribute === 'function' && typeof c.getDisabled === 'function');
      } catch {
        return [];
      }
    })();
    return {
      formContext,
      entity,
      recordId: id,
      formId,
      formType: id ? 'existing' : 'create',
      readOnly: form.readOnly,
      // Every field control disabled (a read-only form disables them all).
      allDisabled: form.readOnly || (fields.length > 0 && fields.every((c) => c.getDisabled!())),
      valueOf(field) {
        let attribute: AttributeLike | null | undefined;
        try {
          attribute = fc?.getAttribute?.(field);
        } catch {
          attribute = undefined;
        }
        return attribute ? { onForm: true, text: valueText(attribute.getValue()) } : { onForm: false, text: '' };
      },
      lcid,
      orgLcid: typeof org?.languageId === 'number' ? org.languageId : lcid,
      orgName: typeof org?.uniqueName === 'string' ? org.uniqueName : '',
      async resolve(library, functionName) {
        const problem = await libraryProblem(library);
        if (problem) throw new Error(problem);
        const fn = (await ensureHost().promise).resolve(functionName);
        if (!fn) throw new Error(t('script.notFound', { functionName, libraryName: library }));
        return fn;
      },
    };
  }, [ensureHost, libraryProblem]);

  /** A warning of this load: the next load clears it. */
  const warn = useCallback((key: string, text: string) => {
    if (!alive.current) return;
    loadKeys.current.add(key);
    live.current.notify({ key, intent: 'warning', text });
  }, []);
  const warnUnavailable = useCallback(
    (c: RibbonCommand, reason: string) => warn(`ribbon:${c.id}`, t('record.ribbon.unavailable', { label: c.label, reason })),
    [warn],
  );

  // The diffs: once per form load, again on a Refresh. The warnings of the previous load go first.
  useEffect(() => {
    const controller = new AbortController();
    for (const key of loadKeys.current) live.current.clear(key);
    loadKeys.current = new Set();
    lateUnavailable.current = new Set();
    const fail = (e: unknown) => {
      if (controller.signal.aborted) return;
      warn('ribbon-read', t('record.ribbon.readFailed', { message: messageOf(e) }));
      setCommands([]);
    };
    readDiffs(entity, controller.signal).then(
      (diffs) => {
        if (controller.signal.aborted) return;
        try {
          const { commands: parsed, warnings } = formCommands(entity, diffs, live.current.lcid);
          for (const text of warnings) warn(`ribbon-warning:${text}`, text);
          const probe = env();
          const ready: RibbonCommand[] = [];
          for (const c of parsed) {
            const reason = c.unavailable ?? parameterProblem(c, probe);
            if (reason) warnUnavailable(c, reason);
            else ready.push(c);
          }
          setCommands(ready);
        } catch (e) {
          // Not a read failure but still no commands: say so instead of leaving an unhandled rejection.
          fail(e);
        }
      },
      fail,
    );
    return () => controller.abort();
  }, [entity, refreshKey, env, warn, warnUnavailable]);

  // E5: after the load, a save (the record), an attribute change (the values), a change of rights, and refresh(). Each
  // evaluation has its own AbortController; the cleanup aborts the previous one, so the last one wins. Each button follows
  // its own rules as they settle: a slow CustomRule holds back only its own button (Review Focus 1).
  useEffect(() => {
    if (formContext === undefined || commands.length === 0) {
      setVisibleIds((prev) => (prev.size === 0 ? prev : new Set()));
      return;
    }
    const controller = new AbortController();
    const { signal } = controller;
    const ids = new Set(commands.map((c) => c.id));
    setVisibleIds((prev) => ([...prev].every((id) => ids.has(id)) ? prev : new Set([...prev].filter((id) => ids.has(id)))));
    const show = (id: string, shown: boolean) => {
      if (signal.aborted || !alive.current) return;
      setVisibleIds((prev) => {
        if (prev.has(id) === shown) return prev;
        const next = new Set(prev);
        if (shown) next.add(id);
        else next.delete(id);
        return next;
      });
    };
    for (const c of commands) {
      void (async () => {
        if (lateUnavailable.current.has(c.id)) return show(c.id, false);
        for (const library of librariesOf(c)) {
          const reason = await libraryProblem(library);
          if (signal.aborted || !alive.current) return;
          if (reason) {
            if (!lateUnavailable.current.has(c.id)) {
              lateUnavailable.current.add(c.id);
              warnUnavailable(c, reason);
            }
            return show(c.id, false);
          }
        }
        const current = env();
        const shown = (await evaluate(c.displayRules, current, signal)) && (await evaluate(c.enableRules, current, signal));
        show(c.id, shown);
      })();
    }
    return () => controller.abort();
  }, [commands, formContext, form.values, form.record, form.readOnly, tick, env, libraryProblem, warnUnavailable]);

  const refresh = useCallback(() => {
    if (alive.current) setTick((n) => n + 1);
  }, []);

  const run = useCallback(
    (c: RibbonCommand) => {
      // One run per command at a time, and none while the form saves: the action sees the saved form (Review Focus 3).
      const { form, saving } = live.current;
      if (runningRef.current.has(c.id) || form.busy || form.isBusy?.() || saving?.()) return;
      runningRef.current.add(c.id);
      setRunning(new Set(runningRef.current));
      const actionKey = `ribbon-action:${c.id}`;
      live.current.clear(actionKey);
      void (async () => {
        for (const action of c.actions) {
          try {
            // Each action reads the form as the previous one left it.
            await runActions([action], env());
          } catch (e) {
            if (alive.current) {
              live.current.notify({ key: actionKey, intent: 'error', text: t('script.error', { functionName: action.functionName, message: messageOf(e) }) });
            }
            break;
          }
          if (!alive.current) break;
        }
      })().finally(() => {
        runningRef.current.delete(c.id);
        if (!alive.current) return;
        setRunning(new Set(runningRef.current));
        refresh();
      });
    },
    [env, refresh],
  );

  const formBusy = form.busy;
  const visible = useMemo(
    () => commands.filter((c) => visibleIds.has(c.id)).map((command) => ({ command, busy: formBusy || running.has(command.id), run: () => run(command) })),
    [commands, visibleIds, running, formBusy, run],
  );
  return { visible, refresh };
}
