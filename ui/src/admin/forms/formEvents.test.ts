import { describe, expect, it } from 'vitest';
import invented from '../../parsers/__fixtures__/forms/invented-all-features.xml?raw';
import { CLASS_ID } from '../../parsers/controlClass';
import { childElements, parseXml } from '../../parsers/xml';
import type { AttributeMeta } from '../tables/metadata';
import {
  addField, addHeaderField, containerImpact, fieldImpact, orphanChangeEvents, readForm, removeField, removeHeaderField, removeSection,
  writeForm, type DesignCell, type FormDesign,
} from './formDesign';
import { blocksOf, formRow } from './formDesignTestSupport';
import {
  addHandler, addLibrary, dropEvent, moveHandler, moveLibrary, otherEvents, removeHandler, removeLibrary, setHandler, type DesignHandler,
} from './formEvents';

const TEXT = CLASS_ID.text;
const LIB_A = '{30000000-0000-0000-0000-000000000001}';
const LIB_B = '{30000000-0000-0000-0000-000000000002}';
const H_LOAD = '{40000000-0000-0000-0000-000000000001}';
const H_LOAD2 = '{40000000-0000-0000-0000-000000000002}';
const H_SAVE = '{40000000-0000-0000-0000-000000000003}';
const H_CHANGE = '{40000000-0000-0000-0000-000000000004}';
const H_TAB = '{40000000-0000-0000-0000-000000000005}';
const H_APP = '{40000000-0000-0000-0000-000000000006}';

const cell = (n: number, attribute: string) => `
                <row>
                  <cell id="{10000000-0000-0000-0000-00000000000${n}}">
                    <labels><label description="${attribute}" languagecode="1033" /></labels>
                    <control id="${attribute}" classid="${TEXT}" datafieldname="${attribute}" />
                  </cell>
                </row>`;

/** A form with two libraries, form handlers, a field OnChange handler, a tab event and an application event. */
const FORM = `<form>
  <tabs>
    <tab name="general" id="{10000000-0000-0000-0000-000000000001}">
      <labels />
      <columns>
        <column width="100%">
          <sections>
            <section name="main" id="{10000000-0000-0000-0000-000000000002}" columns="1">
              <labels />
              <rows>${cell(3, 'name')}${cell(4, 'telephone1')}
              </rows>
            </section>
          </sections>
        </column>
      </columns>
    </tab>
  </tabs>
  <formLibraries>
    <Library name="new_/a.js" libraryUniqueId="${LIB_A}" />
    <Library name="new_/b.js" libraryUniqueId="${LIB_B}" />
  </formLibraries>
  <events>
    <event name="onload" application="false" active="false">
      <Handlers>
        <Handler functionName="A.onLoad" libraryName="new_/a.js" handlerUniqueId="${H_LOAD}" enabled="true" parameters="" passExecutionContext="true" />
        <Handler functionName="A.second" libraryName="new_/a.js" handlerUniqueId="${H_LOAD2}" enabled="false" parameters="'x', 1" passExecutionContext="false" />
      </Handlers>
    </event>
    <event name="onsave" application="false" active="false">
      <Handlers>
        <Handler functionName="B.onSave" libraryName="new_/b.js" handlerUniqueId="${H_SAVE}" enabled="true" parameters="" passExecutionContext="true" />
      </Handlers>
    </event>
    <event name="onchange" application="false" active="false" attribute="telephone1">
      <Handlers>
        <Handler functionName="A.phone" libraryName="new_/a.js" handlerUniqueId="${H_CHANGE}" enabled="true" parameters="" passExecutionContext="true" />
      </Handlers>
    </event>
    <event name="TabStateChange" application="false" active="false">
      <Handlers>
        <Handler functionName="B.tab" libraryName="new_/b.js" handlerUniqueId="${H_TAB}" enabled="true" parameters="" passExecutionContext="true" />
      </Handlers>
    </event>
    <event name="onload" application="true" active="true">
      <InternalHandlers>
        <Handler functionName="Mscrm.app" libraryName="Mscrm.js" handlerUniqueId="${H_APP}" enabled="true" parameters="" passExecutionContext="false" />
      </InternalHandlers>
    </event>
  </events>
</form>`;

