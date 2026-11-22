import { childElements } from '../../parsers/xml';
import { lower } from './formCells';
import type { FormDesign } from './formDesign';
import { newGuid } from './formIds';
import { append, detach, insertBlock } from './xmlIndent';

/** A `<Library>` of `<formLibraries>` (G6). */
export interface DesignLibrary {
  key: string;
  /** The JS web resource name. */
  name: string;
  /** `libraryUniqueId`, as written (`{id}`); kept by Save as. */
  uniqueId: string;
}

/** A `<Handler>` of an event the designer edits (G6). */
export interface DesignHandler {
  key: string;
  functionName: string;
  /** `libraryName`: one of the form's libraries. */
  library: string;
  enabled: boolean;
  /** The raw comma-separated list of JavaScript values. */
  parameters: string;
  /** `passExecutionContext`. */
  passContext: boolean;
  /** `handlerUniqueId`, as written (`{id}`); kept by Save as. */
  uniqueId: string;
}

/** An `<event>` the designer edits: OnLoad and OnSave of the form, OnChange of a column, not `application="true"`. */
export interface DesignEvent {
  key: string;
  name: 'onload' | 'onsave' | 'onchange';
  /** The column of an OnChange event. */
  attribute?: string;
  handlers: DesignHandler[];
}

export type HandlerFields = Omit<DesignHandler, 'key' | 'uniqueId'>;

const flag = (el: Element, name: string, fallback: boolean): boolean => {
  const v = el.getAttribute(name)?.trim().toLowerCase();
  return v === undefined ? fallback : v === 'true' || v === '1';
};

const libraryEls = (form: Element) => childElements(form, 'formLibraries').flatMap((l) => childElements(l, 'Library'));
const eventEls = (form: Element) => childElements(form, 'events').flatMap((e) => childElements(e, 'event'));
const handlerEls = (event: Element) => childElements(event, 'Handlers').flatMap((h) => childElements(h, 'Handler'));

/** The name the designer edits the `<event>` as, or undefined for an event it leaves alone ({@link otherEvents}). */
function modeled(el: Element): DesignEvent['name'] | undefined {
  if (flag(el, 'application', false)) return undefined;
  const name = lower(el.getAttribute('name'));
  const attribute = lower(el.getAttribute('attribute'));
  if (name === 'onchange') return attribute ? 'onchange' : undefined;
  if (name === 'onload' || name === 'onsave') return attribute ? undefined : name;
  return undefined;
}

function handlerOf(el: Element, key: string): DesignHandler {
  return {
    key,
    functionName: el.getAttribute('functionName') ?? '',
    library: el.getAttribute('libraryName') ?? '',
    enabled: flag(el, 'enabled', true),
    parameters: el.getAttribute('parameters') ?? '',
    passContext: flag(el, 'passExecutionContext', false),
    uniqueId: el.getAttribute('handlerUniqueId') ?? '',
  };
}

/** The design of the `<form>` element's scripts and the elements by key: the keys are positions, so a copy reads alike. */
function scan(form: Element) {
  const elements = new Map<string, Element>();
  const libraries = libraryEls(form).map((el, i): DesignLibrary => {
    const key = `lib:${i}`;
    elements.set(key, el);
    return { key, name: el.getAttribute('name') ?? '', uniqueId: el.getAttribute('libraryUniqueId') ?? '' };
  });
  const events: DesignEvent[] = [];
  eventEls(form).forEach((el, i) => {
    const name = modeled(el);
    if (!name) return;
    const key = `ev:${i}`;
    elements.set(key, el);
    const handlers = handlerEls(el).map((h, j) => {
      const hk = `${key}:${j}`;
      elements.set(hk, h);
      return handlerOf(h, hk);
    });
    const attribute = el.getAttribute('attribute')?.trim();
    events.push({ key, name, ...(name === 'onchange' && attribute ? { attribute } : {}), handlers });
  });
  return { libraries, events, elements };
}

/** The libraries and the edited events of the `<form>` element `form`. */
export function readEvents(form: Element): Pick<FormDesign, 'libraries' | 'events'> {
  const { libraries, events } = scan(form);
  return { libraries, events };
}

export const eventsJson = (d: Pick<FormDesign, 'libraries' | 'events'>) => JSON.stringify([d.libraries, d.events]);

/** Whether the designer edits the `<event>` `el`: an OnLoad, OnSave or OnChange of the form's own `<events>` (G6). */
export const isEdited = (form: Element, el: Element): boolean => el.parentElement?.parentElement === form && modeled(el) !== undefined;

