import { Button, Field, Input, makeStyles, MessageBar, MessageBarBody, Select, Spinner, Switch, tokens } from '@fluentui/react-components';
import { useEffect, useRef, useState } from 'react';
import { Link } from 'react-router-dom';
import { GRID_COLUMNS, normaliseId, type BackgroundColor, type FormBackground, type FormPresentation, type GridPlacement, type ViewPresentation } from '../../parsers/presentationJson';
import { useAuth } from '../../shell/auth/AuthProvider';
import { t } from '../../shell/i18n/t';
import { useAfterSave, useDesignerHooks } from '../common/designerHooks';
import type { AttributeMeta } from '../tables/metadata';
import { loadEntity } from '../tables/tableApi';
import { ModeFields } from '../views/ModeFields';
import { BackgroundPicker } from './BackgroundPicker';
import {
  anyMode,
  formLayoutOf,
  listOverlays,
  loadOverlay,
  OverlayUnreadable,
  saveOverlay,
  modesOnly,
  viewModesOf,
  withFormLayout,
  type OverlayChoice,
  type OverlayDoc,
} from '../views/overlayApi';
import type { SubgridSpec } from './formControls';
import type { DesignCell, DesignSection, FormDesign } from './formDesign';
import { labelOf, type FormDesignCardProps } from './formDesignerSlots';

export interface FormLookEditorProps extends Pick<FormDesignCardProps, 'design'> {
  /**
   * The saved form's id when the card mounts; undefined for a new form (its entry is written under the id the create
   * returns). Later ids come from the after-save step: a save under another id (Save as) copies the entry to it.
   */
  formId: string | undefined;
}

const useStyles = makeStyles({
  form: { display: 'flex', flexDirection: 'column', rowGap: tokens.spacingVerticalM, maxWidth: '640px' },
  placement: {
    display: 'grid',
    gridTemplateColumns: 'repeat(auto-fit, minmax(120px, 1fr))',
    columnGap: tokens.spacingHorizontalM,
    rowGap: tokens.spacingVerticalS,
    alignItems: 'end',
    borderTop: `1px solid ${tokens.colorNeutralStroke2}`,
    paddingTop: tokens.spacingVerticalS,
    margin: 0,
    border: 'none',
  },
  legend: { fontWeight: tokens.fontWeightSemibold, gridColumn: '1 / -1', padding: 0 },
  invalid: { gridColumn: '1 / -1', margin: 0, color: tokens.colorPaletteRedForeground1 },
  colours: { gridColumn: '1 / -1', display: 'flex', flexDirection: 'column', rowGap: tokens.spacingVerticalXS },
  fieldsTitle: { margin: 0, fontWeight: tokens.fontWeightSemibold },
  fields: { display: 'flex', flexDirection: 'column', rowGap: tokens.spacingVerticalXS, paddingInlineStart: tokens.spacingHorizontalM },
  subgridsTitle: { margin: 0, fontWeight: tokens.fontWeightSemibold, fontSize: tokens.fontSizeBase400 },
  subgrid: {
    display: 'flex',
    flexDirection: 'column',
    rowGap: tokens.spacingVerticalS,
    borderTop: `1px solid ${tokens.colorNeutralStroke2}`,
    paddingTop: tokens.spacingVerticalS,
    margin: 0,
    border: 'none',
  },
  table: { fontWeight: tokens.fontWeightRegular, color: tokens.colorNeutralForeground3, marginInlineStart: tokens.spacingHorizontalS },
  useView: { alignSelf: 'flex-start' },
});

const message = (e: unknown) => (e instanceof Error ? e.message : String(e));
const EMPTY: FormPresentation = { layout: 'tabs', grid: {} };
/** What a section gets when it leaves the default flow. */
const STARTING: GridPlacement = { col: 1, span: 1, row: 1, rowSpan: 1 };

/** A placement the apps accept as it stands (UI-2c's `parsePlacement` would clamp anything else). */
export function validPlacement(p: GridPlacement): boolean {
  const whole = (n: number, min: number, max = Number.MAX_SAFE_INTEGER) => Number.isInteger(n) && n >= min && n <= max;
  return whole(p.col, 1, GRID_COLUMNS) && whole(p.span, 1, GRID_COLUMNS) && p.col + p.span - 1 <= GRID_COLUMNS && whole(p.row, 1) && whole(p.rowSpan, 1);
}