const read = (xml = FORM): FormDesign => readForm(formRow(xml), 1033);
const formOf = (xml: string) => parseXml(xml).documentElement;
const serialize = (n: Node) => new XMLSerializer().serializeToString(n);
const libraryEls = (xml: string) => childElements(formOf(xml), 'formLibraries').flatMap((l) => childElements(l, 'Library'));
const eventEls = (xml: string) => childElements(formOf(xml), 'events').flatMap((e) => childElements(e, 'event'));
const handlerEls = (event: Element) => childElements(event, 'Handlers').flatMap((h) => childElements(h, 'Handler'));
/** Each `<event>` of `xml` with its name, attribute and handler ids. */
const eventShape = (xml: string) => eventEls(xml).map((e) => ({
  name: e.getAttribute('name'), attribute: e.getAttribute('attribute') ?? undefined,
  handlers: handlerEls(e).map((h) => h.getAttribute('handlerUniqueId')),
}));
const handlerOf = (d: FormDesign, uniqueId: string): DesignHandler =>
  d.events.flatMap((e) => e.handlers).find((h) => h.uniqueId === uniqueId)!;
const libraryOf = (d: FormDesign, name: string) => d.libraries.find((l) => l.name === name)!;
const cellOf = (d: FormDesign, attribute: string): DesignCell =>
  d.tabs.flatMap((t) => t.columns.flatMap((c) => c.sections.flatMap((s) => s.cells))).find((c) => c.attribute === attribute)!;
