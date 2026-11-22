import { optionsOf } from '../admin/tables/fields/fieldProps';
import type { OptionMeta } from '../admin/tables/metadata';
import type { Cell, Control as ControlModel, Section as SectionModel, Tab as TabModel, WebResourceControl } from '../model/form';
import { labelFor } from '../parsers/formXml';
import { collection, type Collection } from './collection';
import { CONTROL_NOTIFICATION_LEVELS, ControlType, FOOTER_PREFIX, HEADER_PREFIX, TAB_DISPLAY_STATES, type ControlNotificationLevel, type TabDisplayState } from './constants';
import type { Runtime } from './formContext';
import { createExecutionContext, type ExecutionContext } from './executionContext';
import { checkedFrameUrl, frameParameters, initialFrameSrc } from './frameSource';
import { Stage, stub } from './notImplemented';

/** Where a control sits: a section of a tab, the header or the footer. */
export interface ControlSpec {
  name: string;
  cell: Cell;
  model: ControlModel;
  place: 'body' | 'header' | 'footer';
  tab?: TabModel;
  section?: SectionModel;
}

export interface FormLayout {
  controls: ControlSpec[];
  tabs: { tab: TabModel; sections: { section: SectionModel; controls: ControlSpec[] }[] }[];
}

/** The name a control is known by: a field control by its column, any other by its control id. */
const baseName = (model: ControlModel) => (model.kind === 'field' ? model.attribute : model.id);

/**
 * The controls of a form with their Client API names, in FormXml order: a column's first body control is named after the
 * column and the next ones `{column}1`, `{column}2` (Learn, controls collection); header controls are `header_{name}` and
 * footer controls `footer_{name}`, numbered the same way among themselves.
 */
export function layoutOf(form: { tabs: TabModel[]; header: Cell[]; footer: Cell[] }): FormLayout {
  const seen = new Map<string, number>();
  const name = (base: string) => {
    const n = seen.get(base) ?? 0;
    seen.set(base, n + 1);
    return n === 0 ? base : `${base}${n}`;
  };
  const controls: ControlSpec[] = [];
  const tabs = form.tabs.map((tab) => ({
    tab,
    sections: tab.columns.flatMap((column) =>
      column.sections.map((section) => {
        const own = section.rows.flat().flatMap((cell) =>
          cell.control ? [{ name: name(baseName(cell.control)), cell, model: cell.control, place: 'body' as const, tab, section }] : [],
        );
        controls.push(...own);
        return { section, controls: own };
      }),
    ),
  }));
  for (const [cells, prefix, place] of [
    [form.header, HEADER_PREFIX, 'header'],
    [form.footer, FOOTER_PREFIX, 'footer'],
  ] as const) {
    for (const cell of cells) {
      if (cell.control) controls.push({ name: name(prefix + baseName(cell.control)), cell, model: cell.control, place });
    }
  }
  return { controls, tabs };
}

/** The control names of a form by name, for the page that refreshes a subgrid or timeline a script names. */
export function controlCells(form: { tabs: TabModel[]; header: Cell[]; footer: Cell[] }): Map<string, Cell> {
  return new Map(layoutOf(form).controls.map((c) => [c.name, c.cell]));
}

export type Control = Record<string, unknown> & { getName(): string; getControlType(): string };
export type Section = Record<string, unknown> & { getName(): string; controls: Collection<Control> };
export type Tab = Record<string, unknown> & { getName(): string; sections: Collection<Section> };

function controlTypeOf(rt: Runtime, model: ControlModel, cell: Cell): string {
  switch (model.kind) {
    case 'field': {
      // Ruling R10: while a PCF control renders the field (not while it loads or after it fell back).
      if (rt.env.overlay.element(cell).customControl) return ControlType.customcontrol;
      const kind = rt.meta(model.attribute)?.kind;
      if (kind === 'lookup') return ControlType.lookup;
      if (kind === 'choice') return ControlType.choice;
      if (kind === 'choices') return ControlType.choices;
      return ControlType.standard;
    }
    case 'subgrid':
      return ControlType.subgrid;
    case 'quickview':
      return ControlType.quickform;
    case 'timeline':
      return ControlType.timelinewall;
    case 'webresource':
      return model.iframe ? ControlType.iframe : ControlType.webresource;
    default:
      // A control class the FormXml parser does not know (UI-1b's placeholder): Learn's generic `standard` (Task 4 ruling).
      return ControlType.standard;
  }
}