const sectionsOf = (tab: FormDesign['tabs'][number]): DesignSection[] => tab.columns.flatMap((c) => c.sections);
/** The field cells of a section that can take a colour (one per column). */
const fieldCellsOf = (section: DesignSection): DesignCell[] => {
  const seen = new Set<string>();
  return section.cells.filter((c) => {
    const key = c.kind === 'field' ? c.attribute?.toLowerCase() : undefined;
    if (!key || seen.has(key)) return false;
    seen.add(key);
    return true;
  });
};
const allSections = (d: FormDesign) => d.tabs.flatMap(sectionsOf);
const sectionNames = (d: FormDesign) => new Set(allSections(d).map((s) => s.name));
const fieldNames = (d: FormDesign) => new Set(allSections(d).flatMap((s) => s.cells.filter((c) => c.kind === 'field' && c.attribute).map((c) => c.attribute!.toLowerCase())));
/** The subgrid cells of the form, one per control id (lowercase, trimmed: the key of `subgrids`), in reading order. */
const subgridCellsOf = (d: FormDesign): { key: string; cell: DesignCell; spec: SubgridSpec }[] => {
  const seen = new Set<string>();
  const out: { key: string; cell: DesignCell; spec: SubgridSpec }[] = [];
  for (const s of allSections(d)) {
    for (const c of s.cells) {
      if (c.kind !== 'subgrid' || c.control?.kind !== 'subgrid') continue;
      const key = c.control.controlId.trim().toLowerCase();
      if (!key || seen.has(key)) continue;
      seen.add(key);
      out.push({ key, cell: c, spec: c.control });
    }
  }
  return out;
};
/** The subgrids an entry can configure: those of the form that no custom control replaces (its entry is ignored at runtime). */
const subgridNames = (d: FormDesign) => new Set(subgridCellsOf(d).filter((x) => !x.cell.pcf).map((x) => x.key));
const hasTimeline = (d: FormDesign) => d.tabs.some((tab) => sectionsOf(tab).some((s) => s.cells.some((c) => c.kind === 'timeline')));

/**
 * True when `look` places a tab or a section, or configures a subgrid, the form no longer has (compared by name, not by order),
 * or configures a subgrid a custom control now replaces.
 */
export function hasStale(look: FormPresentation, design: FormDesign): boolean {
  if (look.background) {
    const sections = sectionNames(design);
    const fields = fieldNames(design);
    if (Object.keys(look.background.sections).some((n) => !sections.has(n)) || Object.keys(look.background.fields).some((n) => !fields.has(n))) return true;
  }
  if (look.subgrids) {
    const subgrids = subgridNames(design);
    if (Object.keys(look.subgrids).some((n) => !subgrids.has(n))) return true;
  }
  return Object.entries(look.grid).some(([tabName, placed]) => {
    const tab = design.tabs.find((x) => x.name === tabName);
    if (!tab) return true;
    const names = sectionsOf(tab).map((x) => x.name);
    return Object.keys(placed).some((n) => !names.includes(n));
  });
}

/**
 * `look` as it is written: placements of tabs and sections the form no longer have are left out; an invalid placement is
 * not saved, so the one stored for that section (`stored`, when valid) stays as it was.
 */