const isDesign = (r: FormDesign | { blockedBy: DesignHandler[] }): r is FormDesign => !('blockedBy' in r);
const GUID = /^\{[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\}$/;

describe('readForm: libraries and events', () => {
  it('reads the libraries in order and the OnLoad, OnSave and OnChange events with their handlers', () => {
    const d = read();
    expect(d.libraries.map((l) => [l.name, l.uniqueId])).toEqual([['new_/a.js', LIB_A], ['new_/b.js', LIB_B]]);
    expect(d.events.map((e) => [e.name, e.attribute, e.handlers.map((h) => h.uniqueId)])).toEqual([
      ['onload', undefined, [H_LOAD, H_LOAD2]],
      ['onsave', undefined, [H_SAVE]],
      ['onchange', 'telephone1', [H_CHANGE]],
    ]);
    const h = handlerOf(d, H_LOAD2);
    expect(h).toMatchObject({ functionName: 'A.second', library: 'new_/a.js', enabled: false, parameters: "'x', 1", passContext: false });
    expect(new Set([...d.libraries, ...d.events, ...d.events.flatMap((e) => e.handlers)].map((x) => x.key)).size)
      .toBe(d.libraries.length + d.events.length + 4);
  });

  it('otherEvents names the events the designer does not edit', () => {
    expect(otherEvents(read())).toEqual(['TabStateChange', 'onload']);
  });

  it('a form without scripts has no libraries and no events', () => {
    const d = read(invented);
    expect(d.libraries).toEqual([]);
    expect(d.events).toEqual([]);
    expect(otherEvents(d)).toEqual([]);
  });
});

describe('library ops and the write', () => {
  it('an unedited form returns the same string, the public fixture too', () => {
    expect(writeForm(read())).toBe(FORM);
    expect(writeForm(read(invented))).toBe(invented);
  });

  it('addLibrary appends <Library name libraryUniqueId/> and leaves the others as they were', () => {
    const d = addLibrary(read(), ' new_/c.js ');
    const xml = writeForm(d);
    expect(xml).not.toBe(FORM);
    const libs = libraryEls(xml);
    expect(libs.map((l) => l.getAttribute('name'))).toEqual(['new_/a.js', 'new_/b.js', 'new_/c.js']);
    expect(Array.from(libs[2].attributes).map((a) => a.name)).toEqual(['name', 'libraryUniqueId']);
    expect(libs[2].getAttribute('libraryUniqueId')).toMatch(GUID);
    expect(libs.slice(0, 2).map(serialize)).toEqual(libraryEls(FORM).map(serialize));
    expect(blocksOf(xml, 'events')).toEqual(blocksOf(FORM, 'events'));
    expect(blocksOf(xml, 'tabs')).toEqual(blocksOf(FORM, 'tabs'));
  });

  it('addLibrary of a blank name or a library already on the form returns d', () => {
    const d = read();
    expect(addLibrary(d, '  ')).toBe(d);
    expect(addLibrary(d, 'NEW_/A.JS')).toBe(d);
  });

  it('a form without <formLibraries> gets one before <events>, or before the blocks after the body', () => {
    const noLibs = FORM.replace(/\s*<formLibraries>[\s\S]*<\/formLibraries>/, '');
    const xml = writeForm(addLibrary(read(noLibs), 'new_/c.js'));
    expect(Array.from(formOf(xml).children).map((c) => c.tagName)).toEqual(['tabs', 'formLibraries', 'events']);
    const xml2 = writeForm(addLibrary(read(invented), 'new_/c.js'));
    const tags = Array.from(formOf(xml2).children).map((c) => c.tagName);
    expect(tags.indexOf('formLibraries')).toBe(tags.indexOf('footer') + 1);
  });

  it('moveLibrary reorders the elements, which keep their text; an out-of-range move returns d', () => {
    const d = read();
    const moved = moveLibrary(d, libraryOf(d, 'new_/b.js').key, -1);
    expect(moved.libraries.map((l) => l.name)).toEqual(['new_/b.js', 'new_/a.js']);
    const xml = writeForm(moved);
    expect(libraryEls(xml).map(serialize)).toEqual([...libraryEls(FORM)].reverse().map(serialize));
    expect(xml).toContain(`<formLibraries>\n    <Library name="new_/b.js"`);
    expect(moveLibrary(d, libraryOf(d, 'new_/a.js').key, -1)).toBe(d);
    expect(moveLibrary(d, libraryOf(d, 'new_/b.js').key, 1)).toBe(d);
    expect(moveLibrary(d, 'nope', 1)).toBe(d);
  });

  it('removeLibrary is blocked while a handler, of any event, names it', () => {
    const d = read();
    const a = removeLibrary(d, libraryOf(d, 'new_/a.js').key);
    expect(isDesign(a)).toBe(false);
    expect((a as { blockedBy: DesignHandler[] }).blockedBy.map((h) => h.functionName)).toEqual(['A.onLoad', 'A.second', 'A.phone']);
    // B.tab is a TabStateChange handler: not edited by the designer, but it still loads the library.
    const b = removeLibrary(d, libraryOf(d, 'new_/b.js').key);
    expect((b as { blockedBy: DesignHandler[] }).blockedBy.map((h) => h.functionName)).toEqual(['B.onSave', 'B.tab']);
    expect(removeLibrary(d, 'nope')).toBe(d);
  });

  it('removeLibrary of an unused library removes its element; emptying <formLibraries> removes it', () => {
    const d = addLibrary(read(invented), 'new_/c.js');
    const saved = read(writeForm(d));
    const removed = removeLibrary(saved, saved.libraries[0].key);
    expect(isDesign(removed)).toBe(true);
    const xml = writeForm(removed as FormDesign);
    expect(childElements(formOf(xml), 'formLibraries')).toEqual([]);

    const two = addLibrary(read(), 'new_/c.js');
    const reread = read(writeForm(two));
    const c = removeLibrary(reread, libraryOf(reread, 'new_/c.js').key) as FormDesign;
    expect(libraryEls(writeForm(c)).map(serialize)).toEqual(libraryEls(FORM).map(serialize));
  });

  it('an empty <formLibraries> the designer did not empty stays', () => {
    const xml = FORM.replace(/<formLibraries>[\s\S]*<\/formLibraries>/, '<formLibraries />');
    const d = read(xml.replace(/\s*<events>[\s\S]*<\/events>/, ''));
    const out = writeForm(addHandler(addLibrary(d, 'x.js'), 'onload', undefined, {
      functionName: 'f', library: 'x.js', enabled: true, parameters: '', passContext: true,
    }));
    expect(childElements(formOf(out), 'formLibraries')).toHaveLength(1);
  });
});

describe('handler ops and the write', () => {
  const NEW = { functionName: ' C.onLoad ', library: 'new_/b.js', enabled: true, parameters: '1, "a"', passContext: true };

  it('addHandler appends the G6 <Handler> to the existing event', () => {
    const d = addHandler(read(), 'onload', undefined, NEW);
    const xml = writeForm(d);
    const load = eventEls(xml)[0];
    const added = handlerEls(load)[2];
    expect(Array.from(added.attributes).map((a) => [a.name, a.name === 'handlerUniqueId' ? 'id' : a.value])).toEqual([
      ['functionName', 'C.onLoad'], ['libraryName', 'new_/b.js'], ['handlerUniqueId', 'id'], ['enabled', 'true'],
      ['parameters', '1, "a"'], ['passExecutionContext', 'true'],
    ]);
    expect(added.getAttribute('handlerUniqueId')).toMatch(GUID);
    expect(handlerEls(load).slice(0, 2).map(serialize)).toEqual(handlerEls(eventEls(FORM)[0]).map(serialize));
    expect(eventEls(xml).slice(1).map(serialize)).toEqual(eventEls(FORM).slice(1).map(serialize));
    expect(blocksOf(xml, 'formLibraries')).toEqual(blocksOf(FORM, 'formLibraries'));
  });

  it('addHandler creates <event name application="false" active="false" [attribute]><Handlers> when there is none', () => {
    const d = addHandler(read(), 'onchange', 'name', { ...NEW, functionName: 'C.name' });
    expect(d.events.at(-1)).toMatchObject({ name: 'onchange', attribute: 'name' });
    const xml = writeForm(d);
    const ev = eventEls(xml).at(-1)!;
    expect(Array.from(ev.attributes).map((a) => [a.name, a.value])).toEqual([
      ['name', 'onchange'], ['application', 'false'], ['active', 'false'], ['attribute', 'name'],
    ]);
    expect(childElements(ev, 'Handlers')).toHaveLength(1);
    expect(handlerEls(ev).map((h) => h.getAttribute('functionName'))).toEqual(['C.name']);
    expect(eventEls(xml).slice(0, -1).map(serialize)).toEqual(eventEls(FORM).map(serialize));

    const onSave = writeForm(addHandler(read(invented), 'onsave', undefined, { ...NEW, library: 'x.js' }));
    expect(onSave).toBe(invented); // the library is not on the form: no change
    const withLib = addLibrary(read(invented), 'x.js');
    const out = writeForm(addHandler(withLib, 'onsave', 'ignored', { ...NEW, library: 'x.js' }));
    expect(eventShape(out)).toEqual([{ name: 'onsave', attribute: undefined, handlers: [expect.stringMatching(GUID)] }]);
    expect(Array.from(formOf(out).children).map((c) => c.tagName).slice(-4, -2)).toEqual(['formLibraries', 'events']);
  });

  it('addHandler returns d for a blank function name, an unknown library or an OnChange without a column', () => {
    const d = read();
    expect(addHandler(d, 'onload', undefined, { ...NEW, functionName: '  ' })).toBe(d);
    expect(addHandler(d, 'onload', undefined, { ...NEW, library: 'other.js' })).toBe(d);
    expect(addHandler(d, 'onchange', undefined, NEW)).toBe(d);
  });

  it('an edited handler changes only its attributes', () => {
    const d = read();
    const edited = setHandler(d, handlerOf(d, H_LOAD).key, { functionName: ' A.renamed ', enabled: false });
    const xml = writeForm(edited);
    const h = handlerEls(eventEls(xml)[0])[0];
    expect(serialize(h)).toBe(serialize(handlerEls(eventEls(FORM)[0])[0])
      .replace('A.onLoad', 'A.renamed').replace('enabled="true"', 'enabled="false"'));
    // The whole document is the source as serialized, with those two attributes changed.
    expect(xml).toBe(serialize(parseXml(FORM.replace('functionName="A.onLoad"', 'functionName="A.renamed"')
      .replace(`handlerUniqueId="${H_LOAD}" enabled="true"`, `handlerUniqueId="${H_LOAD}" enabled="false"`))));
  });

  it('setHandler edits library, parameters and pass context; returns d for no change or an invalid patch', () => {
    const d = read();
    const key = handlerOf(d, H_SAVE).key;
    const edited = setHandler(d, key, { library: 'new_/a.js', parameters: '2', passContext: false });
    const h = handlerEls(eventEls(writeForm(edited))[1])[0];
    expect([h.getAttribute('libraryName'), h.getAttribute('parameters'), h.getAttribute('passExecutionContext')])
      .toEqual(['new_/a.js', '2', 'false']);
    expect(setHandler(d, key, { functionName: 'B.onSave', enabled: true })).toBe(d);
    expect(setHandler(d, key, { functionName: ' ' })).toBe(d);
    expect(setHandler(d, key, { library: 'missing.js' })).toBe(d);
    expect(setHandler(d, 'nope', { enabled: false })).toBe(d);
  });

  it('removeHandler removes the element; the last handler removed drops its <event>', () => {
    const d = read();
    const one = removeHandler(d, handlerOf(d, H_LOAD).key);
    expect(eventShape(writeForm(one))[0].handlers).toEqual([H_LOAD2]);
    const none = removeHandler(removeHandler(d, handlerOf(d, H_CHANGE).key), handlerOf(d, H_SAVE).key);
    expect(none.events.map((e) => e.name)).toEqual(['onload']);
    const xml = writeForm(none);
    expect(eventShape(xml).map((e) => e.name)).toEqual(['onload', 'TabStateChange', 'onload']);
    expect(removeHandler(d, 'nope')).toBe(d);
  });

  it('emptying <events> removes it', () => {
    const xml = FORM.replace(/\s*<event name="(TabStateChange|onload" application="true)[\s\S]*?<\/event>/g, '');
    let d = read(xml);
    for (const h of d.events.flatMap((e) => e.handlers)) d = removeHandler(d, h.key);
    expect(d.events).toEqual([]);
    const out = writeForm(d);
    expect(childElements(formOf(out), 'events')).toEqual([]);
    expect(blocksOf(out, 'formLibraries')).toEqual(blocksOf(FORM, 'formLibraries'));
  });

  it('moveHandler reorders within the event; the elements keep their text', () => {
    const d = read();
    const moved = moveHandler(d, handlerOf(d, H_LOAD).key, 1);
    const xml = writeForm(moved);
    expect(handlerEls(eventEls(xml)[0]).map(serialize)).toEqual([...handlerEls(eventEls(FORM)[0])].reverse().map(serialize));
    expect(moveHandler(d, handlerOf(d, H_LOAD).key, -1)).toBe(d);
    expect(moveHandler(d, handlerOf(d, H_SAVE).key, 1)).toBe(d);
  });

  it('other events and application="true" events are untouched by handler and library edits', () => {
    let d = read();
    d = addHandler(d, 'onload', undefined, NEW);
    d = removeHandler(d, handlerOf(d, H_SAVE).key);
    d = setHandler(d, handlerOf(d, H_CHANGE).key, { enabled: false });
    d = moveLibrary(d, d.libraries[1].key, -1);
    const xml = writeForm(d);
    const others = (x: string) => eventEls(x).filter((e) => e.getAttribute('name') === 'TabStateChange' || e.getAttribute('application') === 'true').map(serialize);
    expect(others(xml)).toEqual(others(FORM));
    expect(others(xml)).toHaveLength(2);
    expect(otherEvents(d)).toEqual(['TabStateChange', 'onload']);
  });

  it('Save as (writeForm of the copy) keeps every library and handler id', () => {
    const d = setHandler(read(), handlerOf(read(), H_LOAD).key, { parameters: '9' });
    const copy: FormDesign = { ...d, name: 'Information (copy)', isDefault: false, active: true };
    const xml = writeForm(copy);
    expect(libraryEls(xml).map((l) => l.getAttribute('libraryUniqueId'))).toEqual([LIB_A, LIB_B]);
    expect(eventShape(xml).flatMap((e) => e.handlers)).toEqual([H_LOAD, H_LOAD2, H_SAVE, H_CHANGE, H_TAB]);
    expect(xml).toContain(H_APP);
  });

  it('moveHandler of an unknown key and setHandler with an empty patch return d itself', () => {
    const d = read();
    expect(moveHandler(d, 'nope', 1)).toBe(d);
    expect(setHandler(d, handlerOf(d, H_LOAD).key, {})).toBe(d);
  });
});

describe('F7 with libraries and events', () => {
  it('removing the last control of a column drops its OnChange event and keeps the library', () => {
    const d = read();
    const xml = writeForm(removeField(d, cellOf(d, 'telephone1').key));
    expect(eventShape(xml).map((e) => e.attribute)).not.toContain('telephone1');
    expect(blocksOf(xml, 'formLibraries')).toEqual(blocksOf(FORM, 'formLibraries'));
    expect(eventEls(xml).map(serialize)).toEqual(eventEls(FORM).filter((e) => e.getAttribute('attribute') !== 'telephone1').map(serialize));
  });

  it('an edit to the form handlers together with the removal keeps both', () => {
    const d = read();
    const edited = setHandler(removeField(d, cellOf(d, 'telephone1').key), handlerOf(d, H_SAVE).key, { enabled: false });
    const xml = writeForm(edited);
    expect(eventShape(xml).map((e) => e.name)).toEqual(['onload', 'onsave', 'TabStateChange', 'onload']);
    expect(handlerEls(eventEls(xml)[1])[0].getAttribute('enabled')).toBe('false');
    expect(libraryEls(xml)).toHaveLength(2);
  });
});

describe('fix round 1', () => {
  const H_INTERNAL = '{40000000-0000-0000-0000-000000000007}';
  /** FORM with telephone1's OnChange holding system <InternalHandlers> before its <Handlers>, from a library of its own. */
  const MIXED = FORM
    .replace('<Library name="new_/b.js"', `<Library name="Sys.js" libraryUniqueId="{30000000-0000-0000-0000-000000000003}" />
    <Library name="new_/b.js"`)
    .replace(/(<event name="onchange" application="false" active="false" attribute="telephone1">)/, `$1
      <InternalHandlers>
        <Handler functionName="Sys.phone" libraryName="Sys.js" handlerUniqueId="${H_INTERNAL}" enabled="true" parameters="" passExecutionContext="false" />
      </InternalHandlers>`);
  const phoneEvent = (xml: string) => eventEls(xml).find((e) => e.getAttribute('attribute') === 'telephone1');

  it('removing the last user handler of an event with <InternalHandlers> keeps the event and its InternalHandlers', () => {
    const d = read(MIXED);
    expect(handlerOf(d, H_INTERNAL)).toBeUndefined();
    const removed = removeHandler(d, handlerOf(d, H_CHANGE).key);
    expect(removed.events.map((e) => e.name)).toEqual(['onload', 'onsave']);
    const xml = writeForm(removed);
    const ev = phoneEvent(xml)!;
    expect(ev).toBeDefined();
    expect(childElements(ev, 'Handlers')).toEqual([]);
    expect(childElements(ev, 'InternalHandlers').map(serialize)).toEqual(childElements(phoneEvent(MIXED)!, 'InternalHandlers').map(serialize));
    // Read back, the event holds no handler the designer edits.
    expect(read(xml).events.map((e) => e.name)).toEqual(['onload', 'onsave', 'onchange']);
    expect(read(xml).events[2].handlers).toEqual([]);
  });

  it('a library named only by an InternalHandler of an edited event cannot be removed', () => {
    const d = read(MIXED);
    const r = removeLibrary(d, libraryOf(d, 'Sys.js').key);
    expect(isDesign(r)).toBe(false);
    expect((r as { blockedBy: DesignHandler[] }).blockedBy.map((h) => h.functionName)).toEqual(['Sys.phone']);
  });

  it('an F7 drop that empties <events> removes the block', () => {
    const only = FORM.replace(/<events>[\s\S]*<\/events>/, `<events>
    <event name="onchange" application="false" active="false" attribute="telephone1">
      <Handlers>
        <Handler functionName="A.phone" libraryName="new_/a.js" handlerUniqueId="${H_CHANGE}" enabled="true" parameters="" passExecutionContext="true" />
      </Handlers>
    </event>
  </events>`);
    const d = read(only);
    const xml = writeForm(removeField(d, cellOf(d, 'telephone1').key));
    expect(childElements(formOf(xml), 'events')).toEqual([]);
    expect(blocksOf(xml, 'formLibraries')).toEqual(blocksOf(only, 'formLibraries'));
  });

  it('an F7 drop that leaves other events keeps <events>', () => {
    const d = read();
    const xml = writeForm(removeField(d, cellOf(d, 'telephone1').key));
    expect(childElements(formOf(xml), 'events')).toHaveLength(1);
  });
});

describe('F7 in the model (final fix)', () => {
  const LIB_C = 'new_/c.js';
  const column = (logicalName: string) =>
    ({ logicalName, displayName: logicalName, kind: 'string', requiredLevel: 'None' }) as AttributeMeta;
  const fields = { functionName: 'C.change', library: LIB_C, enabled: true, parameters: '', passContext: true };
  const blocked = (r: FormDesign | { blockedBy: DesignHandler[] }) =>
    (isDesign(r) ? [] : r.blockedBy.map((h) => h.functionName));
  const onchangeOf = (d: FormDesign, attribute: string) => d.events.filter((e) => e.name === 'onchange' && e.attribute === attribute);

  it('a source field with a handler added in the session, then removed: no orphan is saved and the library can go', () => {
    let d = addLibrary(read(), LIB_C);
    d = addHandler(d, 'onchange', 'telephone1', fields);
    d = addHandler(d, 'onchange', 'telephone1', { ...fields, functionName: 'C.again' });
    const phone = cellOf(d, 'telephone1').key;
    // The source handler A.phone and the two added ones.
    expect(fieldImpact(d, phone, []).handlers).toBe(3);
    expect(blocked(removeLibrary(d, libraryOf(d, LIB_C).key))).toEqual(['C.change', 'C.again']);
    const removed = removeField(d, phone);
    expect(onchangeOf(removed, 'telephone1')).toEqual([]);
    expect(blocked(removeLibrary(removed, libraryOf(removed, LIB_C).key))).toEqual([]);
    const xml = writeForm(removed);
    expect(eventShape(xml).map((e) => e.attribute)).not.toContain('telephone1');
    expect(xml).not.toContain('C.change');
    expect(libraryEls(xml).map((l) => l.getAttribute('name'))).toEqual(['new_/a.js', 'new_/b.js', LIB_C]);
  });

  it('a field added in the session with a handler, then removed: no orphan is saved and the library can go', () => {
    let d = addLibrary(read(), LIB_C);
    d = addField(d, d.tabs[0].columns[0].sections[0].key, column('fax'));
    d = addHandler(d, 'onchange', 'fax', fields);
    const fax = cellOf(d, 'fax').key;
    expect(fieldImpact(d, fax, []).handlers).toBe(1);
    expect(blocked(removeLibrary(d, libraryOf(d, LIB_C).key))).toEqual(['C.change']);
    const removed = removeField(d, fax);
    expect(onchangeOf(removed, 'fax')).toEqual([]);
    expect(blocked(removeLibrary(removed, libraryOf(removed, LIB_C).key))).toEqual([]);
    const xml = writeForm(removed);
    expect(eventShape(xml).map((e) => e.attribute)).not.toContain('fax');
    expect(xml).not.toContain('C.change');
  });

  it('counts the handlers left after the session removed the source ones', () => {
    const d = read();
    const without = removeHandler(d, handlerOf(d, H_CHANGE).key);
    expect(fieldImpact(without, cellOf(without, 'telephone1').key, []).handlers).toBe(0);
    expect(containerImpact(without, without.tabs[0].columns[0].sections[0].key).handlers).toBe(0);
    expect(containerImpact(d, d.tabs[0].columns[0].sections[0].key).handlers).toBe(1);
  });

  it('keeps the events while the column stays in the header, and drops them with its last control', () => {
    let d = addLibrary(read(), LIB_C);
    d = addHandler(addHeaderField(d, column('telephone1')), 'onchange', 'telephone1', fields);
    const body = removeField(d, cellOf(d, 'telephone1').key);
    expect(onchangeOf(body, 'telephone1')[0].handlers.map((h) => h.functionName)).toEqual(['A.phone', 'C.change']);
    expect(fieldImpact(body, body.header[0].key, []).handlers).toBe(2);
    const gone = removeHeaderField(body, body.header[0].key);
    expect(onchangeOf(gone, 'telephone1')).toEqual([]);
    expect(eventShape(writeForm(gone)).map((e) => e.attribute)).not.toContain('telephone1');
  });

  it('removing the section of the last control drops the events from the model', () => {
    const two = FORM.replace('<section name="main"', `<section name="side" id="{10000000-0000-0000-0000-000000000009}" columns="1">
              <labels />
              <rows />
            </section>
            <section name="main"`);
    const d = read(two);
    const main = d.tabs[0].columns[0].sections[1].key;
    expect(containerImpact(d, main).handlers).toBe(1);
    const removed = removeSection(d, main);
    expect(onchangeOf(removed, 'telephone1')).toEqual([]);
    expect(eventShape(writeForm(removed)).map((e) => e.attribute)).not.toContain('telephone1');
  });

  it('an F7 drop removes the whole event, <InternalHandlers> included, and an application OnChange of the column', () => {
    const xml = FORM.replace(/(<event name="onchange" application="false" active="false" attribute="telephone1">)/, `$1
      <InternalHandlers>
        <Handler functionName="Sys.phone" libraryName="Sys.js" handlerUniqueId="{40000000-0000-0000-0000-000000000008}" enabled="true" parameters="" passExecutionContext="false" />
      </InternalHandlers>`).replace('  </events>', `    <event name="onchange" application="true" active="true" attribute="telephone1">
      <Handlers>
        <Handler functionName="Sys.app" libraryName="Sys.js" handlerUniqueId="{40000000-0000-0000-0000-000000000009}" enabled="true" parameters="" passExecutionContext="false" />
      </Handlers>
    </event>
  </events>`);
    const d = read(xml);
    expect(fieldImpact(d, cellOf(d, 'telephone1').key, []).handlers).toBe(2);
    const out = writeForm(removeField(d, cellOf(d, 'telephone1').key));
    expect(eventShape(out).map((e) => e.attribute)).not.toContain('telephone1');
    expect(out).not.toContain('Sys.phone');
    expect(out).not.toContain('Sys.app');
  });
});

describe('events under tabs, sections and controls (final fix)', () => {
  /** FORM with the TabStateChange event under its `<tab>`, as Dataverse writes it, and a form-level one gone. */
  const TABBED = FORM
    .replace(/\s*<event name="TabStateChange"[\s\S]*?<\/event>/, '')
    .replace('<tab name="general" id="{10000000-0000-0000-0000-000000000001}">', `<tab name="general" id="{10000000-0000-0000-0000-000000000001}">
      <events>
        <event name="TabStateChange" application="false" active="false">
          <Handlers>
            <Handler functionName="C.tab" libraryName="new_/c.js" handlerUniqueId="${H_TAB}" enabled="true" parameters="" passExecutionContext="true" />
          </Handlers>
        </event>
      </events>`)
    .replace('<Library name="new_/b.js"', `<Library name="new_/c.js" libraryUniqueId="{30000000-0000-0000-0000-000000000003}" />
    <Library name="new_/b.js"`);

  it('a library used only by a tab handler cannot be removed', () => {
    const d = read(TABBED);
    const r = removeLibrary(d, libraryOf(d, 'new_/c.js').key);
    expect(isDesign(r)).toBe(false);
    expect((r as { blockedBy: DesignHandler[] }).blockedBy.map((h) => h.functionName)).toEqual(['C.tab']);
  });

  it('otherEvents counts the tab event; the designer edits only the form-level events', () => {
    const d = read(TABBED);
    expect(otherEvents(d)).toEqual(['TabStateChange', 'onload']);
    expect(d.events.map((e) => e.name)).toEqual(['onload', 'onsave', 'onchange']);
    const xml = writeForm(setHandler(d, handlerOf(d, H_LOAD).key, { enabled: false }));
    const tabEvents = (x: string) => Array.from(formOf(x).getElementsByTagName('tab')[0].getElementsByTagName('events')).map(serialize);
    expect(tabEvents(xml)).toEqual(tabEvents(TABBED));
  });
});

describe('orphan OnChange events (#274)', () => {
  const H_GONE = '{40000000-0000-0000-0000-00000000000a}';
  const H_FAX = '{40000000-0000-0000-0000-00000000000b}';
  /** FORM with a footer-only column `fax` that has an OnChange, and an OnChange of `new_gone`, a column not on the form. */
  const ORPHANED = FORM
    .replace('  </tabs>', `  </tabs>
  <footer>
    <rows>
      <row>
        <cell id="{10000000-0000-0000-0000-00000000000f}">
          <control id="fax" classid="${TEXT}" datafieldname="fax" />
        </cell>
      </row>
    </rows>
  </footer>`)
    .replace('  </events>', `    <event name="onchange" attribute="fax" application="false">
      <Handlers>
        <Handler functionName="B.fax" libraryName="new_/b.js" handlerUniqueId="${H_FAX}" enabled="true" parameters="" passExecutionContext="true" />
      </Handlers>
    </event>
    <event name="onchange" attribute="new_gone" application="false">
      <Handlers>
        <Handler functionName="A.gone" libraryName="new_/a.js" handlerUniqueId="${H_GONE}" enabled="true" parameters="" passExecutionContext="true" />
      </Handlers>
    </event>
  </events>`);

  it('orphanChangeEvents lists an OnChange of a column not on the form, and dropEvent removes it', () => {
    const d = read(ORPHANED);
    expect(writeForm(d)).toBe(ORPHANED);
    const orphans = orphanChangeEvents(d);
    expect(orphans.map((e) => [e.attribute, e.handlers.map((h) => h.uniqueId)])).toEqual([['new_gone', [H_GONE]]]);
    const blockers = (x: FormDesign) => {
      const r = removeLibrary(x, libraryOf(x, 'new_/a.js').key);
      return isDesign(r) ? [] : r.blockedBy.map((h) => h.functionName);
    };
    expect(blockers(d)).toContain('A.gone');
    const dropped = dropEvent(d, orphans[0].key);
    expect(orphanChangeEvents(dropped)).toEqual([]);
    expect(blockers(dropped)).not.toContain('A.gone');
    const xml = writeForm(dropped);
    expect(eventShape(xml).map((e) => e.attribute)).not.toContain('new_gone');
    const others = (x: string) => eventEls(x).filter((e) => e.getAttribute('attribute') !== 'new_gone').map(serialize);
    expect(others(xml)).toEqual(others(ORPHANED));
    expect(dropEvent(d, 'nope')).toBe(d);
  });
});

describe('indentation (C3)', () => {
  const NEW = { functionName: 'C.name', library: 'new_/b.js', enabled: true, parameters: '', passContext: true };

  it('a new event and its handler are indented', () => {
    const xml = writeForm(addHandler(read(), 'onchange', 'name', NEW));
    const ev = eventEls(xml).at(-1)!;
    const id = ev.getElementsByTagName('Handler')[0].getAttribute('handlerUniqueId');
    expect(xml).toContain([
      '    </event>',
      '    <event name="onchange" application="false" active="false" attribute="name">',
      '      <Handlers>',
      `        <Handler functionName="C.name" libraryName="new_/b.js" handlerUniqueId="${id}" enabled="true" parameters="" passExecutionContext="true"/>`,
      '      </Handlers>',
      '    </event>',
      '  </events>',
      '</form>',
    ].join('\n'));
    // A new <events> and <formLibraries> sit on their own lines, their children one step in.
    const bare = FORM.replace(/\s*<formLibraries>[\s\S]*<\/events>/, '');
    const fresh = writeForm(addHandler(addLibrary(read(bare), 'new_/c.js'), 'onload', undefined, { ...NEW, library: 'new_/c.js' }));
    const tail = fresh.slice(fresh.indexOf('  </tabs>')).split('\n').map((l) => l.replace(/^(\s*<\w+) .*?(\/?>)$/, '$1 …$2'));
    expect(tail).toEqual([
      '  </tabs>', '  <formLibraries>', '    <Library …/>', '  </formLibraries>', '  <events>', '    <event …>', '      <Handlers>',
      '        <Handler …/>', '      </Handlers>', '    </event>', '  </events>', '</form>',
    ]);
    // The same edit in a compact copy of the form adds no line.
    expect(writeForm(addHandler(read(FORM.replace(/>\s+</g, '><')), 'onchange', 'name', NEW))).not.toContain('\n');
  });
});
