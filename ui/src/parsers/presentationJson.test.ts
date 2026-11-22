import { describe, expect, it } from 'vitest';
import { parsePresentation } from './presentationJson';

const ramp = Object.fromEntries(
  [10, 20, 30, 40, 50, 60, 70, 80, 90, 100, 110, 120, 130, 140, 150, 160].map((k, i) => [String(k), '#' + (i + 16).toString(16).padStart(2, '0').repeat(3)]),
);

describe('parsePresentation', () => {
  it('reads a full valid overlay', () => {
    const r = parsePresentation(
      JSON.stringify({
        version: 1,
        theme: { brandRamp: ramp, fontFamily: 'Inter', density: 'compact', colorScheme: 'dark' },
        whiteLabel: { name: 'Contoso', logo: 'new_logo.png', favicon: 'new_fav.ico' },
      }),
    );
    expect(r.error).toBeUndefined();
    expect(r.warnings).toEqual([]);
    expect(r.model?.theme.brand?.[110]).toBe(ramp['110']);
    expect(r.model?.theme.fontFamily).toBe('Inter');
    expect(r.model?.theme.density).toBe('compact');
    expect(r.model?.theme.colorScheme).toBe('dark');
    expect(r.model?.whiteLabel).toEqual({ name: 'Contoso', logo: 'new_logo.png', favicon: 'new_fav.ico' });
  });

  it('defaults to comfortable density and empty white label', () => {
    const r = parsePresentation('{"version":1}');
    expect(r.model).toEqual({ theme: { density: 'comfortable' }, whiteLabel: {}, views: {}, forms: {}, choices: {} });
    expect(r.warnings).toEqual([]);
  });

  it('drops a ramp with 15 keys and warns', () => {
    const short: Record<string, string> = { ...ramp };
    delete short['160'];
    const r = parsePresentation(JSON.stringify({ version: 1, theme: { brandRamp: short } }));
    expect(r.model?.theme.brand).toBeUndefined();
    expect(r.warnings).toContain('theme.brandRamp needs the 16 shades 10…160');
  });

  it('drops a ramp with a bad hex value and warns', () => {
    const r = parsePresentation(JSON.stringify({ version: 1, theme: { brandRamp: { ...ramp, '50': 'red' } } }));
    expect(r.model?.theme.brand).toBeUndefined();
    expect(r.warnings).toContain('theme.brandRamp needs the 16 shades 10…160');
  });

  it('warns on unknown keys, top-level and nested', () => {
    const r = parsePresentation('{"version":1,"theme":{"shadow":1},"extra":true,"whiteLabel":{"x":1}}');
    expect(r.warnings).toEqual(['Unknown key extra', 'Unknown key theme.shadow', 'Unknown key whiteLabel.x']);
    expect(r.model).toBeDefined();
  });

  it('accepts a views entry and a forms entry without warnings', () => {
    const r = parsePresentation('{"version":1,"views":{"a":{}},"forms":{"b":{}}}');
    expect(r.warnings).toEqual([]);
    expect(r.model).toBeDefined();
  });

  it('keeps a view id named __proto__ as an own entry and does not touch the prototype', () => {
    const r = parsePresentation('{"version":1,"views":{"__proto__":{"cards":{}}}}');
    expect(Object.keys(r.model!.views)).toEqual(['__proto__']);
    expect(r.model!.views['__proto__']).toEqual({ cards: {} });
    expect(Object.getPrototypeOf(r.model!.views)).toBeNull();
    expect(({} as { cards?: unknown }).cards).toBeUndefined();
  });

  it('warns when two keys normalise to the same view id and uses the last entry', () => {
    const r = parsePresentation('{"version":1,"views":{"{ABC}":{"cards":{}},"abc":{"kanban":{"groupBy":"statuscode"}}}}');
    expect(r.warnings).toEqual(['views.abc is given more than once; the last entry is used.']);
    expect(r.model!.views.abc).toEqual({ kanban: { groupBy: 'statuscode' } });
  });

  it('errors on another version', () => {
    const r = parsePresentation('{"version":2}');
    expect(r.model).toBeUndefined();
    expect(r.error).toBe('This presentation uses version 2, which this version does not read.');
  });

  it('errors when version is missing', () => {
    const r = parsePresentation('{}');
    expect(r.error).toBe('This presentation has no version; it needs "version": 1.');
  });

  it('errors on text that is not JSON', () => {
    const r = parsePresentation('not json');
    expect(r.model).toBeUndefined();
    expect(r.error).toMatch(/JSON/i);
  });

  it('errors when the JSON is not an object', () => {
    expect(parsePresentation('[1]').error).toBeTruthy();
    expect(parsePresentation('null').error).toBeTruthy();
  });

  it('warns on invalid density and colour scheme and falls back', () => {
    const r = parsePresentation('{"version":1,"theme":{"density":"tiny","colorScheme":"blue"}}');
    expect(r.model?.theme.density).toBe('comfortable');
    expect(r.model?.theme.colorScheme).toBeUndefined();
    expect(r.warnings).toEqual([
      'theme.density must be "comfortable" or "compact"; comfortable is used.',
      'theme.colorScheme must be "system", "light" or "dark"; it is ignored.',
    ]);
  });

  it('strips characters that could break the CSS font value', () => {
    const r = parsePresentation(JSON.stringify({ version: 1, theme: { fontFamily: 'In"ter;{x}' } }));
    expect(r.model?.theme.fontFamily).toBe('Interx');
    expect(r.warnings).toEqual(['theme.fontFamily contained characters that are not allowed; they were removed.']);
  });

  it('ignores a font family that is empty after stripping', () => {
    const r = parsePresentation(JSON.stringify({ version: 1, theme: { fontFamily: ';{}' } }));
    expect(r.model?.theme.fontFamily).toBeUndefined();
    expect(r.warnings.length).toBe(2);
  });

  it('warns on non-string white label values', () => {
    const r = parsePresentation('{"version":1,"whiteLabel":{"name":5}}');
    expect(r.model?.whiteLabel.name).toBeUndefined();
    expect(r.warnings).toEqual(['whiteLabel.name must be text; it is ignored.']);
  });

  describe('views', () => {
    const ID = 'ab12cd34-0000-0000-0000-000000000001';
    const parse = (views: unknown) => parsePresentation(JSON.stringify({ version: 1, views }));

    it('parses views keyed by normalised id', () => {
      const r = parse({ ['{' + ID.toUpperCase() + '}']: { default: 'kanban', kanban: { groupBy: 'statuscode' } } });
      expect(r.warnings).toEqual([]);
      expect(r.model?.views[ID].kanban?.groupBy).toBe('statuscode');
      expect(r.model?.views[ID].default).toBe('kanban');
    });

    it('drops a view entry with an unknown mode or a bad shape, with a warning', () => {
      const r = parse({ [ID]: { default: 'gantt', kanban: {}, calendar: { start: 5 } } });
      const v = r.model?.views[ID];
      expect(v?.default).toBeUndefined();
      expect(v?.kanban).toBeUndefined();
      expect(v?.calendar).toBeUndefined();
      expect(r.warnings.some((w) => w.startsWith(`views.${ID}.default`))).toBe(true);
      expect(r.warnings.some((w) => w.startsWith(`views.${ID}.kanban`))).toBe(true);
      expect(r.warnings.some((w) => w.startsWith(`views.${ID}.calendar`))).toBe(true);
    });

    it('keeps calendar with start and optional end, and cards', () => {
      const r = parse({ [ID]: { default: 'grid', calendar: { start: 'a', end: 'b' }, cards: {} } });
      expect(r.warnings).toEqual([]);
      expect(r.model?.views[ID]).toEqual({ default: 'grid', calendar: { start: 'a', end: 'b' }, cards: {} });
    });

    it('keeps calendar.layout "timeline"', () => {
      const r = parse({ [ID]: { calendar: { start: 'a', end: 'b', layout: 'timeline' } } });
      expect(r.warnings).toEqual([]);
      expect(r.model?.views[ID].calendar).toEqual({ start: 'a', end: 'b', layout: 'timeline' });
    });

    it('ignores an unknown calendar.layout with a warning and keeps the calendar', () => {
      const r = parse({ [ID]: { calendar: { start: 'a', layout: 'gantt' } } });
      expect(r.warnings).toEqual([`views.${ID}.calendar.layout must be "month" or "timeline"; it is ignored.`]);
      expect(r.model?.views[ID].calendar).toEqual({ start: 'a' });
    });

    it('warns on unknown keys inside a view', () => {
      const r = parse({ [ID]: { foo: 1 } });
      expect(r.warnings).toContain(`Unknown key views.${ID}.foo`);
    });

    it('default naming a mode that is not configured is dropped with a warning', () => {
      const r = parse({ [ID]: { default: 'calendar' } });
      expect(r.model?.views[ID].default).toBeUndefined();
      expect(r.warnings.some((w) => w.startsWith(`views.${ID}.default`))).toBe(true);
    });

    it('views that is not an object is ignored with a warning', () => {
      const r = parse([1]);
      expect(r.model?.views).toEqual({});
      expect(r.warnings.some((w) => w.startsWith('views must be an object'))).toBe(true);
    });

    it('absent views gives an empty record', () => {
      expect(parsePresentation('{"version":1}').model?.views).toEqual({});
    });
  });
  describe('forms', () => {
    const ID = 'ab12cd34-0000-0000-0000-000000000001';
    const parse = (forms: unknown) => parsePresentation(JSON.stringify({ version: 1, forms }));
    const one = (entry: unknown) => parse({ [`{${ID.toUpperCase()}}`]: entry });

    it('parses a full entry keyed by normalised id with defaults filled in', () => {
      const r = one({ layout: 'wizard', grid: { general: { details: { col: 7, span: 6, row: 1 } } }, timeline: { appearance: 'cards' } });
      expect(r.warnings).toEqual([]);
      const f = r.model?.forms[ID];
      expect(f?.layout).toBe('wizard');
      expect(f?.grid.general.details).toEqual({ col: 7, span: 6, row: 1, rowSpan: 1 });
      expect(f?.timeline).toEqual({ appearance: 'cards', density: 'comfortable' });
    });

    describe('subgrids', () => {
      it('parses subgrids keyed by lowercase trimmed name, with the views rules', () => {
        const r = one({ subgrids: { ' Contacts ': { default: 'kanban', kanban: { groupBy: 'statuscode' } } } });
        expect(r.warnings).toEqual([]);
        expect(r.model?.forms[ID].subgrids).toEqual({ contacts: { default: 'kanban', kanban: { groupBy: 'statuscode' } } });
      });

      it('ignores subgrids that is not an object, with a warning', () => {
        const r = one({ subgrids: [] });
        expect(r.warnings).toContain(`forms.${ID}.subgrids must be an object; it is ignored.`);
        expect(r.model?.forms[ID].subgrids).toBeUndefined();
      });

      it('warns like a view entry, with the subgrid path, and keeps the entry', () => {
        const r = one({ subgrids: { X: { calendar: {} } } });
        expect(r.warnings).toEqual([`forms.${ID}.subgrids.x.calendar needs a "start" column name (and "end" as text when given); it is ignored.`]);
        expect(r.model?.forms[ID].subgrids?.x).toEqual({});
      });

      it('keeps a subgrid calendar.layout and warns on an unknown one, with the subgrid path', () => {
        const r = one({ subgrids: { a: { calendar: { start: 'x', layout: 'timeline' } }, b: { calendar: { start: 'x', layout: 'week' } } } });
        expect(r.model?.forms[ID].subgrids?.a).toEqual({ calendar: { start: 'x', layout: 'timeline' } });
        expect(r.model?.forms[ID].subgrids?.b).toEqual({ calendar: { start: 'x' } });
        expect(r.warnings).toEqual([`forms.${ID}.subgrids.b.calendar.layout must be "month" or "timeline"; it is ignored.`]);
      });

      it('no longer reports subgrids as an unknown key', () => {
        expect(one({ subgrids: {} }).warnings).toEqual([]);
      });

      it('the later duplicate wins, with a warning', () => {
        const r = one({ subgrids: { a: { cards: {} }, A: { kanban: { groupBy: 'g' } } } });
        expect(r.warnings).toEqual([`forms.${ID}.subgrids.a is given more than once; the last entry is used.`]);
        expect(r.model?.forms[ID].subgrids).toEqual({ a: { kanban: { groupBy: 'g' } } });
      });
    });

    it('defaults to tabs layout, empty grid and no timeline', () => {
      const f = one({}).model?.forms[ID];
      expect(f).toEqual({ layout: 'tabs', grid: {} });
    });

    it('defaults span to 1 when omitted and keeps rowSpan', () => {
      const f = one({ grid: { t: { s: { col: 2, row: 3, rowSpan: 2 } } } }).model?.forms[ID];
      expect(f?.grid.t.s).toEqual({ col: 2, span: 1, row: 3, rowSpan: 2 });
    });

    it('warns and uses tabs for an unknown layout', () => {
      const r = one({ layout: 'steps' });
      expect(r.model?.forms[ID].layout).toBe('tabs');
      expect(r.warnings.some((w) => w.startsWith(`forms.${ID}.layout`))).toBe(true);
    });

    it('clamps col 0 to 1 with a warning', () => {
      const r = one({ grid: { t: { s: { col: 0, row: 1 } } } });
      expect(r.model?.forms[ID].grid.t.s.col).toBe(1);
      expect(r.warnings.some((w) => w.startsWith(`forms.${ID}.grid.t.s.col`))).toBe(true);
    });

    it('clamps span when col + span - 1 exceeds 12', () => {
      const r = one({ grid: { t: { s: { col: 9, span: 6, row: 1 } } } });
      expect(r.model?.forms[ID].grid.t.s.span).toBe(4);
      expect(r.warnings.some((w) => w.startsWith(`forms.${ID}.grid.t.s.span`))).toBe(true);
    });

    it('clamps a non-integer row to an integer with a warning', () => {
      const r = one({ grid: { t: { s: { col: 1, row: 1.5 } } } });
      expect(r.model?.forms[ID].grid.t.s.row).toBe(1);
      expect(r.warnings.some((w) => w.startsWith(`forms.${ID}.grid.t.s.row`))).toBe(true);
    });

    it('drops non-object grid, tab and section entries with warnings', () => {
      const a = one({ grid: 5 });
      expect(a.model?.forms[ID].grid).toEqual({});
      expect(a.warnings.length).toBe(1);
      const b = one({ grid: { t: 'x' } });
      expect(b.model?.forms[ID].grid).toEqual({});
      expect(b.warnings.length).toBe(1);
      const c = one({ grid: { t: { s: 'x', ok: { col: 1, row: 1 } } } });
      expect(Object.keys(c.model!.forms[ID].grid.t)).toEqual(['ok']);
      expect(c.warnings.length).toBe(1);
    });

    it('drops a section without a usable col or row', () => {
      const r = one({ grid: { t: { s: { row: 1 } }, u: { s: { row: 1 }, k: { col: 1, row: 1 } } } });
      // A tab whose placements were all dropped is not stored (the warnings explain); the others keep what remains.
      expect(Object.keys(r.model!.forms[ID].grid)).toEqual(['u']);
      expect(r.model?.forms[ID].grid.u).toEqual({ k: { col: 1, span: 1, row: 1, rowSpan: 1 } });
      expect(r.warnings).toContain(`forms.${ID}.grid.t.s needs a number "col" and "row"; it is ignored.`);
    });

    it('warns about unknown keys at every level', () => {
      const r = parsePresentation(
        JSON.stringify({ version: 1, forms: { [ID]: { x: 1, grid: { t: { s: { col: 1, row: 1, y: 2 } } }, timeline: { z: 3 } } } }),
      );
      expect(r.warnings).toContain(`Unknown key forms.${ID}.x`);
      expect(r.warnings).toContain(`Unknown key forms.${ID}.timeline.z`);
      expect(r.warnings).toContain(`Unknown key forms.${ID}.grid.t.s.y`);
      expect(r.warnings.length).toBe(3);
    });

    it('warns on invalid timeline values and keeps defaults', () => {
      const r = one({ timeline: { appearance: 'grid', density: 'tight' } });
      expect(r.model?.forms[ID].timeline).toEqual({ appearance: 'list', density: 'comfortable' });
      expect(r.warnings.length).toBe(2);
    });

    it('reads compact density', () => {
      expect(one({ timeline: { density: 'compact' } }).model?.forms[ID].timeline).toEqual({ appearance: 'list', density: 'compact' });
    });

    it('warns on a duplicate normalised id and the last wins', () => {
      const r = parse({ '{ABC}': { layout: 'wizard' }, abc: { layout: 'tabs' } });
      expect(r.warnings).toEqual(['forms.abc is given more than once; the last entry is used.']);
      expect(r.model?.forms.abc.layout).toBe('tabs');
    });

    it('absent forms gives an empty record; a non-object forms or entry warns', () => {
      expect(parsePresentation('{"version":1}').model?.forms).toEqual({});
      const a = parse(5);
      expect(a.model?.forms).toEqual({});
      expect(a.warnings.length).toBe(1);
      const b = parse({ x: 5 });
      expect(b.model?.forms).toEqual({});
      expect(b.warnings.length).toBe(1);
    });

    it('reads background colours: names and #rrggbb, fields by lowercase column', () => {
      const r = one({ background: { sections: { main: 'Yellow', side: '#A1B2C3' }, fields: { XV_Name: 'green' } } });
      expect(r.warnings).toEqual([]);
      expect(r.model?.forms[ID].background).toEqual({ sections: { main: 'yellow', side: '#a1b2c3' }, fields: { xv_name: 'green' } });
    });

    it('warns about and drops colours it does not know, and a background that is not an object', () => {
      const r = one({ background: { sections: { main: 'chartreuse', side: '#abc' }, fields: 'x', other: 1 } });
      expect(r.model?.forms[ID].background).toEqual({ sections: {}, fields: {} });
      expect(r.warnings).toEqual([
        `Unknown key forms.${ID}.background.other`,
        `forms.${ID}.background.sections.main must be one of yellow, orange, red, pink, purple, blue, teal, green, gray or a colour as #rrggbb; it is ignored.`,
        `forms.${ID}.background.sections.side must be one of yellow, orange, red, pink, purple, blue, teal, green, gray or a colour as #rrggbb; it is ignored.`,
        `forms.${ID}.background.fields must be an object; it is ignored.`,
      ]);
      const bad = one({ background: 'yellow' });
      expect(bad.model?.forms[ID].background).toBeUndefined();
      expect(bad.warnings).toEqual([`forms.${ID}.background must be an object; it is ignored.`]);
    });

    it('keeps __proto__ keys harmless', () => {
      const r = parsePresentation('{"version":1,"forms":{"__proto__":{"grid":{"__proto__":{"__proto__":{"col":1,"row":1}}}}}}');
      expect(Object.getPrototypeOf(r.model!.forms)).toBeNull();
      expect(Object.keys(r.model!.forms)).toEqual(['__proto__']);
      const grid = r.model!.forms['__proto__'].grid;
      expect(Object.getPrototypeOf(grid)).toBeNull();
      expect(({} as Record<string, unknown>).col).toBeUndefined();
    });
  });
});

