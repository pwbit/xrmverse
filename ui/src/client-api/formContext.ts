import type { AttributeMeta } from '../admin/tables/metadata';
import type { RecordContext } from '../apps/record/loadRecord';
import type { RecordForm } from '../apps/record/useRecordForm';
import type { FormModel } from '../model/form';
import type { FormNotification } from '../renderers/classic/form/FormNotifications';
import { t } from '../shell/i18n/t';
import { createAttribute, type Attribute } from './attribute';
import { collection, lookupIn, type Collection } from './collection';
import { EntitySaveOption, FORM_NOTIFICATION_LEVELS, FormType, SaveMode, type FormNotificationLevel } from './constants';
import { createControl, createTabs, layoutOf, type Control, type Tab } from './control';
import { runFunctions, type HandlerSlot, type RunMode } from './events';
import { createExecutionContext, type ExecutionContext } from './executionContext';
import { Stage, stub, stubProperty } from './notImplemented';
import type { UiOverlay } from './uiOverlay';
import { bracedUpper } from './values';

/** How a save the form or a script started ended. */
export type SaveOutcome = 'saved' | 'prevented' | 'failed';

/** What the formContext reads and writes through: the form state, the UI overlay and the page that hosts the form. */
export interface FormContextEnv {
  form: FormModel;
  ctx: RecordContext;
  state: RecordForm;
  overlay: UiOverlay;
  /** The forms of the form selector. */
  forms: FormModel[];
  /** Opens another form of the table, through the page's form selector (the unsaved-changes guard applies). */
  selectForm(id: string): void;
  /** Reloads the subgrid or timeline control `name` (its rows, or its first page with the current filters). */
  refreshControl(name: string): void;
  lcid: number;
  /** The user's time zone (IANA, #131): User Local dates are read and written in it; absent, UTC. */
  timeZone?: string;
  /** Saves as the command bar does: OnSave with `mode`, then the save (and the list after Save & Close). */
  save(mode: number, andClose: boolean): Promise<SaveOutcome>;
  /** Reads the record again; the form's OnLoad does not run again. */
  refresh(): Promise<void>;
  /** Leaves the form (the unsaved-changes guard applies). */
  close(): void;
  /** Runs the FormXml handlers of an OnSave or OnChange event with the run's execution context; settles when they have run. */
  runFormXml(event: 'onsave' | 'onchange', attribute: string | undefined, makeContext: () => ExecutionContext, mode?: RunMode): Promise<void> | void;
  /** Called by `formContext.ui.refreshRibbon()`; the command bar evaluates its custom command rules again. */
  onRefreshRibbon?: () => void;
  /** Shows a script failure as a form notification. */
  notify(notification: FormNotification): void;
  /** The app shell's global context, which `executionContext.getContext()` returns. */
  globalContext?: unknown;
  /** The app settings AsyncOnLoad and AsyncOnSave: OnLoad and OnSave wait for the promises their handlers return. */
  asyncHandlers?: AsyncHandlers;
}

/** The app settings that make the form wait for the promises of OnLoad and OnSave handlers (both off by default). */
export interface AsyncHandlers {
  onLoad: boolean;
  onSave: boolean;
}

type Fn = (...args: unknown[]) => unknown;

/** What the attribute and control objects of one formContext share. */
export interface Runtime {
  /** Throws once the form was left: a formContext is valid only while its form is open. */
  readonly env: FormContextEnv;
  readonly fc: FormContext;
  meta(name: string): AttributeMeta | undefined;
  attribute(name: string): Attribute | null;
  attributes(): Attribute[];
  controls(): Control[];
  onChange: Map<string, Fn[]>;
  raiseChange(name: string): void;
  run(fns: readonly Fn[], makeContext: () => ExecutionContext): void;
  /** The global context of `executionContext.getContext()`. */
  globalContext(): unknown;
  /** The lookup control whose PreSearch handlers are running, and the filters they add. */
  collecting?: { control: object; filters: { xml: string; entity?: string }[] };
}