/** One control of the form (Learn, Controls): its state lives in the overlay, keyed by its FormXml cell. */
export function createControl(rt: Runtime, spec: ControlSpec): Control {
  const { cell, model, name } = spec;
  const path = (member: string) => `formContext.getControl("${name}").${member}`;
  const overlay = () => rt.env.overlay;
  const own = () => overlay().element(cell);
  const attributeName = model.kind === 'field' ? model.attribute : undefined;
  const meta = attributeName ? rt.meta(attributeName) : undefined;

  const control: Control = {
    getName: () => name,
    getControlType: () => controlTypeOf(rt, model, cell),
    getLabel: () => own().label ?? labelFor(cell.labels, rt.env.lcid) ?? meta?.displayName ?? '',
    setLabel: (label: unknown) => overlay().setElement(cell, { label: String(label) }),
    getVisible: () => own().visible ?? cell.visible,
    setVisible: (visible: unknown) => overlay().setElement(cell, { visible: visible === true }),
    getDisabled: () => {
      const set = own().disabled;
      if (set !== undefined) return set;
      if (model.kind !== 'field') return false;
      return model.disabled || spec.place === 'footer' || rt.env.state.isReadOnly(model.attribute);
    },
    setDisabled: (disabled: unknown) => overlay().setElement(cell, { disabled: disabled === true }),
    setFocus: () => {
      if (spec.tab) overlay().selectTab(spec.tab);
      overlay().setElement(cell, { focus: (own().focus ?? 0) + 1 });
    },
    getParent: () => {
      if (!spec.tab || !spec.section) return null;
      const tab = (rt.fc.ui.tabs.get as (n: number) => Tab | null)(rt.env.form.tabs.indexOf(spec.tab));
      return tab?.sections.get((s: Section) => sectionModels.get(s) === spec.section)[0] ?? null;
    },
  };

  if (model.kind === 'field') {
    const isLevel = (level: unknown): level is ControlNotificationLevel => CONTROL_NOTIFICATION_LEVELS.includes(level as ControlNotificationLevel);
    Object.assign(control, {
      getAttribute: () => rt.attribute(model.attribute),
      // Learn: an error notification on a control; it blocks the save while it is shown.
      setNotification: (message: unknown, uniqueId?: unknown) => {
        if (typeof message !== 'string') return false;
        overlay().addControlNotification(cell, { message, level: 'ERROR', uniqueId: typeof uniqueId === 'string' ? uniqueId : '' });
        return true;
      },
      clearNotification: (uniqueId?: unknown) => overlay().clearControlNotification(cell, typeof uniqueId === 'string' ? uniqueId : undefined),
      // Ruling C7: the messages are shown, the actions are not.
      addNotification: (notification: unknown) => {
        const n = notification as { messages?: unknown; notificationLevel?: unknown; uniqueId?: unknown } | null;
        const message = Array.isArray(n?.messages) ? n.messages[0] : undefined;
        if (typeof message !== 'string' || !isLevel(n?.notificationLevel) || typeof n?.uniqueId !== 'string') return false;
        overlay().addControlNotification(cell, { message, level: n.notificationLevel, uniqueId: n.uniqueId });
        return true;
      },
      // The shape of its dictionary is not documented: not guessed (Known limit).
      getOutputs: stub(path('getOutputs'), Stage.later),
    });
  }
  if (model.kind !== 'field') {
    // Learn lists the notification members for every control type; UI-1c shows notifications on field controls only.
    for (const member of ['setNotification', 'clearNotification', 'addNotification']) control[member] = stub(path(member), Stage.later);
  }
  if (model.kind === 'field' && meta?.kind === 'lookup') Object.assign(control, lookupMembers(rt, spec, control, path));
  if (model.kind === 'field' && (meta?.kind === 'choice' || meta?.kind === 'choices')) {
    const base = (): OptionMeta[] => own().options ?? optionsOf(meta, rt.env.ctx.fieldContext);
    const all = () => optionsOf(meta, rt.env.ctx.fieldContext);
    Object.assign(control, {
      getOptions: () => base().map((o) => ({ value: o.value, text: o.label })),
      addOption: (option: unknown, index?: unknown) => {
        const o = option as { value?: unknown; text?: unknown } | null;
        if (typeof o?.value !== 'number') return;
        const known = all().find((x) => x.value === o.value);
        const added: OptionMeta = { ...known, value: o.value, label: typeof o.text === 'string' ? o.text : (known?.label ?? String(o.value)) };
        const rest = base().filter((x) => x.value !== o.value);
        const at = typeof index === 'number' ? Math.max(0, Math.min(index, rest.length)) : rest.length;
        overlay().setElement(cell, { options: [...rest.slice(0, at), added, ...rest.slice(at)] });
      },
      removeOption: (value: unknown) => overlay().setElement(cell, { options: base().filter((x) => x.value !== value) }),
      clearOptions: () => overlay().setElement(cell, { options: [] }),
    });
  }
  if (model.kind === 'subgrid') {
    const grid = (member: string) => stub(path(member), Stage.later);
    Object.assign(control, {
      refresh: () => rt.env.refreshControl(name),
      getEntityName: () => model.targetEntity,
      getGrid: grid('getGrid'),
      addOnLoad: grid('addOnLoad'),
      removeOnLoad: grid('removeOnLoad'),
      getFetchXml: grid('getFetchXml'),
      getGridType: grid('getGridType'),
      getRelationship: grid('getRelationship'),
      getUrl: grid('getUrl'),
      getViewSelector: grid('getViewSelector'),
      openRelatedGrid: grid('openRelatedGrid'),
      refreshRibbon: grid('refreshRibbon'),
    });
  }
  if (model.kind === 'webresource') Object.assign(control, frameMembers(rt, spec, model, path));
  // Learn: reloads the timeline; it reads its first page again with the filters the user set.
  if (model.kind === 'timeline') control.refresh = () => rt.env.refreshControl(name);
  return control;
}