export function writable(look: FormPresentation, design: FormDesign, stored?: FormPresentation): FormPresentation {
  const grid: FormPresentation['grid'] = {};
  for (const tab of design.tabs) {
    const placed = look.grid[tab.name];
    if (!placed) continue;
    const kept: Record<string, GridPlacement> = {};
    for (const s of sectionsOf(tab)) {
      const p = placed[s.name];
      if (!p) continue;
      const fallback = stored?.grid[tab.name]?.[s.name];
      if (validPlacement(p)) kept[s.name] = p;
      else if (fallback && validPlacement(fallback)) kept[s.name] = fallback;
    }
    if (Object.keys(kept).length > 0) grid[tab.name] = kept;
  }
  const result: FormPresentation = { ...look, grid };
  if (look.background) {
    const sections = sectionNames(design);
    const fields = fieldNames(design);
    const background: FormBackground = {
      sections: Object.fromEntries(Object.entries(look.background.sections).filter(([n]) => sections.has(n))),
      fields: Object.fromEntries(Object.entries(look.background.fields).filter(([n]) => fields.has(n))),
    };
    if (Object.keys(background.sections).length > 0 || Object.keys(background.fields).length > 0) result.background = background;
    else delete result.background;
  }
  if (look.subgrids) {
    // S6: the entry of a subgrid the form no longer has, or that a custom control replaces, is dropped.
    const names = subgridNames(design);
    const subgrids = Object.fromEntries(Object.entries(look.subgrids).filter(([n]) => names.has(n)));
    if (Object.keys(subgrids).length > 0) result.subgrids = subgrids;
    else delete result.subgrids;
  }
  return result;
}

type Read =
  | { state: 'loading' }
  | { state: 'ready'; doc: OverlayDoc }
  | { state: 'unreadable'; reason: string }
  | { state: 'failed'; detail: string };

/** The columns of a subgrid's table, by table (lowercase). */
type Columns = { state: 'ready'; attributes: AttributeMeta[] } | { state: 'failed'; detail: string };

/**
 * Modern look (F11): this form's entry in a presentation overlay's `forms` (UI-2c shape): tabs or a wizard, the timeline's
 * appearance and density (when the form has a timeline), and the grid placement of the sections of a tab. Changes are
 * written after the form's save, and only when this card changed (or a section it places was deleted); the overlay is read
 * again just before it is written, so nothing else in it is lost.
 */
