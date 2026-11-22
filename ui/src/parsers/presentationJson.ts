import type { BrandVariants } from '@fluentui/react-components';

/** The parsed presentation overlay (web resource JSON, version 1). */
export interface PresentationModel {
  theme: {
    brand?: BrandVariants;
    fontFamily?: string;
    density: 'comfortable' | 'compact';
    colorScheme?: 'system' | 'light' | 'dark';
  };
  whiteLabel: { name?: string; logo?: string; favicon?: string };
  /** Per-view presentation, keyed by the normalised view id (lowercase, no braces). */
  views: Record<string, ViewPresentation>;
  /** Per-form presentation, keyed by the normalised form id (lowercase, no braces). */
  forms: Record<string, FormPresentation>;
  /** Text colour per choice value, keyed by the global choice's name or `table.column` (lowercase), then by option value. */
  choices: Record<string, Record<number, { text: string }>>;
}

/** Where a section sits on the 12-column free grid of a tab. */
export interface GridPlacement {
  col: number;
  span: number;
  row: number;
  rowSpan: number;
}

/** How one main form is presented. Tab and section names are checked later, against the FormXml. */
export interface FormPresentation {
  layout: 'tabs' | 'wizard';
  /** tab name → section name → placement. */
  grid: Record<string, Record<string, GridPlacement>>;
  timeline?: { appearance: 'list' | 'cards'; density: 'comfortable' | 'compact' };
  /** Background colours: of a section by its name (its fields show it), of a field cell by its column (wins over the section's). */
  background?: FormBackground;
  /** Per-subgrid view modes, keyed by the subgrid control's name (lowercase, trimmed); an entry has the shape and rules of a `views` entry. */
  subgrids?: Record<string, ViewPresentation>;
}

/** The named background colours: each follows the light or dark theme. */
export const BACKGROUND_NAMES = ['yellow', 'orange', 'red', 'pink', 'purple', 'blue', 'teal', 'green', 'gray'] as const;
export type BackgroundName = (typeof BACKGROUND_NAMES)[number];
/** A named colour, or a free colour as `#rrggbb` (lowercase; the same in light and dark). */
export type BackgroundColor = BackgroundName | `#${string}`;

export interface FormBackground {
  /** Section name → colour. */
  sections: Record<string, BackgroundColor>;
  /** Column logical name → colour, for the field cells of the form's sections. */
  fields: Record<string, BackgroundColor>;
}

/** The colour as it is stored (a known name, or `#rrggbb` in lowercase); undefined when it is neither. */
export function backgroundColorOf(value: unknown): BackgroundColor | undefined {
  if (typeof value !== 'string') return undefined;
  const v = value.trim().toLowerCase();
  if ((BACKGROUND_NAMES as readonly string[]).includes(v)) return v as BackgroundName;
  return HEX.test(v) ? (v as BackgroundColor) : undefined;
}

export type ViewMode = 'grid' | 'kanban' | 'calendar' | 'cards';

/** How the calendar mode draws its records: a 6-week grid with bars, or one row per record across the month's days. */
export type CalendarLayout = 'month' | 'timeline';

/** How one saved view is presented. Column names are checked later, against metadata. */
export interface ViewPresentation {
  default?: ViewMode;
  kanban?: { groupBy: string };
  calendar?: { start: string; end?: string; layout?: CalendarLayout };
  cards?: Record<string, never>;
  /**
   * The columns whose text wraps onto more lines in the grid, by cell name (lowercase, as in the LayoutXml, `alias.attr` for a
   * related column). Every other column stays on one line, cut with an ellipsis (the full text in its tooltip), as in
   * Dataverse. Views only: a subgrid follows the entry of the view it shows.
   */
  wrap?: string[];
}

/** The key a view id is stored under: lowercase, without braces. */
export function normaliseId(id: string): string {
  return id.replace(/[{}]/g, '').trim().toLowerCase();
}