/**
 * Every `<event>` of every `<events>` in `form`, in document order: the form's own and those of its tabs (where Dataverse
 * writes `TabStateChange`), sections and controls.
 */
const allEventEls = (form: Element) =>
  Array.from(form.getElementsByTagName('event')).filter((el) => el.parentElement?.tagName === 'events');

/**
 * The names of the events the designer does not edit (any other name, `application="true"`, or under a tab, section or
 * control), in document order.
 */
export function otherEvents(d: FormDesign): string[] {
  const form = d.source.documentElement;
  return allEventEls(form).filter((el) => !isEdited(form, el)).map((el) => el.getAttribute('name') ?? '');
}

/**
 * The handlers the designer does not edit, which still load their libraries: every `<Handler>` of every `<events>` (tab
 * events included) other than those in the `<Handlers>` of an edited event (the `<InternalHandlers>` of an edited event
 * included).
 */
function otherHandlers(d: FormDesign): DesignHandler[] {
  const form = d.source.documentElement;
  return allEventEls(form).flatMap((el, i) => {
    const edited = isEdited(form, el) ? new Set(handlerEls(el)) : new Set<Element>();
    return Array.from(el.getElementsByTagName('Handler')).filter((h) => !edited.has(h)).map((h, j) => handlerOf(h, `other:${i}:${j}`));
  });
}

// ---------------------------------------------------------------------------------------------------------------------
// Operations: each returns a new design, or `d` itself when nothing changes.

const hasLibrary = (d: FormDesign, name: string) => d.libraries.some((l) => lower(l.name) === lower(name));

/** `items` with the item `key` moved by `by` places; undefined when it is not there or would leave the list. */
function moved<T extends { key: string }>(items: T[], key: string, by: -1 | 1): T[] | undefined {
  const i = items.findIndex((x) => x.key === key);
  const j = i + by;
  if (i < 0 || j < 0 || j >= items.length) return undefined;
  const out = [...items];
  [out[i], out[j]] = [out[j], out[i]];
  return out;
}

/** Appends a new `<Library>` (a JS web resource name); `d` for a blank name or one already on the form. */
export function addLibrary(d: FormDesign, name: string): FormDesign {
  const trimmed = name.trim();
  if (!trimmed || hasLibrary(d, trimmed)) return d;
  return { ...d, libraries: [...d.libraries, { key: `lib:${newGuid()}`, name: trimmed, uniqueId: newGuid() }] };
}

export function moveLibrary(d: FormDesign, key: string, by: -1 | 1): FormDesign {
  const libraries = moved(d.libraries, key, by);
  return libraries ? { ...d, libraries } : d;
}

/**
 * Removes a library; blocked while any handler names it, those of the events the designer does not edit included. `d`
 * for an unknown key.
 */
export function removeLibrary(d: FormDesign, key: string): FormDesign | { blockedBy: DesignHandler[] } {
  const library = d.libraries.find((l) => l.key === key);
  if (!library) return d;
  const blockedBy = [...d.events.flatMap((e) => e.handlers), ...otherHandlers(d)]
    .filter((h) => lower(h.library) === lower(library.name));
  if (blockedBy.length > 0) return { blockedBy };
  return { ...d, libraries: d.libraries.filter((l) => l !== library) };
}

/** The fields with the function name trimmed; undefined when it is blank or the library is not one of the form's. */
function valid(d: FormDesign, h: HandlerFields): HandlerFields | undefined {
  const functionName = h.functionName.trim();
  if (!functionName || !hasLibrary(d, h.library)) return undefined;
  return { functionName, library: h.library, enabled: h.enabled, parameters: h.parameters, passContext: h.passContext };
}

/**
 * Appends a handler to the form's OnLoad or OnSave event, or to the OnChange event of `attribute` (required for
 * OnChange, ignored otherwise); the event is created when there is none. `d` for a blank function name or a library
 * that is not on the form.
 */
export function addHandler(d: FormDesign, event: DesignEvent['name'], attribute: string | undefined, h: HandlerFields): FormDesign {
  const fields = valid(d, h);
  const column = event === 'onchange' ? attribute?.trim() : undefined;
  if (!fields || (event === 'onchange' && !column)) return d;
  const handler: DesignHandler = { key: `h:${newGuid()}`, ...fields, uniqueId: newGuid() };
  const target = d.events.find((e) => e.name === event && lower(e.attribute) === lower(column));
  if (target) {
    return { ...d, events: d.events.map((e) => (e === target ? { ...e, handlers: [...e.handlers, handler] } : e)) };
  }
  const added: DesignEvent = { key: `ev:${newGuid()}`, name: event, ...(column ? { attribute: column } : {}), handlers: [handler] };
  return { ...d, events: [...d.events, added] };
}