/** The members of an IFRAME or web resource control (Learn): the address, the IFRAME element and its window. */
function frameMembers(rt: Runtime, spec: ControlSpec, model: WebResourceControl, path: (m: string) => string) {
  const { cell } = spec;
  const initial = (): string => {
    try {
      return initialFrameSrc(model, frameParameters(rt.env, rt.globalContext()));
    } catch {
      // A refused address is not shown; scripts still read what the form says.
      return model.url;
    }
  };
  const frame = (member: string): HTMLIFrameElement => {
    const el = rt.env.overlay.frame(cell);
    if (!el) throw new Error(`${path(member)}: the frame is not shown. Call it after the control is on screen (its tab is selected and it is visible).`);
    return el;
  };
  return {
    getSrc: () => rt.env.overlay.element(cell).src ?? initial(),
    setSrc: (src: unknown) => {
      if (typeof src !== 'string') throw new Error(`${path('setSrc')}: the address must be a string.`);
      try {
        checkedFrameUrl(src);
      } catch {
        throw new Error(`${path('setSrc')}: ${src} is not allowed; only http and https addresses and web resource paths can be shown.`);
      }
      rt.env.overlay.setElement(cell, { src });
    },
    // Learn: the IFRAME element of the DOM.
    getObject: () => frame('getObject'),
    getInitialUrl: () => initial(),
    // Learn: a promise of the content window; it waits for a frame that is still loading.
    getContentWindow: () =>
      new Promise<Window>((resolve, reject) => {
        let el: HTMLIFrameElement;
        try {
          el = frame('getContentWindow');
        } catch (e) {
          reject(e);
          return;
        }
        const settle = (target: HTMLIFrameElement) => {
          const win = target.contentWindow;
          if (!win) return reject(new Error(`${path('getContentWindow')}: the frame has no window.`));
          let doc: Document | null = null;
          try {
            doc = target.contentDocument;
          } catch {
            // Another origin.
          }
          // A document the shell cannot read (another origin, or a sandboxed frame) never reports its state: the window is what Learn returns.
          if (!doc || (doc.readyState === 'complete' && doc.URL !== 'about:blank')) return resolve(win);
          // A same-origin page that is still loading: wait for it, or for the frame that replaces it (`setSrc`).
          const stop = rt.env.overlay.onFrame(cell, (next) => {
            target.removeEventListener('load', onLoad);
            stop();
            settle(next);
          });
          const onLoad = () => {
            stop();
            if (target.contentWindow) resolve(target.contentWindow);
            else reject(new Error(`${path('getContentWindow')}: the frame has no window.`));
          };
          target.addEventListener('load', onLoad, { once: true });
        };
        settle(el);
      }),
    addOnReadyStateComplete: stub(path('addOnReadyStateComplete'), Stage.later),
    removeOnReadyStateComplete: stub(path('removeOnReadyStateComplete'), Stage.later),
  };
}

