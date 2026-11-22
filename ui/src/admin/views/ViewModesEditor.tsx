import { Field, makeStyles, MessageBar, MessageBarBody, Select, Spinner, Switch, tokens } from '@fluentui/react-components';
import { useEffect, useRef, useState } from 'react';
import { Link } from 'react-router-dom';
import { normaliseId, type ViewPresentation } from '../../parsers/presentationJson';
import { useAuth } from '../../shell/auth/AuthProvider';
import { t } from '../../shell/i18n/t';
import type { AttributeMeta } from '../tables/metadata';
import { useAfterSave, useDesignerHooks } from './designerSlots';
import { ModeFields } from './ModeFields';
import { listOverlays, loadOverlay, OverlayUnreadable, saveOverlay, viewModesOf, withViewModes, type OverlayChoice, type OverlayDoc } from './overlayApi';

export interface ViewModesEditorProps {
  /** The table's own columns. */
  attributes: AttributeMeta[];
  /**
   * The saved view's id when the card mounts; undefined for a new view (its entry is written under the id the create
   * returns). Later ids come from the after-save step: a save under another id (Save as) copies the modes to it.
   */
  viewId: string | undefined;
  /** The view's columns as the designer holds them (cell name and header), for their "Wrap text" switches. */
  columns?: { name: string; header: string }[];
}

const useStyles = makeStyles({
  form: { display: 'flex', flexDirection: 'column', rowGap: tokens.spacingVerticalM, maxWidth: '480px' },
  wrap: { display: 'flex', flexDirection: 'column', rowGap: tokens.spacingVerticalXS, border: 'none', margin: 0, padding: 0 },
  legend: { fontWeight: tokens.fontWeightSemibold, padding: 0, marginBottom: tokens.spacingVerticalXS },
});

/** `modes` with `wrap` kept to the columns the view still has (in the view's order); no `wrap` when none is left. */
export function wrapOfColumns(modes: ViewPresentation, columns: string[] | undefined): ViewPresentation {
  if (!columns) return modes;
  const kept = columns.filter((c) => modes.wrap?.includes(c.toLowerCase())).map((c) => c.toLowerCase());
  const { wrap: _wrap, ...rest } = modes;
  return kept.length ? { ...rest, wrap: kept } : rest;
}

const message = (e: unknown) => (e instanceof Error ? e.message : String(e));
export { boardColumns, dateColumns } from './ModeFields';

type Read =
  | { state: 'loading' }
  | { state: 'ready'; doc: OverlayDoc }
  | { state: 'unreadable'; reason: string }
  | { state: 'failed'; detail: string };

/**
 * View modes (modern look), E11: this view's entry in a presentation overlay's `views` (UI-2b shape): a board grouped by a
 * choice column, a calendar on date columns, cards, and the default mode. Changes are written after the view's save, and
 * only when this card changed; the overlay is read again just before it is written, so nothing else in it is lost.
 */