export interface FormContext {
  data: Record<string, unknown> & { entity: Record<string, unknown> & { attributes: Collection<Attribute> }; attributes: Collection<Attribute> };
  ui: Record<string, unknown> & { controls: Collection<Control>; tabs: Collection<Tab> };
  getAttribute(arg?: unknown): Attribute | Attribute[] | null;
  getControl(arg?: unknown): Control | Control[] | null;
}

interface Internals {
  rt: Runtime;
  onSave: Fn[];
  onPostSave: Fn[];
  dispose(): void;
}
const internals = new WeakMap<FormContext, Internals>();

/**
 * The OnSave event arguments (Learn, Save event arguments). `disableAsyncTimeout` and `preventDefaultOnError` apply to the
 * handler that calls them: each handler of the run gets its own slot.
 */
function saveEventArgs(mode: number) {
  let prevented = false;
  let current = { noTimeout: false, preventOnError: false };
  const args = {
    getSaveMode: () => mode,
    preventDefault: () => void (prevented = true),
    isDefaultPrevented: () => prevented,
    preventDefaultOnError: () => void (current.preventOnError = true),
    disableAsyncTimeout: () => void (current.noTimeout = true),
  };
  const slot = (): HandlerSlot => {
    const mine = { noTimeout: false, preventOnError: false };
    current = mine;
    return {
      timeoutDisabled: () => mine.noTimeout,
      failed: () => {
        if (mine.preventOnError) prevented = true;
      },
    };
  };
  return { args, slot };
}

const notSaved = (outcome: SaveOutcome) => ({ errorCode: 0, message: t(outcome === 'prevented' ? 'script.savePrevented' : 'script.notSaved') });

/**
 * The documented Client API `formContext` over one open form. It reads and writes only through the form state
 * (`RecordForm`) and the UI overlay, never the DOM. Members of the UI-1c surface work; other documented members throw "not
 * available in this version"; undocumented names are `undefined`.
 */