/** The members of a lookup control: PreSearch handlers, custom filters and the tables it offers. */
function lookupMembers(rt: Runtime, spec: ControlSpec, control: Control, path: (m: string) => string) {
  const { cell, name } = spec;
  const overlay = () => rt.env.overlay;
  const handlers: ((...args: unknown[]) => unknown)[] = [];
  const meta = rt.meta((spec.model as { attribute: string }).attribute)!;
  /** Runs the PreSearch handlers and returns the filters they added for the table searched. */
  const collect = (target: string): string[] => {
    const filters: { xml: string; entity?: string }[] = [];
    const previous = rt.collecting;
    rt.collecting = { control, filters };
    try {
      let context: ExecutionContext | undefined;
      rt.run(handlers, () => (context ??= createExecutionContext({ formContext: rt.fc, eventSource: control, globalContext: rt.globalContext })));
    } finally {
      rt.collecting = previous;
    }
    return filters.filter((f) => !f.entity || f.entity === target).map((f) => f.xml);
  };
  return {
    addPreSearch: (fn: unknown) => {
      if (typeof fn !== 'function' || handlers.includes(fn as never)) return;
      handlers.push(fn as never);
      overlay().setPreSearch(cell, collect);
    },
    removePreSearch: (fn: unknown) => {
      const at = handlers.indexOf(fn as never);
      if (at >= 0) handlers.splice(at, 1);
      if (handlers.length === 0) overlay().setPreSearch(cell, undefined);
    },
    // Learn: valid inside a PreSearch handler of this control. Elsewhere it has no effect, which is logged.
    addCustomFilter: (filter: unknown, entityLogicalName?: unknown) => {
      if (rt.collecting?.control !== control) {
        console.warn(`addCustomFilter on ${name} was called outside a PreSearch handler and is ignored.`);
        return;
      }
      if (typeof filter !== 'string') throw new Error(`addCustomFilter on ${name}: the filter must be a FetchXML <filter> string.`);
      rt.collecting.filters.push({ xml: filter, entity: typeof entityLogicalName === 'string' && entityLogicalName ? entityLogicalName : undefined });
    },
    getEntityTypes: () => [...(overlay().element(cell).entityTypes ?? targetsOf(rt, meta.logicalName, meta.targets))],
    setEntityTypes: (types: unknown) => {
      if (!Array.isArray(types) || types.some((x) => typeof x !== 'string')) throw new Error(`setEntityTypes on ${name}: pass an array of table names.`);
      overlay().setElement(cell, { entityTypes: [...(types as string[])] });
    },
    addCustomView: stub(path('addCustomView'), Stage.later),
    getDefaultView: stub(path('getDefaultView'), Stage.later),
    setDefaultView: stub(path('setDefaultView'), Stage.later),
    addOnLookupTagClick: stub(path('addOnLookupTagClick'), Stage.later),
    removeOnLookupTagClick: stub(path('removeOnLookupTagClick'), Stage.later),
  };
}