export function ViewModesEditor({ attributes, viewId, columns }: ViewModesEditorProps) {
  const { api } = useAuth();
  const { markDirty } = useDesignerHooks();
  const styles = useStyles();
  const [overlays, setOverlays] = useState<OverlayChoice[]>();
  const [listError, setListError] = useState<string>();
  const [selected, setSelected] = useState<string>();
  const [read, setRead] = useState<Read>({ state: 'loading' });
  const [modes, setModes] = useState<ViewPresentation>({});
  const [saveError, setSaveError] = useState<string>();
  /** Changes not written yet; the after-save step reads it, so it is a ref as well as the state that drives it. */
  const changed = useRef(false);
  const latest = useRef(modes);
  latest.current = modes;
  const columnNames = useRef<string[] | undefined>(undefined);
  columnNames.current = columns?.map((c) => c.name);
  /**
   * The view whose entry the form holds. It moves to a saved id only in the after-save step, so a Save as (the address moves
   * to the copy before the step runs) still knows the form holds the source's modes and copies them.
   */
  const shownFor = useRef(viewId);

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

  // The form shows the overlay's entry for the view, unless the user has changes not written yet (they then apply to the
  // overlay chosen next too).
  const doc = read.state === 'ready' ? read.doc : undefined;
  useEffect(() => {
    if (!doc || changed.current) return;
    const id = shownFor.current;
    setModes((id && viewModesOf(doc, id)) || {});
  }, [doc]);

  useAfterSave('viewModes', async (id) => {
    // A save under another id than the one shown is a Save as: the copy gets the modes the form holds.
    const copy = shownFor.current !== undefined && normaliseId(id) !== normaliseId(shownFor.current);
    const unknown = overlays === undefined || listError !== undefined || (selected !== undefined && read.state !== 'ready');
    if (api && copy && !changed.current && unknown) {
      // The source's modes are not known (the overlay list or the overlay is loading, failed or unreadable): say so rather
      // than copy nothing.
      shownFor.current = id;
      setSaveError(t('views.modes.notCopied'));
      return;
    }
    // A wrapped column the view no longer shows is dropped from the overlay too.
    const before = latest.current;
    const writing = wrapOfColumns(before, columnNames.current);
    const stale = (before.wrap?.length ?? 0) !== (writing.wrap?.length ?? 0);
    if (!api || !selected || !(changed.current || copy || stale)) {
      shownFor.current = id;
      return;
    }
    setSaveError(undefined);
    try {
      const fresh = await loadOverlay(api, selected);
      const next = withViewModes(fresh, id, writing);
      // Nothing to write (a copy of a view without modes, or modes equal to the stored ones): no PATCH, no publish.
      if (JSON.stringify(next.json) !== JSON.stringify(fresh.json)) await saveOverlay(api, next);
      shownFor.current = id;
      if (latest.current === before) {
        changed.current = false;
        if (writing !== before) setModes(writing);
      }
      setRead({ state: 'ready', doc: next });
    } catch (e) {
      // The modes are not written: the page stays changed, so Back still asks.
      markDirty();
      if (e instanceof OverlayUnreadable) setRead({ state: 'unreadable', reason: e.reason });
      else setSaveError(t('views.modes.saveFailed', { detail: message(e) }));
    }
  });

  const update = (next: ViewPresentation) => {
    changed.current = true;
    setModes(next);
    markDirty();
  };

  /** Another overlay: unsaved mode changes stay in the form and are written to the chosen overlay. */
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
          <MessageBarBody>{t('views.modes.loadFailed', { detail: listError })}</MessageBarBody>
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
        <span>{t('views.modes.noneInUse')}</span> <Link to="/manage/environment">{t('views.modes.environmentLink')}</Link>
      </p>
    );

  return (
    <div className={styles.form}>
      <p>{t('views.modes.intro')}</p>
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
          <MessageBarBody>{t('views.modes.loadFailed', { detail: read.detail })}</MessageBarBody>
        </MessageBar>
      )}
      {read.state === 'unreadable' && (
        <MessageBar intent="warning">
          <MessageBarBody>{t('views.modes.unreadable', { reason: read.reason })}</MessageBarBody>
        </MessageBar>
      )}
      {read.state === 'ready' && (
        <>
          <ModeFields attributes={attributes} value={modes} onChange={update} />
          {columns && columns.length > 0 && (
            <fieldset className={styles.wrap}>
              <legend className={styles.legend}>{t('views.modes.wrap')}</legend>
              <p>{t('views.modes.wrapIntro')}</p>
              {columns.map((c) => {
                const key = c.name.toLowerCase();
                const on = !!modes.wrap?.includes(key);
                return (
                  <Switch
                    key={c.name}
                    label={c.header}
                    checked={on}
                    onChange={(_, d) => {
                      const others = (modes.wrap ?? []).filter((n) => n !== key);
                      const next = d.checked ? [...others, key] : others;
                      const { wrap: _wrap, ...rest } = modes;
                      update(next.length ? { ...rest, wrap: next } : rest);
                    }}
                  />
                );
              })}
            </fieldset>
          )}
        </>
      )}
    </div>
  );
}