const SHADES = [10, 20, 30, 40, 50, 60, 70, 80, 90, 100, 110, 120, 130, 140, 150, 160] as const;
const HEX = /^#[0-9a-fA-F]{6}$/;
/** Characters that could break out of a quoted CSS font-family value. */
const UNSAFE_FONT_CHARS = /[;{}"'`\\<>\u0000-\u001f]/g;

const isObject = (v: unknown): v is Record<string, unknown> => typeof v === 'object' && v !== null && !Array.isArray(v);

function unknownKeys(obj: Record<string, unknown>, known: string[], path: string, warnings: string[]): void {
  for (const key of Object.keys(obj)) if (!known.includes(key)) warnings.push(`Unknown key ${path ? path + '.' : ''}${key}`);
}

function parseRamp(value: unknown): BrandVariants | undefined {
  if (!isObject(value)) return undefined;
  const ramp: Record<number, string> = {};
  for (const shade of SHADES) {
    const v = value[String(shade)];
    if (typeof v !== 'string' || !HEX.test(v)) return undefined;
    ramp[shade] = v;
  }
  if (Object.keys(value).length !== SHADES.length) return undefined;
  return ramp as unknown as BrandVariants;
}

/** Parses the presentation JSON. Problems that leave the overlay usable become warnings; an unusable overlay becomes `error`. */
export function parsePresentation(text: string): { model?: PresentationModel; warnings: string[]; error?: string } {
  const warnings: string[] = [];
  let raw: unknown;
  try {
    raw = JSON.parse(text);
  } catch (e) {
    return { warnings, error: `The text is not valid JSON (${e instanceof Error ? e.message : String(e)}).` };
  }
  if (!isObject(raw)) return { warnings, error: 'The text is valid JSON but not an object.' };
  if (raw.version === undefined) return { warnings, error: 'This presentation has no version; it needs "version": 1.' };
  if (raw.version !== 1) return { warnings, error: `This presentation uses version ${String(raw.version)}, which this version does not read.` };

  unknownKeys(raw, ['version', 'theme', 'whiteLabel', 'views', 'forms', 'choices'], '', warnings);

  const model: PresentationModel = { theme: { density: 'comfortable' }, whiteLabel: {}, views: Object.create(null) as Record<string, ViewPresentation>, forms: Object.create(null) as Record<string, FormPresentation>, choices: Object.create(null) as PresentationModel['choices'] };

  if (raw.theme !== undefined) {
    if (!isObject(raw.theme)) warnings.push('theme must be an object; it is ignored.');
    else {
      const t = raw.theme;
      unknownKeys(t, ['brandRamp', 'fontFamily', 'density', 'colorScheme'], 'theme', warnings);
      if (t.brandRamp !== undefined) {
        const brand = parseRamp(t.brandRamp);
        if (brand) model.theme.brand = brand;
        else warnings.push('theme.brandRamp needs the 16 shades 10…160');
      }
      if (t.fontFamily !== undefined) {
        if (typeof t.fontFamily !== 'string') warnings.push('theme.fontFamily must be text; it is ignored.');
        else {
          const cleaned = t.fontFamily.replace(UNSAFE_FONT_CHARS, '').trim();
          if (cleaned !== t.fontFamily.trim()) warnings.push('theme.fontFamily contained characters that are not allowed; they were removed.');
          if (cleaned) model.theme.fontFamily = cleaned;
          else warnings.push('theme.fontFamily is empty; the default font is used.');
        }
      }
      if (t.density !== undefined) {
        if (t.density === 'comfortable' || t.density === 'compact') model.theme.density = t.density;
        else warnings.push('theme.density must be "comfortable" or "compact"; comfortable is used.');
      }
      if (t.colorScheme !== undefined) {
        if (t.colorScheme === 'system' || t.colorScheme === 'light' || t.colorScheme === 'dark') model.theme.colorScheme = t.colorScheme;
        else warnings.push('theme.colorScheme must be "system", "light" or "dark"; it is ignored.');
      }
    }
  }

  if (raw.whiteLabel !== undefined) {
    if (!isObject(raw.whiteLabel)) warnings.push('whiteLabel must be an object; it is ignored.');
    else {
      const w = raw.whiteLabel;
      unknownKeys(w, ['name', 'logo', 'favicon'], 'whiteLabel', warnings);
      for (const key of ['name', 'logo', 'favicon'] as const) {
        if (w[key] === undefined) continue;
        if (typeof w[key] === 'string' && (w[key] as string).trim()) model.whiteLabel[key] = (w[key] as string).trim();
        else warnings.push(`whiteLabel.${key} must be text; it is ignored.`);
      }
    }
  }

  if (raw.views !== undefined) {
    if (!isObject(raw.views)) warnings.push('views must be an object; it is ignored.');
    else {
      for (const [key, entry] of Object.entries(raw.views)) {
        const id = normaliseId(key);
        if (id in model.views) warnings.push(`views.${id} is given more than once; the last entry is used.`);
        delete model.views[id];
        parseView(id, entry, model, warnings);
      }
    }
  }

  if (raw.forms !== undefined) {
    if (!isObject(raw.forms)) warnings.push('forms must be an object; it is ignored.');
    else {
      for (const [key, entry] of Object.entries(raw.forms)) {
        const id = normaliseId(key);
        if (id in model.forms) warnings.push(`forms.${id} is given more than once; the last entry is used.`);
        delete model.forms[id];
        parseForm(id, entry, model, warnings);
      }
    }
  }

  if (raw.choices !== undefined) {
    if (!isObject(raw.choices)) warnings.push('choices must be an object; it is ignored.');
    else {
      for (const [key, values] of Object.entries(raw.choices)) {
        const id = key.trim().toLowerCase();
        if (!isObject(values)) {
          warnings.push(`choices.${id} must be an object; it is ignored.`);
          continue;
        }
        const entries = Object.create(null) as Record<number, { text: string }>;
        for (const [value, entry] of Object.entries(values)) {
          const path = `choices.${id}.${value}`;
          if (!/^-?\d+$/.test(value)) {
            warnings.push(`${path}: the option value must be a whole number`);
            continue;
          }
          if (!isObject(entry) || typeof entry.text !== 'string' || !HEX.test(entry.text)) {
            warnings.push(`${path}: "text" must be a colour like #RRGGBB; it is ignored.`);
            continue;
          }
          entries[Number(value)] = { text: entry.text };
        }
        if (id in model.choices) warnings.push(`choices.${id} is given more than once; the last entry is used.`);
        model.choices[id] = entries;
      }
    }
  }

  return { model, warnings };
}

const MODES: readonly string[] = ['grid', 'kanban', 'calendar', 'cards'];
const CALENDAR_LAYOUTS: readonly string[] = ['month', 'timeline'];
const isName = (v: unknown): v is string => typeof v === 'string' && v.trim() !== '';

/** One `views` entry read as {@link parsePresentation} reads it (`id` names it in the warnings); undefined when it is not an object. */
export function parseViewEntry(id: string, entry: unknown, warnings: string[] = []): ViewPresentation | undefined {
  return parseViewBody(`views.${id}`, entry, warnings, true);
}

function parseView(id: string, entry: unknown, model: PresentationModel, warnings: string[]): void {
  const view = parseViewBody(`views.${id}`, entry, warnings, true);
  if (view) model.views[id] = view;
}

/**
 * The modes of a `views`-shaped entry (a view's, or a form's subgrid's); `path` names it in the warnings. Undefined when it is
 * not an object. `wrap` is read for a view's entry only (`withWrap`); in a subgrid's entry it is an unknown key.
 */
function parseViewBody(path: string, entry: unknown, warnings: string[], withWrap = false): ViewPresentation | undefined {
  if (!isObject(entry)) {
    warnings.push(`${path} must be an object; it is ignored.`);
    return undefined;
  }
  unknownKeys(entry, ['default', 'kanban', 'calendar', 'cards', ...(withWrap ? ['wrap'] : [])], path, warnings);
  const view: ViewPresentation = {};
  if (withWrap && entry.wrap !== undefined) {
    if (!Array.isArray(entry.wrap)) warnings.push(`${path}.wrap must be a list of column names; it is ignored.`);
    else {
      const names: string[] = [];
      for (const name of entry.wrap) {
        if (isName(name)) {
          const key = name.trim().toLowerCase();
          if (!names.includes(key)) names.push(key);
        } else warnings.push(`${path}.wrap: ${JSON.stringify(name)} is not a column name; it is ignored.`);
      }
      if (names.length) view.wrap = names;
    }
  }
  if (entry.kanban !== undefined) {
    if (isObject(entry.kanban) && isName(entry.kanban.groupBy)) view.kanban = { groupBy: entry.kanban.groupBy.trim() };
    else warnings.push(`${path}.kanban needs a "groupBy" column name; it is ignored.`);
  }
  if (entry.calendar !== undefined) {
    const c = entry.calendar;
    if (isObject(c) && isName(c.start) && (c.end === undefined || isName(c.end))) {
      view.calendar = { start: c.start.trim() };
      if (c.end !== undefined) view.calendar.end = (c.end as string).trim();
      if (c.layout !== undefined) {
        if (typeof c.layout === 'string' && CALENDAR_LAYOUTS.includes(c.layout)) view.calendar.layout = c.layout as CalendarLayout;
        else warnings.push(`${path}.calendar.layout must be "month" or "timeline"; it is ignored.`);
      }
    } else warnings.push(`${path}.calendar needs a "start" column name (and "end" as text when given); it is ignored.`);
  }
  if (entry.cards !== undefined) {
    if (isObject(entry.cards)) view.cards = {};
    else warnings.push(`${path}.cards must be an object; it is ignored.`);
  }
  if (entry.default !== undefined) {
    const d = entry.default;
    if (typeof d !== 'string' || !MODES.includes(d)) warnings.push(`${path}.default must be "grid", "kanban", "calendar" or "cards"; it is ignored.`);
    else if (d !== 'grid' && !view[d as 'kanban' | 'calendar' | 'cards']) warnings.push(`${path}.default is "${d}" but that mode is not configured; it is ignored.`);
    else view.default = d as ViewMode;
  }
  return view;
}

/** The columns of a tab's free grid (ruling C3). */
export const GRID_COLUMNS = 12;

/** Reads a whole number in [min, max]; clamps (with a warning) anything else. Returns undefined when it is not a number. */
function clampInt(value: unknown, min: number, max: number, path: string, warnings: string[]): number | undefined {
  if (typeof value !== 'number' || !Number.isFinite(value)) return undefined;
  const whole = Math.floor(value);
  const clamped = Math.min(max, Math.max(min, whole));
  if (clamped !== value) warnings.push(`${path} must be a whole number from ${min} to ${max}; ${clamped} is used.`);
  return clamped;
}

function parsePlacement(path: string, entry: unknown, warnings: string[]): GridPlacement | undefined {
  if (!isObject(entry)) {
    warnings.push(`${path} must be an object; it is ignored.`);
    return undefined;
  }
  unknownKeys(entry, ['col', 'span', 'row', 'rowSpan'], path, warnings);
  const col = clampInt(entry.col, 1, GRID_COLUMNS, `${path}.col`, warnings);
  const row = clampInt(entry.row, 1, Number.MAX_SAFE_INTEGER, `${path}.row`, warnings);
  if (col === undefined || row === undefined) {
    warnings.push(`${path} needs a number "col" and "row"; it is ignored.`);
    return undefined;
  }
  let span = 1;
  if (entry.span !== undefined) {
    const s = clampInt(entry.span, 1, GRID_COLUMNS, `${path}.span`, warnings);
    if (s === undefined) warnings.push(`${path}.span must be a number; 1 is used.`);
    else span = s;
  }
  if (col + span - 1 > GRID_COLUMNS) {
    span = GRID_COLUMNS - col + 1;
    warnings.push(`${path}.span runs past column ${GRID_COLUMNS}; ${span} is used.`);
  }
  let rowSpan = 1;
  if (entry.rowSpan !== undefined) {
    const r = clampInt(entry.rowSpan, 1, Number.MAX_SAFE_INTEGER, `${path}.rowSpan`, warnings);
    if (r === undefined) warnings.push(`${path}.rowSpan must be a number; 1 is used.`);
    else rowSpan = r;
  }
  return { col, span, row, rowSpan };
}

/** One `forms` entry read as {@link parsePresentation} reads it (`id` names it in the warnings); undefined when it is not an object. */
export function parseFormEntry(id: string, entry: unknown, warnings: string[] = []): FormPresentation | undefined {
  const holder = { forms: Object.create(null) as Record<string, FormPresentation> } as PresentationModel;
  parseForm(id, entry, holder, warnings);
  return holder.forms[id];
}

function parseForm(id: string, entry: unknown, model: PresentationModel, warnings: string[]): void {
  const path = `forms.${id}`;
  if (!isObject(entry)) {
    warnings.push(`${path} must be an object; it is ignored.`);
    return;
  }
  unknownKeys(entry, ['layout', 'grid', 'timeline', 'background', 'subgrids'], path, warnings);
  const form: FormPresentation = { layout: 'tabs', grid: Object.create(null) as FormPresentation['grid'] };
  if (entry.layout !== undefined) {
    if (entry.layout === 'tabs' || entry.layout === 'wizard') form.layout = entry.layout;
    else warnings.push(`${path}.layout must be "tabs" or "wizard"; tabs is used.`);
  }
  if (entry.grid !== undefined) {
    if (!isObject(entry.grid)) warnings.push(`${path}.grid must be an object; it is ignored.`);
    else {
      for (const [tab, sections] of Object.entries(entry.grid)) {
        const tabPath = `${path}.grid.${tab}`;
        if (!isObject(sections)) {
          warnings.push(`${tabPath} must be an object; it is ignored.`);
          continue;
        }
        const placed = Object.create(null) as Record<string, GridPlacement>;
        const given = Object.entries(sections);
        for (const [section, value] of given) {
          const placement = parsePlacement(`${tabPath}.${section}`, value, warnings);
          if (placement) placed[section] = placement;
        }
        // Every placement was dropped (each with its warning): the tab keeps its classic body.
        if (given.length > 0 && Object.keys(placed).length === 0) continue;
        form.grid[tab] = placed;
      }
    }
  }
  if (entry.timeline !== undefined) {
    if (!isObject(entry.timeline)) warnings.push(`${path}.timeline must be an object; it is ignored.`);
    else {
      const t = entry.timeline;
      unknownKeys(t, ['appearance', 'density'], `${path}.timeline`, warnings);
      const timeline: NonNullable<FormPresentation['timeline']> = { appearance: 'list', density: 'comfortable' };
      if (t.appearance !== undefined) {
        if (t.appearance === 'list' || t.appearance === 'cards') timeline.appearance = t.appearance;
        else warnings.push(`${path}.timeline.appearance must be "list" or "cards"; list is used.`);
      }
      if (t.density !== undefined) {
        if (t.density === 'comfortable' || t.density === 'compact') timeline.density = t.density;
        else warnings.push(`${path}.timeline.density must be "comfortable" or "compact"; comfortable is used.`);
      }
      form.timeline = timeline;
    }
  }
  if (entry.background !== undefined) {
    const background = parseBackground(`${path}.background`, entry.background, warnings);
    if (background) form.background = background;
  }
  if (entry.subgrids !== undefined) {
    if (!isObject(entry.subgrids)) warnings.push(`${path}.subgrids must be an object; it is ignored.`);
    else {
      const subgrids = Object.create(null) as Record<string, ViewPresentation>;
      for (const [rawKey, value] of Object.entries(entry.subgrids)) {
        const key = rawKey.trim().toLowerCase();
        const subPath = `${path}.subgrids.${key}`;
        if (key in subgrids) warnings.push(`${subPath} is given more than once; the last entry is used.`);
        delete subgrids[key];
        const view = parseViewBody(subPath, value, warnings);
        if (view) subgrids[key] = view;
      }
      form.subgrids = subgrids;
    }
  }
  model.forms[id] = form;
}

function parseBackground(path: string, value: unknown, warnings: string[]): FormBackground | undefined {
  if (!isObject(value)) {
    warnings.push(`${path} must be an object; it is ignored.`);
    return undefined;
  }
  unknownKeys(value, ['sections', 'fields'], path, warnings);
  const background: FormBackground = { sections: Object.create(null) as FormBackground['sections'], fields: Object.create(null) as FormBackground['fields'] };
  for (const key of ['sections', 'fields'] as const) {
    const given = value[key];
    if (given === undefined) continue;
    if (!isObject(given)) {
      warnings.push(`${path}.${key} must be an object; it is ignored.`);
      continue;
    }
    for (const [name, colour] of Object.entries(given)) {
      const parsed = backgroundColorOf(colour);
      if (parsed && name.trim()) background[key][key === 'fields' ? name.trim().toLowerCase() : name.trim()] = parsed;
      else warnings.push(`${path}.${key}.${name} must be one of ${BACKGROUND_NAMES.join(', ')} or a colour as #rrggbb; it is ignored.`);
    }
  }
  return background;
}