const targetsOf = (rt: Runtime, attribute: string, declared: string[]): string[] => {
  const related = rt.env.ctx.fieldContext.lookups.filter((r) => r.referencingAttribute === attribute).map((r) => r.referencedEntity);
  return [...new Set(related.length ? related : declared)];
};

/** The model behind each section object, for a control's `getParent()`. */
const sectionModels = new WeakMap<Section, SectionModel>();

/** `formContext.ui.tabs`, each with its sections and their controls. */
export function createTabs(rt: Runtime, layout: FormLayout, ui: object): Collection<Tab> {
  const controls = rt.controls;
  const overlay = () => rt.env.overlay;
  const labelOf = (model: { labels: Record<number, string>; name: string }) => overlay().element(model).label ?? labelFor(model.labels, rt.env.lcid) ?? model.name;
  const tabs: Tab[] = layout.tabs.map(({ tab, sections }) => {
    const self: Tab = {
      getName: () => tab.name,
      getLabel: () => labelOf(tab),
      setLabel: (label: unknown) => overlay().setElement(tab, { label: String(label) }),
      getVisible: () => overlay().element(tab).visible ?? tab.visible,
      setVisible: (visible: unknown) => overlay().setElement(tab, { visible: visible === true }),
      // Unified Interface shows one tab at a time: the selected tab is the expanded one.
      getDisplayState: (): TabDisplayState => (selectedTab(rt, layout) === tab ? 'expanded' : 'collapsed'),
      setDisplayState: (state: unknown) => {
        if (!TAB_DISPLAY_STATES.includes(state as TabDisplayState)) throw new Error(`setDisplayState on ${tab.name}: "${String(state)}" is not a display state.`);
        if (state === 'expanded') overlay().selectTab(tab);
      },
      setFocus: () => overlay().selectTab(tab),
      getParent: () => ui,
      sections: collection(() => sectionObjects),
      addTabStateChange: stub(`formContext.ui.tabs.get("${tab.name}").addTabStateChange`, Stage.later),
      removeTabStateChange: stub(`formContext.ui.tabs.get("${tab.name}").removeTabStateChange`, Stage.later),
      getContentType: stub(`formContext.ui.tabs.get("${tab.name}").getContentType`, Stage.later),
      setContentType: stub(`formContext.ui.tabs.get("${tab.name}").setContentType`, Stage.later),
    };
    const sectionObjects: Section[] = sections.map(({ section, controls: own }) => {
      const names = new Set(own.map((c) => c.name));
      const s: Section = {
        getName: () => section.name,
        getLabel: () => labelOf(section),
        setLabel: (label: unknown) => overlay().setElement(section, { label: String(label) }),
        getVisible: () => overlay().element(section).visible ?? section.visible,
        setVisible: (visible: unknown) => overlay().setElement(section, { visible: visible === true }),
        controls: collection(() => controls().filter((c) => names.has(c.getName()))),
        getParent: () => self,
      };
      sectionModels.set(s, section);
      return s;
    });
    return self;
  });
  return collection(() => tabs);
}

/** The tab the form shows: the one a script or the user selected while it is visible, else the first visible one. */
export function selectedTab(rt: Runtime, layout: FormLayout): TabModel | undefined {
  const visible = layout.tabs.map((t) => t.tab).filter((tab) => rt.env.overlay.element(tab).visible ?? tab.visible);
  const picked = rt.env.overlay.selectedTab();
  return visible.find((tab) => tab === picked) ?? visible[0];
}