export function createFormContext(env: FormContextEnv): FormContext {
  let disposed = false;
  const layout = layoutOf(env.form);
  const onChange = new Map<string, Fn[]>();
  const fc = {} as FormContext;
  const byName = new Map(env.ctx.entity.attributes.map((a) => [a.logicalName, a]));
  const attrs: Attribute[] = [];
  const attrMap = new Map<string, Attribute>();
  const rt: Runtime = {
    get env() {
      if (disposed) throw new Error('This formContext belongs to a form that is no longer open.');
      return env;
    },
    fc,
    meta: (name) => byName.get(name),
    attribute: (name) => attrMap.get(name) ?? null,
    attributes: () => attrs,
    controls: () => controls,
    onChange,
    raiseChange(name) {
      const attribute = attrMap.get(name);
      if (!attribute) return;
      let context: ExecutionContext | undefined;
      const make = () => (context ??= createExecutionContext({ formContext: fc, eventSource: attribute, globalContext: rt.globalContext }));
      rt.env.runFormXml('onchange', name, make);
      rt.run(onChange.get(name) ?? [], make);
    },
    run: (fns, make) => runFunctions([...fns], make, (n) => rt.env.notify(n)),
    globalContext: () => rt.env.globalContext,
  };
  for (const name of new Set(layout.controls.flatMap((c) => (c.model.kind === 'field' ? [c.model.attribute] : [])))) {
    const meta = byName.get(name);
    if (!meta) continue;
    const a = createAttribute(rt, meta);
    attrs.push(a);
    attrMap.set(name, a);
  }
  const controls: Control[] = layout.controls.map((spec) => createControl(rt, spec));
  const ui = {} as FormContext['ui'];
  const tabs = createTabs(rt, layout, ui);
  const onSave: Fn[] = [];
  const onPostSave: Fn[] = [];
  const addTo = (list: Fn[]) => (fn: unknown) => {
    if (typeof fn === 'function' && !list.includes(fn as Fn)) list.push(fn as Fn);
  };
  const removeFrom = (list: Fn[]) => (fn: unknown) => {
    const at = list.indexOf(fn as Fn);
    if (at >= 0) list.splice(at, 1);
  };

  const state = () => rt.env.state;
  // A create form has its id as soon as the record is created, before the page loads the new record into it.
  const recordId = () => state().latest().id;
  const getId = () => {
    const id = recordId();
    return id ? bracedUpper(id) : '';
  };
  const primaryValue = (): string | null => {
    const primary = rt.env.ctx.entity.primaryNameAttribute;
    const a = primary ? attrMap.get(primary) : undefined;
    const v = a ? a.getValue() : primary ? (state().record ?? rt.env.ctx.record)?.[primary] : undefined;
    return typeof v === 'string' ? v : null;
  };
  const isDirty = () => attrs.some((a) => a.getIsDirty());
  const isValid = () =>
    rt.env.overlay.blockingMessages().length === 0 &&
    attrs.every((a) => {
      const name = a.getName();
      if (!state().isRequired(name) || state().isReadOnly(name)) return true;
      const v = a.getValue();
      return v !== null && v !== '' && !(Array.isArray(v) && v.length === 0);
    });
  const saveAs = async (mode: number, andClose: boolean): Promise<void> => {
    const outcome = await rt.env.save(mode, andClose);
    if (outcome !== 'saved') throw notSaved(outcome);
  };

  const entity: FormContext['data']['entity'] = {
    attributes: collection(() => attrs),
    getId,
    getEntityName: () => rt.env.ctx.entity.logicalName,
    getEntitySetName: () => rt.env.ctx.entity.entitySetName ?? '',
    getEntityReference: () => ({ id: getId(), entityType: rt.env.ctx.entity.logicalName, name: primaryValue() }),
    getIsDirty: isDirty,
    getPrimaryAttributeValue: () => primaryValue(),
    getDataXml: stub('formContext.data.entity.getDataXml', Stage.later),
    isValid,
    save: (saveOption?: unknown) => {
      if (saveOption === EntitySaveOption.SaveAndNew) stub('formContext.data.entity.save("saveandnew")', Stage.later)();
      if (saveOption !== undefined && saveOption !== null && saveOption !== EntitySaveOption.SaveAndClose) {
        throw new Error(`formContext.data.entity.save: "${String(saveOption)}" is not a save option.`);
      }
      const close = saveOption === EntitySaveOption.SaveAndClose;
      void saveAs(close ? SaveMode.SaveAndClose : SaveMode.Save, close).catch(() => undefined);
    },
    addOnSave: addTo(onSave),
    removeOnSave: removeFrom(onSave),
    addOnPostSave: addTo(onPostSave),
    removeOnPostSave: removeFrom(onPostSave),
  };

  const data: FormContext['data'] = {
    entity,
    // Learn: the non-table columns of the form; a classic form has none.
    attributes: collection<Attribute>(() => []),
    getIsDirty: isDirty,
    isValid,
    save: (saveOptions?: { saveMode?: unknown }) =>
      saveAs(typeof saveOptions?.saveMode === 'number' ? saveOptions.saveMode : SaveMode.Save, false),
    refresh: async (save?: unknown) => {
      if (save === true) await saveAs(SaveMode.Save, false);
      await rt.env.refresh();
    },
    addOnLoad: stub('formContext.data.addOnLoad', Stage.later),
    removeOnLoad: stub('formContext.data.removeOnLoad', Stage.later),
  };
  // Business process flows are out of UI-1 (spec §5.4).
  stubProperty(data, 'process', 'formContext.data.process', Stage.later);

  const items = rt.env.forms.map((form) => ({
    getId: () => form.id,
    getLabel: () => form.name,
    navigate: () => rt.env.selectForm(form.id),
    getVisible: () => rt.env.overlay.element(form).visible ?? true,
    setVisible: (visible: unknown) => rt.env.overlay.setElement(form, { visible: visible === true }),
  }));
  const isLevel = (level: unknown): level is FormNotificationLevel => FORM_NOTIFICATION_LEVELS.includes(level as FormNotificationLevel);
  Object.assign(ui, {
    controls: collection(() => controls),
    tabs,
    formSelector: {
      // Learn: with a single form there is no current item.
      getCurrentItem: () => (items.length < 2 ? null : (items.find((i) => i.getId() === rt.env.form.id) ?? null)),
      items: collection(() => items),
    },
    getFormType: () => {
      const s = state();
      if (!recordId()) return FormType.Create;
      // Learn, getFormType: Disabled is the form of a deactivated record; Read Only, of one the user may not update.
      if (s.inactiveLabel !== undefined) return FormType.Disabled;
      return s.readOnly ? FormType.ReadOnly : FormType.Update;
    },
    setFormNotification: (message: unknown, level: unknown, uniqueId: unknown) => {
      if (typeof message !== 'string' || !isLevel(level) || typeof uniqueId !== 'string') return false;
      rt.env.overlay.setFormNotification({ message, level, uniqueId });
      return true;
    },
    clearFormNotification: (uniqueId: unknown) => typeof uniqueId === 'string' && rt.env.overlay.clearFormNotification(uniqueId),
    close: () => rt.env.close(),
    // UI-1e ruling E5: asks the host to evaluate the custom command rules again; a no-op when the host set no callback.
    refreshRibbon: () => rt.env.onRefreshRibbon?.(),
    setFormEntityName: (name: unknown) => {
      if (typeof name === 'string') rt.env.overlay.setFormEntityName(name);
    },
    addOnLoad: stub('formContext.ui.addOnLoad', Stage.later),
    removeOnLoad: stub('formContext.ui.removeOnLoad', Stage.later),
    getViewPortHeight: stub('formContext.ui.getViewPortHeight', Stage.later),
    getViewPortWidth: stub('formContext.ui.getViewPortWidth', Stage.later),
  });
  stubProperty(ui, 'navigation', 'formContext.ui.navigation', Stage.later);
  stubProperty(ui, 'process', 'formContext.ui.process', Stage.later);
  stubProperty(ui, 'quickForms', 'formContext.ui.quickForms', Stage.later);

  Object.assign(fc, {
    data,
    ui,
    getAttribute: (arg?: unknown) => lookupIn(entity.attributes, arg),
    getControl: (arg?: unknown) => lookupIn(ui.controls, arg),
  });
  internals.set(fc, {
    rt,
    onSave,
    onPostSave,
    dispose: () => {
      for (const c of layout.controls) env.overlay.setPreSearch(c.cell, undefined);
      disposed = true;
    },
  });
  return fc;
}

