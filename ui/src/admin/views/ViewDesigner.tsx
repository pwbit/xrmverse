import { Button, Field, Input, MessageBar, MessageBarBody, Spinner, Textarea } from '@fluentui/react-components';
import { Fragment, useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import { useNavigate, useParams } from 'react-router-dom';
import { useAuth } from '../../shell/auth/AuthProvider';
import { t } from '../../shell/i18n/t';
import { SaveIcon } from '../../shell/icons';
import { PageHeader } from '../../shell/layout/PageHeader';
import { useSurfaceStyles } from '../../shell/layout/surface';
import { ConfirmDialog } from '../common/ConfirmDialog';
import type { EntityMeta } from '../tables/metadata';
import { loadEntity, loadRelationships, lookupsOf } from '../tables/tableApi';
import { useLoad } from '../tables/useLoad';
import { ColumnEditor, columnHeader } from './ColumnEditor';
import { DesignerContext, type AfterSave, type DesignCardProps, type DesignerHooks } from './designerSlots';
import { FilterEditor } from './FilterEditor';
import { FindColumnsEditor } from './FindColumnsEditor';
import { SortEditor } from './SortEditor';
import { loadView, publishTable, storeView, type ViewRecord } from './viewApi';
import { newView, readView, type DesignColumn, type ViewDesign } from './viewDesign';
import { ViewModesEditor } from './ViewModesEditor';
import { ViewPreview } from './ViewPreview';

const MAX_NAME = 200;
const NEW = 'new';

/** "{name} (copy)", the base name cut so the result fits {@link MAX_NAME}. */
function copyName(name: string): string {
  const room = MAX_NAME - t('views.designer.copyName', { name: '' }).length;
  return t('views.designer.copyName', { name: name.slice(0, Math.max(0, room)) });
}

const sameId = (a: string, b: string) => a.replace(/[{}]/g, '').toLowerCase() === b.replace(/[{}]/g, '').toLowerCase();

/** A titled card of the designer page; its title names the region. */
function Card({ id, title, children }: { id: string; title: string; children: ReactNode }) {
  const surface = useSurfaceStyles();
  return (
    <section className={surface.section} aria-labelledby={id}>
      <h3 id={id} className={surface.sectionTitle}>
        {title}
      </h3>
      {children}
    </section>
  );
}

/**
 * The view designer (`/manage/tables/:table/views/:id`, `:id` = `new`): properties, columns, sort, filter, preview of one
 * `savedquery` (E1–E9), and its view modes in a presentation overlay (E11). The page holds the {@link ViewDesign}; every card gets it with `onChange` ({@link DesignCardProps})
 * and returns the next one. Save writes the view (create, then the page moves to the new id; update afterwards) and
 * publishes, then runs the cards' after-save steps ({@link DesignerContext}) with the view's id.
 */
export function ViewDesigner() {
  const { table = '', id = NEW } = useParams();
  const { api } = useAuth();
  const navigate = useNavigate();
  const tablePath = `/manage/tables/${encodeURIComponent(table)}`;

  const entity = useLoad(table, (a) => loadEntity(a, table));
  const rels = useLoad(table, (a) => loadRelationships(a));
  const [design, setDesign] = useState<ViewDesign>();
  /** The id whose design the page holds: `new` before the first save, the view's id after loading it. */
  const [heldId, setHeldId] = useState<string>();
  /**
   * The view the next save updates (undefined: the next save creates). Set as soon as a create returns, before the address
   * changes (the router moves in a transition), so a second Save never creates a second view.
   */
  const target = useRef<string>();
  const record = useLoad(id !== NEW && id !== heldId && id !== target.current ? id : undefined, (a) => loadView(a, id));
  /**
   * The row last taken up. `useLoad` keeps the data of a key it is asked for again (Back to a view after a Save as): that
   * row is the one read before the page's saves, so it is never applied twice; the page waits for the fresh read instead.
   */
  const applied = useRef<ViewRecord>();
  /** A row the designer cannot read (malformed or missing FetchXML/LayoutXML): shown like a failed load. */
  const [unreadable, setUnreadable] = useState<{ id: string; message: string }>();
  const [dirty, setDirty] = useState(false);
  const [saving, setSaving] = useState(false);
  const savingRef = useRef(false);
  const [failure, setFailure] = useState<string>();
  const [leaving, setLeaving] = useState(false);
  const [related, setRelated] = useState<Record<string, EntityMeta>>({});
  const afterSave = useRef(new Map<string, AfterSave>());
  /**
   * Bumped when the page takes up another view than its own create or Save as moved to (a new view, a loaded row: Back, a
   * link): the cards remount, so nothing they keep (the view modes) carries over from the view shown before.
   */
  const [generation, setGeneration] = useState(0);

  const meta = entity.data;
  const keys = useMemo(() => meta && { primaryId: meta.primaryIdAttribute, primaryName: meta.primaryNameAttribute ?? '' }, [meta]);

  // The design: a new Public view (E2/E3 defaults), or the loaded row (only the row of the id in the address). After a
  // create or a Save as, the address moves to the id the page already holds: nothing is read again.
  useEffect(() => {
    if (!keys) return;
    if (id === NEW) {
      if (heldId === NEW) return;
      setDesign(newView(table, keys, ''));
      setHeldId(NEW);
      target.current = undefined;
      setDirty(false);
      setFailure(undefined);
      setUnreadable(undefined);
      setGeneration((g) => g + 1);
    } else if (id === heldId) {
      return;
    } else if (id === target.current) {
      // The page's own create or Save as: the design, the cards and their state stay.
      setHeldId(id);
    } else if (record.data && record.data !== applied.current && sameId(record.data.savedqueryid, id)) {
      applied.current = record.data;
      setDirty(false);
      setFailure(undefined);
      let read: ViewDesign;
      try {
        read = readView(record.data, keys);
      } catch (e) {
        // Nothing of another view stays, and no Save reaches this one: the page shows why it cannot open it.
        setDesign(undefined);
        setHeldId(undefined);
        target.current = undefined;
        setUnreadable({ id, message: e instanceof Error ? e.message : String(e) });
        return;
      }
      setDesign(read);
      setHeldId(id);
      target.current = id;
      setUnreadable(undefined);
      setGeneration((g) => g + 1);
    } else if (heldId !== undefined) {
      // Another view opened from outside (Back, a link): nothing of the one shown stays, and no Save reaches it, while it loads.
      setDesign(undefined);
      setHeldId(undefined);
      target.current = undefined;
      setDirty(false);
      setFailure(undefined);
      setUnreadable(undefined);
    }
  }, [keys, id, heldId, table, record.data]);

  // The related tables of the design's link-entities, for the headers.
  const linkedTables = design ? [...new Set(design.links.map((l) => l.entity))].join(',') : '';
  useEffect(() => {
    if (!api || !linkedTables) return;
    let current = true;
    for (const name of linkedTables.split(',')) {
      if (related[name]) continue;
      loadEntity(api, name)
        .then((m) => current && setRelated((r) => ({ ...r, [m.logicalName]: m })))
        .catch(() => undefined); // the header falls back to logical names
    }
    return () => {
      current = false;
    };
    // `related` is read, not watched: a table loaded meanwhile is skipped next time.
  }, [api, linkedTables]);

  const onRelated = useCallback((m: EntityMeta) => setRelated((r) => (r[m.logicalName] ? r : { ...r, [m.logicalName]: m })), []);

  /** Counts the edits, so a save clears `dirty` only when nothing changed while it ran. */
  const edits = useRef(0);
  const change = useCallback((next: ViewDesign) => {
    edits.current += 1;
    setDesign(next);
    setDirty(true);
  }, []);

  const hooks = useMemo<DesignerHooks>(
    () => ({
      registerAfterSave: (key, run) => {
        afterSave.current.set(key, run);
        return () => {
          if (afterSave.current.get(key) === run) afterSave.current.delete(key);
        };
      },
      markDirty: () => {
        edits.current += 1;
        setDirty(true);
      },
    }),
    [],
  );

  /**
   * Stores `d` (a create when `existing` is undefined) and hands the id to `onStored` before publishing, so a failed publish
   * still leaves the page on the stored view (the next Save updates it). Then publishes and runs the after-save steps. True
   * when everything but the steps succeeded; a failure is shown verbatim.
   */
  async function write(d: ViewDesign, existing: string | undefined, onStored: (id: string) => void): Promise<boolean> {
    if (!api || savingRef.current) return false;
    savingRef.current = true;
    setSaving(true);
    setFailure(undefined);
    try {
      const saved = await storeView(api, d, existing);
      onStored(saved);
      await publishTable(api, d.table);
      for (const step of [...afterSave.current.values()]) {
        try {
          await step(saved);
        } catch (e) {
          setFailure(e instanceof Error ? e.message : String(e));
        }
      }
      return true;
    } catch (e) {
      setFailure(e instanceof Error ? e.message : String(e));
      return false;
    } finally {
      savingRef.current = false;
      setSaving(false);
    }
  }

  async function save() {
    if (!design) return;
    const existing = target.current;
    const before = edits.current;
    const ok = await write(design, existing, (id) => {
      if (existing) return;
      target.current = id;
      navigate(`${tablePath}/views/${id}`, { replace: true });
    });
    if (ok && edits.current === before) setDirty(false);
  }

  async function saveAs() {
    if (!design) return;
    const name = copyName(design.name);
    const copy: ViewDesign = { ...design, name, isDefault: false, isManaged: false };
    const before = edits.current;
    const ok = await write(copy, undefined, (id) => {
      target.current = id;
      setDesign((d) => d && { ...d, name, isDefault: false, isManaged: false });
      navigate(`${tablePath}/views/${id}`);
    });
    if (ok && edits.current === before) setDirty(false);
  }

  const back = () => (dirty ? setLeaving(true) : navigate(tablePath));

  const header = (c: DesignColumn) => columnHeader(design!, c, meta, related);
  const nameMissing = !design || design.name.trim() === '';
  const loadError = entity.error ?? rels.error ?? record.error ?? (unreadable && unreadable.id === id ? unreadable.message : undefined);
  const card: DesignCardProps | undefined = design && meta ? { design, attributes: meta.attributes, onChange: change } : undefined;

  return (
    <section>
      <PageHeader
        title={design?.name || (id === NEW ? t('views.designer.newTitle') : '')}
        breadcrumb={[
          { label: t('nav.tables'), to: '/manage/tables' },
          { label: meta?.displayName ?? table, to: tablePath },
        ]}
        actions={
          <>
            <Button appearance="primary" icon={<SaveIcon />} disabled={!api || !design || saving || nameMissing} onClick={() => void save()}>
              {t('views.designer.save')}
            </Button>
            {design?.queryType === 0 && (
              <Button disabled={!api || saving || nameMissing || !target.current} onClick={() => void saveAs()}>
                {t('views.designer.saveAs')}
              </Button>
            )}
            <Button onClick={back}>{t('views.designer.back')}</Button>
          </>
        }
      />
      {loadError && (
        <MessageBar intent="error">
          <MessageBarBody>{t('views.designer.loadFailed', { detail: loadError })}</MessageBarBody>
        </MessageBar>
      )}
      {failure && (
        <MessageBar intent="error">
          <MessageBarBody>{failure}</MessageBarBody>
        </MessageBar>
      )}
      {design?.isManaged && (
        <MessageBar intent="info">
          <MessageBarBody>{t('views.designer.managed')}</MessageBarBody>
        </MessageBar>
      )}
      {!design && !loadError && <Spinner label={t('common.loading')} />}
      {design && meta && card && (
        <DesignerContext.Provider value={hooks}>
          <Fragment key={generation}>
            <Card id="view-properties" title={t('views.designer.properties')}>
              <Field label={t('views.designer.name')} required validationMessage={design.name.trim() === '' ? t('views.designer.nameRequired') : undefined}>
                <Input value={design.name} maxLength={MAX_NAME} onChange={(_, d) => change({ ...design, name: d.value })} />
              </Field>
              <Field label={t('views.designer.description')}>
                <Textarea value={design.description} onChange={(_, d) => change({ ...design, description: d.value })} />
              </Field>
            </Card>
            <Card id="view-columns" title={t('views.columns.title')}>
              <ColumnEditor
                design={design}
                entity={meta}
                lookups={rels.data ? lookupsOf(rels.data, table) : []}
                related={related}
                onRelated={onRelated}
                onChange={change}
              />
            </Card>
            <Card id="view-sort" title={t('views.sort.title')}>
              <SortEditor {...card} />
            </Card>
            <Card id="view-filter" title={t('views.filter.title')}>
              <FilterEditor {...card} />
            </Card>
            {design.queryType === 4 && (
              <Card id="view-find" title={t('views.find.title')}>
                <FindColumnsEditor {...card} />
              </Card>
            )}
            <Card id="view-modes" title={t('views.modes.title')}>
              <ViewModesEditor
                attributes={meta.attributes}
                viewId={heldId && heldId !== NEW ? heldId : undefined}
                columns={design.columns.map((c) => ({ name: c.name, header: header(c) }))}
              />
            </Card>
            <Card id="view-preview" title={t('views.preview.title')}>
              <ViewPreview design={design} entitySet={meta.entitySetName} header={header} />
            </Card>
          </Fragment>
        </DesignerContext.Provider>
      )}
      <ConfirmDialog
        open={leaving}
        title={t('views.designer.leave.title')}
        body={t('views.designer.leave.body')}
        confirmLabel={t('views.designer.leave.confirm')}
        cancelLabel={t('common.cancel')}
        onConfirm={() => {
          setLeaving(false);
          navigate(tablePath);
        }}
        onCancel={() => setLeaving(false)}
      />
    </section>
  );
}