function mapHandlers(d: FormDesign, key: string, f: (handlers: DesignHandler[], i: number) => DesignHandler[] | undefined): FormDesign {
  const event = d.events.find((e) => e.handlers.some((h) => h.key === key));
  if (!event) return d;
  const handlers = f(event.handlers, event.handlers.findIndex((h) => h.key === key));
  if (!handlers) return d;
  // An event left with no handler is removed (G6).
  const events = handlers.length === 0
    ? d.events.filter((e) => e !== event)
    : d.events.map((e) => (e === event ? { ...e, handlers } : e));
  return { ...d, events };
}

/** Edits a handler; `d` for an unknown key, no change, a blank function name or a library that is not on the form. */
export function setHandler(d: FormDesign, key: string, patch: Partial<HandlerFields>): FormDesign {
  return mapHandlers(d, key, (handlers, i) => {
    const h = handlers[i];
    const fields = valid(d, { ...h, ...patch });
    if (!fields) return undefined;
    const next = { ...h, ...fields };
    if (JSON.stringify(next) === JSON.stringify(h)) return undefined;
    return handlers.map((x, j) => (j === i ? next : x));
  });
}

/** Removes an event with its handlers (an orphan OnChange, see `orphanChangeEvents`); `d` for an unknown key. */
export function dropEvent(d: FormDesign, key: string): FormDesign {
  const events = d.events.filter((e) => e.key !== key);
  return events.length === d.events.length ? d : { ...d, events };
}

/** Removes a handler; the event it leaves empty goes too. */
export function removeHandler(d: FormDesign, key: string): FormDesign {
  return mapHandlers(d, key, (handlers, i) => handlers.filter((_, j) => j !== i));
}

/** Moves a handler within its event. */
export function moveHandler(d: FormDesign, key: string, by: -1 | 1): FormDesign {
  return mapHandlers(d, key, (handlers) => moved(handlers, key, by));
}

// ---------------------------------------------------------------------------------------------------------------------
// Writing

function create(doc: Document, tag: string, attributes: Record<string, string>): Element {
  const el = doc.createElement(tag);
  for (const [name, value] of Object.entries(attributes)) el.setAttribute(name, value);
  return el;
}

/** Sets `name` only when it differs, so an untouched element serializes as it was. */
function setText(el: Element, name: string, value: string) {
  if ((el.getAttribute(name) ?? '') !== value) el.setAttribute(name, value);
}

function setFlag(el: Element, name: string, value: boolean, fallback: boolean) {
  if (flag(el, name, fallback) !== value) el.setAttribute(name, String(value));
}

/**
 * Makes `current` (elements of `holder`) read as `wanted`: each wanted element takes the place of a current one in turn,
 * so the whitespace between them stays; the rest are appended with the indentation of the last one, or removed.
 */
function sync(holder: Element, current: Element[], wanted: Element[]) {
  if (current.length === wanted.length && current.every((e, i) => e === wanted[i])) return;
  const doc = holder.ownerDocument;
  const slots = current.map((el) => {
    const marker = doc.createComment('');
    el.parentNode!.insertBefore(marker, el);
    el.parentNode!.removeChild(el);
    return marker;
  });
  wanted.forEach((el, i) => {
    if (i < slots.length) slots[i].parentNode!.replaceChild(el, slots[i]);
    else append(holder, el);
  });
  for (const marker of slots.slice(wanted.length)) detach(marker);
}

/** The element after the body blocks (`tabs`, `header`, `footer`), where a new script block goes. */
function afterBody(form: Element): Element | null {
  const body = Array.from(form.children).filter((c) => ['tabs', 'header', 'footer'].includes(c.tagName)).pop();
  return body ? body.nextElementSibling : form.firstElementChild;
}

/** The first `<tag>` of `form`, created before `ref` when there is none. */
function block(form: Element, tag: string, ref: () => Element | null): Element {
  const found = childElements(form, tag)[0];
  if (found) return found;
  const el = form.ownerDocument.createElement(tag);
  insertBlock(form, el, ref());
  return el;
}