/** Raises the OnChange event of `attribute` after a user change (FormXml handlers, then those added with `addOnChange`). */
export function raiseOnChange(fc: FormContext, attribute: string): void {
  internals.get(fc)?.rt.raiseChange(attribute);
}

/**
 * Raises OnSave with `mode`; true when the save must not run: a handler called `preventDefault()`, or one that called
 * `preventDefaultOnError()` failed. With the app setting AsyncOnSave on, each handler's promise is awaited first.
 */
export async function raiseOnSave(fc: FormContext, mode: number): Promise<boolean> {
  const inner = internals.get(fc);
  if (!inner) return false;
  const save = saveEventArgs(mode);
  let context: ExecutionContext | undefined;
  const make = () => (context ??= createExecutionContext({ formContext: fc, eventArgs: save.args, globalContext: inner.rt.globalContext }));
  const env = inner.rt.env;
  const run: RunMode = { wait: !!env.asyncHandlers?.onSave, slot: save.slot };
  await env.runFormXml('onsave', undefined, make, run);
  await runFunctions(inner.onSave, make, (n) => env.notify(n), run);
  return save.args.isDefaultPrevented();
}

/** Raises PostSave after a save that ran (Learn, PostSave event arguments). */
export function raiseOnPostSave(fc: FormContext, saved: boolean): void {
  const inner = internals.get(fc);
  if (!inner || inner.onPostSave.length === 0) return;
  const args = {
    getEntityReference: () => (fc.data.entity.getEntityReference as () => unknown)(),
    getIsSaveSuccess: () => saved,
    getSaveErrorInfo: () => (saved ? null : notSaved('failed')),
  };
  let context: ExecutionContext | undefined;
  inner.rt.run(inner.onPostSave, () => (context ??= createExecutionContext({ formContext: fc, eventArgs: args, globalContext: inner.rt.globalContext })));
}

/** The form was left: every later call of this formContext throws, and its lookups lose their PreSearch handlers. */
export function disposeFormContext(fc: FormContext): void {
  internals.get(fc)?.dispose();
}