describe('parsePresentation: choices', () => {
  it('keeps valid entries, keys lowercased', () => {
    const r = parsePresentation(JSON.stringify({ version: 1, choices: { 'Account.IndustryCode': { '1': { text: '#FFFF00' } } } }));
    expect(r.warnings).toEqual([]);
    expect(r.model?.choices).toEqual({ 'account.industrycode': { 1: { text: '#FFFF00' } } });
  });

  it('warns about a non-integer option value and drops it', () => {
    const r = parsePresentation(JSON.stringify({ version: 1, choices: { k: { abc: { text: '#FFFF00' } } } }));
    expect(r.warnings).toContain('choices.k.abc: the option value must be a whole number');
    expect(r.model?.choices.k ?? {}).toEqual({});
  });

  it('warns about a bad text colour and drops the entry', () => {
    const r = parsePresentation(JSON.stringify({ version: 1, choices: { k: { '1': { text: 'red' }, '2': { text: '#000000' } } } }));
    expect(r.warnings.some((w) => w.startsWith('choices.k.1'))).toBe(true);
    expect(r.model?.choices.k).toEqual({ 2: { text: '#000000' } });
  });

  it('warns when choices is not an object and still parses other sections', () => {
    const r = parsePresentation(JSON.stringify({ version: 1, choices: [], theme: { density: 'compact' } }));
    expect(r.warnings.some((w) => w.startsWith('choices must be an object'))).toBe(true);
    expect(r.model?.theme.density).toBe('compact');
    expect(r.model?.choices).toEqual({});
  });

  it('does not report choices as an unknown key', () => {
    expect(parsePresentation('{"version":1,"choices":{}}').warnings).toEqual([]);
  });

  it('reads a view\'s wrap as lowercase column names, once each, and warns about the rest', () => {
    const { model, warnings } = parsePresentation(
      JSON.stringify({ version: 1, views: { V1: { wrap: ['Title', 'title', 'pc.FullName', 3, ''] }, V2: { wrap: 'title' } } }),
    );
    expect(model!.views.v1).toEqual({ wrap: ['title', 'pc.fullname'] });
    expect(model!.views.v2).toEqual({});
    expect(warnings).toEqual([
      'views.v1.wrap: 3 is not a column name; it is ignored.',
      'views.v1.wrap: "" is not a column name; it is ignored.',
      'views.v2.wrap must be a list of column names; it is ignored.',
    ]);
  });

  it('does not read wrap in a subgrid entry (a subgrid follows its view)', () => {
    const { model, warnings } = parsePresentation(JSON.stringify({ version: 1, forms: { F1: { subgrids: { contacts: { cards: {}, wrap: ['title'] } } } } }));
    expect(model!.forms.f1.subgrids!.contacts).toEqual({ cards: {} });
    expect(warnings).toEqual(['Unknown key forms.f1.subgrids.contacts.wrap']);
  });
});