function writeHandler(doc: Document, el: Element | undefined, h: DesignHandler): Element {
  if (!el) {
    return create(doc, 'Handler', {
      functionName: h.functionName, libraryName: h.library, handlerUniqueId: h.uniqueId, enabled: String(h.enabled),
      parameters: h.parameters, passExecutionContext: String(h.passContext),
    });
  }
  setText(el, 'functionName', h.functionName);
  setText(el, 'libraryName', h.library);
  setText(el, 'handlerUniqueId', h.uniqueId);
  setFlag(el, 'enabled', h.enabled, true);
  setText(el, 'parameters', h.parameters);
  setFlag(el, 'passExecutionContext', h.passContext, false);
  return el;
}

/**
 * Removes the blocks (`<formLibraries>`, `<events>`) that held elements before the write and hold none now: the designer
 * emptied them (G6). An F7 drop counts as a designer edit.
 */
export function dropEmptied(blocks: Element[]) {
  for (const b of blocks) if (b.parentNode && b.children.length === 0) detach(b);
}

/**
 * Removes the user handlers of an `<event>` the design removed: its `<Handlers>`, or the whole `<event>` when nothing else
 * is in it. A system `<InternalHandlers>` (out-of-box forms) keeps the event.
 */
function removeEvent(el: Element) {
  const rest = Array.from(el.children).filter((c) => c.tagName !== 'Handlers');
  if (rest.length === 0) detach(el);
  else for (const h of childElements(el, 'Handlers')) detach(h);
}

/**
 * Writes the design's libraries and edited events into the `<form>` element `form` (a copy of the source, read as
 * `before` by {@link readEvents}). What did not change keeps its element and text; events the designer does not edit are
 * never touched. An `<event>` the design removed goes, and a `<formLibraries>` or `<events>` the design emptied goes.
 */
export function writeEvents(form: Element, d: Pick<FormDesign, 'libraries' | 'events'>) {
  const doc = form.ownerDocument;
  const { libraries, events, elements } = scan(form);

  if (JSON.stringify(libraries) !== JSON.stringify(d.libraries)) {
    const holders = childElements(form, 'formLibraries').filter((l) => l.children.length > 0);
    const current = libraryEls(form);
    const wanted = d.libraries.map((l) => {
      const el = elements.get(l.key);
      if (!el) return create(doc, 'Library', { name: l.name, libraryUniqueId: l.uniqueId });
      setText(el, 'name', l.name);
      setText(el, 'libraryUniqueId', l.uniqueId);
      return el;
    });
    if (wanted.length > 0) {
      const holder = current[0]?.parentNode as Element | undefined
        ?? block(form, 'formLibraries', () => childElements(form, 'events')[0] ?? afterBody(form));
      sync(holder, current, wanted);
    } else {
      for (const el of current) detach(el);
    }
    dropEmptied(holders);
  }

  if (JSON.stringify(events) === JSON.stringify(d.events)) return;
  const holders = childElements(form, 'events').filter((e) => e.children.length > 0);
  const kept = new Set(d.events.map((e) => e.key));
  for (const e of events) if (!kept.has(e.key)) removeEvent(elements.get(e.key)!);
  const was = new Map(events.map((e) => [e.key, e]));
  for (const e of d.events) {
    const el = elements.get(e.key);
    const before = was.get(e.key);
    if (el && before) {
      if (JSON.stringify(before.handlers) === JSON.stringify(e.handlers)) continue;
      const current = handlerEls(el);
      const wanted = e.handlers.map((h) => writeHandler(doc, elements.get(h.key), h));
      let holder = current[0]?.parentNode as Element | undefined ?? childElements(el, 'Handlers')[0];
      if (!holder) {
        holder = doc.createElement('Handlers');
        append(el, holder);
      }
      sync(holder, current, wanted);
      continue;
    }
    const added = create(doc, 'event', {
      name: e.name, application: 'false', active: 'false', ...(e.attribute ? { attribute: e.attribute } : {}),
    });
    const handlers = doc.createElement('Handlers');
    for (const h of e.handlers) handlers.appendChild(writeHandler(doc, undefined, h));
    added.appendChild(handlers);
    const holder = block(form, 'events', () => {
      // After <formLibraries> (at the end when it is the last block), else after the body.
      const libraries = childElements(form, 'formLibraries')[0];
      return libraries ? libraries.nextElementSibling : afterBody(form);
    });
    append(holder, added);
  }
  dropEmptied(holders);
}