export function FormLookEditor({ design, formId }: FormLookEditorProps) {
  const { api } = useAuth();
  const { markDirty } = useDesignerHooks();
  const styles = useStyles();
  const [overlays, setOverlays] = useState<OverlayChoice[]>();
  const [listError, setListError] = useState<string>();
  const [selected, setSelected] = useState<string>();
  const [read, setRead] = useState<Read>({ state: 'loading' });
  const [look, setLook] = useState<FormPresentation>(EMPTY);
  const [saveError, setSaveError] = useState<string>();
  const [tabKey, setTabKey] = useState<string>();
  const [columns, setColumns] = useState<Record<string, Columns>>({});
  /** The tables whose columns were asked for (each is read once). */
  const asked = useRef(new Set<string>());
  /** Changes not written yet; the after-save step reads it, so it is a ref as well as the state that drives it. */
  const changed = useRef(false);
  const latest = useRef(look);
  latest.current = look;
  const latestDesign = useRef(design);
  latestDesign.current = design;
  /** The form whose entry the card holds; it moves to a saved id only in the after-save step (so a Save as copies). */
  const shownFor = useRef(formId);

  useEffect(() => {
    if (!api) return;
    let live = true;
    listOverlays(api)
      .then((list) => {
        if (!live) return;
        setOverlays(list);
        setSelected((s) => s ?? list[0]?.webResourceName);
      })
      .catch((e: unknown) => live && setListError(message(e)));
    return () => {
      live = false;
    };
  }, [api]);

  useEffect(() => {
    if (!api || !selected) return;
    let live = true;
    setRead({ state: 'loading' });
    loadOverlay(api, selected)
      .then((doc) => live && setRead({ state: 'ready', doc }))
      .catch((e: unknown) =>
        live && setRead(e instanceof OverlayUnreadable ? { state: 'unreadable', reason: e.reason } : { state: 'failed', detail: message(e) }),
      );
    return () => {
      live = false;
    };
  }, [api, selected]);

  // The columns of the tables of the subgrids that offer modes (not those a custom control replaces), each read once.
  const tables = [...new Set(subgridCellsOf(design).filter((x) => !x.cell.pcf).map((x) => x.spec.targetEntity.trim().toLowerCase()))]
    .filter((table) => table !== '')
    .sort()
    .join(',');
  useEffect(() => {
    if (!api || !tables) return;
    for (const table of tables.split(',')) {
      if (asked.current.has(table)) continue;
      asked.current.add(table);
      loadEntity(api, table)
        .then((entity) => setColumns((c) => ({ ...c, [table]: { state: 'ready', attributes: entity.attributes } })))
        .catch((e: unknown) => setColumns((c) => ({ ...c, [table]: { state: 'failed', detail: message(e) } })));
    }
  }, [api, tables]);

  const doc = read.state === 'ready' ? read.doc : undefined;
  useEffect(() => {
    if (!doc || changed.current) return;
    const id = shownFor.current;
    setLook((id && formLayoutOf(doc, id)) || EMPTY);
  }, [doc]);

  useAfterSave('formLook', async (id) => {
    const copy = shownFor.current !== undefined && normaliseId(id) !== normaliseId(shownFor.current);
    const unknown = overlays === undefined || listError !== undefined || (selected !== undefined && read.state !== 'ready');
    if (api && copy && !changed.current && unknown) {
      // The source's entry is not known (the overlay list or the overlay is loading, failed or unreadable): say so.
      shownFor.current = id;
      setSaveError(t('forms.look.notCopied'));
      return;
    }
    const writing = latest.current;
    // A placement of a section deleted in the designer is dropped even when nothing else changed.
    const dropping = hasStale(writing, latestDesign.current);
    if (!api || !selected || !(changed.current || copy || dropping)) {
      shownFor.current = id;
      return;
    }
    setSaveError(undefined);
    try {
      const fresh = await loadOverlay(api, selected);
      const pruned = writable(writing, latestDesign.current, shownFor.current ? formLayoutOf(fresh, shownFor.current) : undefined);
      const next = withFormLayout(fresh, id, pruned);
      // Nothing to write (a copy of a form without an entry, or an entry equal to the stored one): no PATCH, no publish.
      if (JSON.stringify(next.json) !== JSON.stringify(fresh.json)) await saveOverlay(api, next);
      shownFor.current = id;
      if (latest.current === writing) changed.current = false;
      setRead({ state: 'ready', doc: next });
    } catch (e) {
      // The entry is not written: the page stays changed, so Back still asks.
      markDirty();
      if (e instanceof OverlayUnreadable) setRead({ state: 'unreadable', reason: e.reason });
      else setSaveError(t('forms.look.saveFailed', { detail: message(e) }));
    }
  });

  const update = (next: FormPresentation) => {
    changed.current = true;
    setLook(next);
    markDirty();
  };

  const choose = (name: string) => {
    setSaveError(undefined);
    setSelected(name);
  };

  const saveBar = saveError && (
    <MessageBar intent="error">
      <MessageBarBody>{saveError}</MessageBarBody>
    </MessageBar>
  );
  if (listError)
    return (
      <>
        {saveBar}
        <MessageBar intent="error">
          <MessageBarBody>{t('forms.look.loadFailed', { detail: listError })}</MessageBarBody>
        </MessageBar>
      </>
    );
  if (!overlays)
    return (
      <>
        {saveBar}
        <Spinner size="small" label={t('common.loading')} />
      </>
    );
  if (overlays.length === 0)
    return (
      <p>
        <span>{t('forms.look.noneInUse')}</span> <Link to="/manage/environment">{t('views.modes.environmentLink')}</Link>
      </p>
    );

  const tab = design.tabs.find((x) => x.key === tabKey) ?? design.tabs[0];
  const sections = tab ? sectionsOf(tab) : [];
  const placed = (tab && look.grid[tab.name]) || {};
  const timeline = look.timeline ?? { appearance: 'list' as const, density: 'comfortable' as const };

  const place = (section: DesignSection, placement: GridPlacement | undefined) => {
    const grid = { ...look.grid };
    const tabPlacements = { ...(grid[tab.name] ?? {}) };
    if (placement) tabPlacements[section.name] = placement;
    else delete tabPlacements[section.name];
    if (Object.keys(tabPlacements).length > 0) grid[tab.name] = tabPlacements;
    else delete grid[tab.name];
    update({ ...look, grid });
  };
  const colour = (key: keyof FormBackground, name: string, value: BackgroundColor | undefined) => {
    const background: FormBackground = { sections: { ...(look.background?.sections ?? {}) }, fields: { ...(look.background?.fields ?? {}) } };
    if (value) background[key][name] = value;
    else delete background[key][name];
    const next: FormPresentation = { ...look, background };
    if (Object.keys(background.sections).length === 0 && Object.keys(background.fields).length === 0) delete next.background;
    update(next);
  };
  const setSubgrid = (key: string, modes: ViewPresentation | undefined) => {
    const subgrids = { ...(look.subgrids ?? {}) };
    if (modes && anyMode(modes)) subgrids[key] = modes;
    else delete subgrids[key];
    const next: FormPresentation = { ...look, subgrids };
    if (Object.keys(subgrids).length === 0) delete next.subgrids;
    update(next);
  };
  const subgridCells = subgridCellsOf(design);
  const number = (section: DesignSection, key: keyof GridPlacement, label: string) => {
    const p = placed[section.name];
    return (
      <Field label={label}>
        <Input
          type="number"
          value={Number.isNaN(p[key]) ? '' : String(p[key])}
          onChange={(_, d) => place(section, { ...p, [key]: d.value.trim() === '' ? NaN : Number(d.value) })}
        />
      </Field>
    );
  };

  return (
    <div className={styles.form}>
      <p>{t('forms.look.intro')}</p>
      <Field label={t('views.modes.overlay')}>
        <Select value={selected} onChange={(_, d) => choose(d.value)}>
          {overlays.map((o) => (
            <option key={o.webResourceName} value={o.webResourceName}>
              {o.label}
            </option>
          ))}
        </Select>
      </Field>
      {saveBar}
      {read.state === 'loading' && <Spinner size="small" label={t('common.loading')} />}
      {read.state === 'failed' && (
        <MessageBar intent="error">
          <MessageBarBody>{t('forms.look.loadFailed', { detail: read.detail })}</MessageBarBody>
        </MessageBar>
      )}
      {read.state === 'unreadable' && (
        <MessageBar intent="warning">
          <MessageBarBody>{t('forms.look.unreadable', { reason: read.reason })}</MessageBarBody>
        </MessageBar>
      )}
      {read.state === 'ready' && (
        <>
          <Field label={t('forms.look.layout')}>
            <Select value={look.layout} onChange={(_, d) => update({ ...look, layout: d.value === 'wizard' ? 'wizard' : 'tabs' })}>
              <option value="tabs">{t('forms.look.layout.tabs')}</option>
              <option value="wizard">{t('forms.look.layout.wizard')}</option>
            </Select>
          </Field>
          {hasTimeline(design) && (
            <>
              <Field label={t('forms.look.timeline.appearance')}>
                <Select value={timeline.appearance} onChange={(_, d) => update({ ...look, timeline: { ...timeline, appearance: d.value === 'cards' ? 'cards' : 'list' } })}>
                  <option value="list">{t('forms.look.timeline.list')}</option>
                  <option value="cards">{t('forms.look.timeline.cards')}</option>
                </Select>
              </Field>
              <Field label={t('forms.look.timeline.density')}>
                <Select value={timeline.density} onChange={(_, d) => update({ ...look, timeline: { ...timeline, density: d.value === 'compact' ? 'compact' : 'comfortable' } })}>
                  <option value="comfortable">{t('forms.look.timeline.comfortable')}</option>
                  <option value="compact">{t('forms.look.timeline.compact')}</option>
                </Select>
              </Field>
            </>
          )}
          {tab && (
            <>
              <Field label={t('forms.look.grid.tab')} hint={t('forms.look.grid.title')}>
                <Select value={tab.key} onChange={(_, d) => setTabKey(d.value)}>
                  {design.tabs.map((x) => (
                    <option key={x.key} value={x.key}>
                      {labelOf(x.labels, design.lcid, x.name)}
                    </option>
                  ))}
                </Select>
              </Field>
              {sections.length === 0 && <p>{t('forms.look.grid.noSections')}</p>}
              {sections.map((s) => {
                const p = placed[s.name];
                const name = labelOf(s.labels, design.lcid, s.name);
                return (
                  <fieldset key={s.key} className={styles.placement} aria-label={name}>
                    <legend className={styles.legend}>{name}</legend>
                    <Switch
                      label={t('forms.look.grid.default')}
                      checked={!p}
                      onChange={(_, d) => place(s, d.checked ? undefined : STARTING)}
                    />
                    {p && (
                      <>
                        {number(s, 'col', t('forms.look.grid.col'))}
                        {number(s, 'span', t('forms.look.grid.span'))}
                        {number(s, 'row', t('forms.look.grid.row'))}
                        {number(s, 'rowSpan', t('forms.look.grid.rowSpan'))}
                        {!validPlacement(p) && (
                          <p role="alert" className={styles.invalid}>
                            {t('forms.look.grid.invalid')}
                          </p>
                        )}
                      </>
                    )}
                    {s.name && (
                      <div className={styles.colours}>
                        <BackgroundPicker
                          label={`${t('forms.look.background.section')}: ${name}`}
                          value={look.background?.sections[s.name]}
                          onChange={(v) => colour('sections', s.name, v)}
                        />
                        <p className={styles.fieldsTitle}>{t('forms.look.background.fields')}</p>
                        {fieldCellsOf(s).length === 0 ? (
                          <p>{t('forms.look.background.noFields')}</p>
                        ) : (
                          <div className={styles.fields}>
                            <span>{t('forms.look.background.inherited')}</span>
                            {fieldCellsOf(s).map((c) => {
                              const attribute = c.attribute!.toLowerCase();
                              return (
                                <BackgroundPicker
                                  key={c.key}
                                  label={labelOf(c.labels, design.lcid, c.attribute!)}
                                  value={look.background?.fields[attribute]}
                                  onChange={(v) => colour('fields', attribute, v)}
                                />
                              );
                            })}
                          </div>
                        )}
                      </div>
                    )}
                  </fieldset>
                );
              })}
            </>
          )}
          <p className={styles.subgridsTitle}>{t('forms.look.subgrids')}</p>
          {subgridCells.length === 0 ? (
            <p>{t('forms.look.subgridsNone')}</p>
          ) : (
            <>
              <p>{t('forms.look.subgridsIntro')}</p>
              {subgridCells.map(({ key, cell, spec }) => {
                const name = labelOf(cell.labels, design.lcid, spec.controlId);
                const table = spec.targetEntity.trim().toLowerCase();
                // A subgrid that names no table cannot list its columns (and stays a grid at runtime).
                const cols: Columns | undefined = table ? columns[table] : { state: 'failed', detail: t('record.subgrid.noTarget') };
                const fromView = spec.viewId ? modesOnly(viewModesOf(read.doc, spec.viewId)) : undefined;
                return (
                  <fieldset key={key} className={styles.subgrid} aria-label={name}>
                    <legend className={styles.legend}>
                      {name}
                      <span className={styles.table}>{spec.targetEntity}</span>
                    </legend>
                    {cell.pcf ? (
                      <p>{t('forms.look.subgridPcf')}</p>
                    ) : !cols ? (
                      <Spinner size="tiny" label={t('common.loading')} />
                    ) : cols.state === 'failed' ? (
                      <MessageBar intent="error">
                        <MessageBarBody>{t('forms.look.subgridTableFailed', { table: spec.targetEntity, detail: cols.detail })}</MessageBarBody>
                      </MessageBar>
                    ) : (
                      <>
                        {anyMode(fromView) && (
                          <Button className={styles.useView} size="small" onClick={() => setSubgrid(key, { ...fromView })}>
                            {t('forms.look.useViewModes')}
                          </Button>
                        )}
                        <ModeFields
                          attributes={cols.attributes}
                          value={look.subgrids?.[key] ?? {}}
                          onChange={(next) => setSubgrid(key, next)}
                          idPrefix={`subgrid-${key}`}
                        />
                      </>
                    )}
                  </fieldset>
                );
              })}
            </>
          )}
        </>
      )}
    </div>
  );
}
