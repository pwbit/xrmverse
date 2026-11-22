import {
  Button, Field, Input, makeStyles, MessageBar, MessageBarActions, MessageBarBody, Spinner, Textarea, tokens,
} from '@fluentui/react-components';
import { Fragment, useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import { useNavigate, useParams, useSearchParams } from 'react-router-dom';
import { useAuth } from '../../shell/auth/AuthProvider';
import { t } from '../../shell/i18n/t';
import { SaveIcon } from '../../shell/icons';
import { PageHeader } from '../../shell/layout/PageHeader';
import { useSurfaceStyles } from '../../shell/layout/surface';
import { ConfirmDialog } from '../common/ConfirmDialog';
import { DesignerContext, type AfterSave, type DesignerHooks } from '../common/designerHooks';
import { loadEntity } from '../tables/tableApi';
import { useLoad } from '../tables/useLoad';
import { publishTable } from '../views/viewApi';
import { FormCanvas } from './FormCanvas';
import { listCustomControls } from './formControlsApi';
import { loadForm, storeForm, type FormRecord } from './formApi';
import { allCells } from './formCells';
import { FORM_TYPE, newForm, readForm, uiLcid, type FormDesign, type NewFormType } from './formDesign';
import { cellLabel, type FormDesignCardProps, type Selection } from './formDesignerSlots';
import { canBindPcf, pcfMissing } from './formPcf';
import { FormPropertiesPanel } from './FormPropertiesPanel';
import { FormLookEditor } from './FormLookEditor';
import { EventsEditor } from './EventsEditor';
import { HeaderEditor } from './HeaderEditor';

/** `systemform.name` takes up to 100 characters. */
const MAX_NAME = 100;
const NEW = 'new';

const NEW_TITLE = {
  [FORM_TYPE.main]: 'forms.designer.newTitle',
  [FORM_TYPE.quickView]: 'forms.designer.newTitle.quickView',
  [FORM_TYPE.quickCreate]: 'forms.designer.newTitle.quickCreate',
} as const;

/** The type of a new form, from the address's `type` (N2): 6 and 7 are Quick view and Quick create, anything else is Main. */
export function newFormType(search: URLSearchParams): NewFormType {
  const type = search.get('type');
  return type === String(FORM_TYPE.quickView) ? FORM_TYPE.quickView : type === String(FORM_TYPE.quickCreate) ? FORM_TYPE.quickCreate : FORM_TYPE.main;
}

/** The id of the name's validation message, which Save and Save as point to (A2). */
const NAME_MESSAGE_ID = 'form-name-message';
/** The form types the designer edits (ruling F2): Main, Quick view and Quick create. */
const DESIGNED = new Set<number>([FORM_TYPE.main, FORM_TYPE.quickView, FORM_TYPE.quickCreate]);
/** Quick view and Quick create: designed without the header and Modern look cards (ruling I3). */
const QUICK = new Set<number>([FORM_TYPE.quickView, FORM_TYPE.quickCreate]);

/** "{name} (copy)", the base name cut so the result fits {@link MAX_NAME}. */
function copyName(name: string): string {
  const room = MAX_NAME - t('forms.designer.copyName', { name: '' }).length;
  return t('forms.designer.copyName', { name: name.slice(0, Math.max(0, room)) });
}

const sameId = (a: string, b: string) => a.replace(/[{}]/g, '').toLowerCase() === b.replace(/[{}]/g, '').toLowerCase();

const useStyles = makeStyles({
  body: { display: 'flex', flexWrap: 'wrap', alignItems: 'flex-start', columnGap: tokens.spacingHorizontalL },
  structure: { flex: '3 1 480px', minWidth: 0 },
  properties: { flex: '1 1 240px', minWidth: 0 },
});

/** A titled card of the designer page; its title names the region. */
function Card({ id, title, className, children }: { id: string; title: string; className?: string; children: ReactNode }) {
  const surface = useSurfaceStyles();
  return (
    <section className={className ? `${surface.section} ${className}` : surface.section} aria-labelledby={id}>
      <h3 id={id} className={surface.sectionTitle}>
        {title}
      </h3>
      {children}
    </section>
  );
}

/**
 * The form designer (`/manage/tables/:table/forms/:id`, `:id` = `new`): the properties, header and structure of one
 * `systemform` (rulings F1–F10). The page holds the {@link FormDesign}; every card gets it with `onChange`
 * ({@link FormDesignCardProps}) and returns the next one. Save writes the form (create, then the page moves to the new id;
 * update afterwards) and publishes the table, then runs the cards' after-save steps ({@link DesignerContext}) with the
 * form's id. Forms of other types than Main, Quick view and Quick create open read-only.
 */
export function FormDesigner() {
  const { table = '', id = NEW } = useParams();
  const newType = newFormType(useSearchParams()[0]);
  /** The held key of a new form: its type is part of it, so moving from one new type to another builds a fresh design (N5). */
  const newKey = `${NEW}:${newType}`;
  const { api } = useAuth();
  const navigate = useNavigate();
  const styles = useStyles();
  const tablePath = `/manage/tables/${encodeURIComponent(table)}`;

  const entity = useLoad(table, (a) => loadEntity(a, table));
  const [design, setDesign] = useState<FormDesign>();
  /** Whether the form shown comes from a managed solution (F10's info bar). */
  const [managed, setManaged] = useState(false);
  /** The id whose design the page holds: `new:<type>` before the first save, the form's id after loading it. */
  const [heldId, setHeldId] = useState<string>();
  /**
   * The form the next save updates (undefined: the next save creates). Set as soon as a create returns, before the address
   * changes (the router moves in a transition), so a second Save never creates a second form.
   */
  const target = useRef<string>();
  const record = useLoad(id !== NEW && id !== heldId && id !== target.current ? id : undefined, (a) => loadForm(a, id));
  /**
   * The row last taken up. `useLoad` keeps the data of a key it is asked for again (Back to a form after a Save as): that
   * row is the one read before the page's saves, so it is never applied twice; the page waits for the fresh read instead.
   */
  const applied = useRef<FormRecord>();
  /** A row the designer cannot read (missing or malformed FormXML): shown like a failed load. */
  const [unreadable, setUnreadable] = useState<{ id: string; message: string }>();
  const [dirty, setDirty] = useState(false);
  const [saving, setSaving] = useState(false);
  const savingRef = useRef(false);
  const [failure, setFailure] = useState<string>();
  const [leaving, setLeaving] = useState(false);
  const [selection, setSelection] = useState<Selection>();
  const afterSave = useRef(new Map<string, AfterSave>());
  /**
   * Bumped when the page takes up another form than its own create or Save as moved to (a new form, a loaded row: Back, a
   * link): the cards remount, so nothing they keep carries over from the form shown before.
   */
  const [generation, setGeneration] = useState(0);

  /**
   * The installed custom controls (G5), read once the page needs them: when the form has a custom control, or a field or
   * subgrid is selected. They give the panel its choices and Save its check of the required properties.
   */
  const [wantControls, setWantControls] = useState(false);
  const customControls = useLoad(wantControls ? 'customcontrols' : undefined, listCustomControls);
  if (!wantControls && design) {
    const cells = allCells(design);
    const selected = selection?.kind === 'cell' ? cells.find((c) => c.key === selection.key) : undefined;
    if ((selected && canBindPcf(selected)) || cells.some((c) => c.pcf)) setWantControls(true);
  }

  const meta = entity.data;
  const primary = useMemo(
    () => (meta ? meta.attributes.find((a) => a.logicalName === meta.primaryNameAttribute) ?? null : undefined),
    [meta],
  );

  // The design: a new Main form (F2), or the loaded row (only the row of the id in the address). After a create or a
  // Save as, the address moves to the id the page already holds: nothing is read again.
  useEffect(() => {
    if (primary === undefined) return;
    if (id === NEW) {
      if (heldId === newKey) return;
      target.current = undefined;
      setDirty(false);
      setFailure(undefined);
      setManaged(false);
      setSelection(undefined);
      setGeneration((g) => g + 1);
      if (!primary) {
        setDesign(undefined);
        setHeldId(undefined);
        setUnreadable({ id: NEW, message: t('forms.designer.noPrimaryName') });
        return;
      }
      setDesign(newForm(table, primary, uiLcid(), '', newType));
      setHeldId(newKey);
      setUnreadable(undefined);
    } else if (id === heldId) {
      return;
    } else if (id === target.current) {
      // The page's own create or Save as: the design, the cards and their state stay.
      setHeldId(id);
    } else if (record.data && record.data !== applied.current && sameId(record.data.systemformid, id)) {
      applied.current = record.data;
      setDirty(false);
      setFailure(undefined);
      setSelection(undefined);
      let read: FormDesign;
      try {
        read = readForm(record.data, uiLcid());
      } catch (e) {
        // Nothing of another form stays, and no Save reaches this one: the page shows why it cannot open it.
        setDesign(undefined);
        setHeldId(undefined);
        target.current = undefined;
        setUnreadable({ id, message: e instanceof Error ? e.message : String(e) });
        return;
      }
      setDesign(read);
      setManaged(record.data.ismanaged === true);
      setHeldId(id);
      target.current = id;
      setUnreadable(undefined);
      setGeneration((g) => g + 1);
    } else if (heldId !== undefined) {
      // Another form opened from outside (Back, a link): nothing of the one shown stays, and no Save reaches it, while it loads.
      setDesign(undefined);
      setHeldId(undefined);
      target.current = undefined;
      setDirty(false);
      setFailure(undefined);
      setUnreadable(undefined);
      setSelection(undefined);
    }
  }, [primary, id, heldId, table, record.data, newKey, newType]);

  /** Counts the edits, so a save clears `dirty` only when nothing changed while it ran. */
  const edits = useRef(0);
  const change = useCallback((next: FormDesign) => {
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
   * still leaves the page on the stored form (the next Save updates it). Then publishes and runs the after-save steps (not
   * run when the publish failed: the next Save runs them). True when everything, the steps too, succeeded; a failure is
   * shown verbatim, and a failed step leaves the page changed.
   */
  async function write(d: FormDesign, existing: string | undefined, onStored: (id: string) => void): Promise<boolean> {
    if (!api || savingRef.current) return false;
    savingRef.current = true;
    setSaving(true);
    setFailure(undefined);
    try {
      const saved = await storeForm(api, d, existing);
      onStored(saved);
      await publishTable(api, d.table);
      let stepsOk = true;
      for (const step of [...afterSave.current.values()]) {
        try {
          await step(saved);
        } catch (e) {
          stepsOk = false;
          setFailure(e instanceof Error ? e.message : String(e));
        }
      }
      return stepsOk;
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
    const ok = await write(design, existing, (saved) => {
      if (existing) return;
      target.current = saved;
      navigate(`${tablePath}/forms/${saved}`, { replace: true });
    });
    if (ok && edits.current === before) setDirty(false);
  }

  async function saveAs() {
    if (!design) return;
    const name = copyName(design.name);
    const copy: FormDesign = { ...design, name, isDefault: false, active: true };
    const before = edits.current;
    const ok = await write(copy, undefined, (saved) => {
      target.current = saved;
      setDesign((d) => d && { ...d, name, isDefault: false, active: true });
      setManaged(false);
      navigate(`${tablePath}/forms/${saved}`);
    });
    if (ok && edits.current === before) setDirty(false);
  }

  const back = () => (dirty ? setLeaving(true) : navigate(tablePath));

  const editable = design !== undefined && DESIGNED.has(design.type);
  const readOnly = !editable;
  const nameMissing = !design || design.name.trim() === '';
  /** Custom controls whose required properties are not set (G5): Save waits for them. */
  const missing = design && meta && editable && customControls.data ? pcfMissing(design, customControls.data, meta.attributes) : [];
  /** Whether the form binds a custom control: then Save waits for the list that checks them (H5). */
  const bound = design !== undefined && allCells(design).some((c) => c.pcf);
  /** The required check cannot run yet (H5): the list is on its way, or failed (`error`). */
  const checking = editable && bound && (customControls.loading || customControls.error !== undefined || !customControls.data);
  // The check needs the table's columns too: nothing is saved while they are missing.
  const blocked = nameMissing || missing.length > 0 || checking || (editable && !meta);
  /** The name's validation message, shown on an editable form with a blank name. */
  const nameMessage = !readOnly && nameMissing ? t('forms.designer.nameRequired') : undefined;
  /** The messages that block Save and Save as, which the buttons point to (A2). */
  const blockedBy = [
    nameMessage !== undefined && meta ? NAME_MESSAGE_ID : undefined,
    design && meta && missing.length > 0 ? 'form-save-pcf' : undefined,
    checking ? 'form-save-check' : undefined,
  ].filter(Boolean).join(' ') || undefined;
  const loadError = entity.error ?? record.error ?? (unreadable && unreadable.id === id ? unreadable.message : undefined);
  const card: FormDesignCardProps | undefined = design && meta ? { design, attributes: meta.attributes, onChange: change } : undefined;

  return (
    <section>
      <PageHeader
        title={design?.name || (id === NEW ? t(NEW_TITLE[newType]) : '')}
        breadcrumb={[
          { label: t('nav.tables'), to: '/manage/tables' },
          { label: meta?.displayName ?? table, to: tablePath },
        ]}
        actions={
          <>
            {(editable || !design) && (
              <Button appearance="primary" icon={<SaveIcon />} disabled={!api || !design || saving || blocked} aria-describedby={blockedBy} onClick={() => void save()}>
                {t('forms.designer.save')}
              </Button>
            )}
            {/* A stored, editable form: the copy keeps its type (formApi.storeForm posts the design's type). */}
            {editable && target.current !== undefined && (
              <Button disabled={!api || saving || blocked} aria-describedby={blockedBy} onClick={() => void saveAs()}>
                {t('forms.designer.saveAs')}
              </Button>
            )}
            <Button onClick={back}>{t('forms.designer.back')}</Button>
          </>
        }
      />
      {loadError && (
        <MessageBar intent="error">
          <MessageBarBody>{t('forms.designer.loadFailed', { detail: loadError })}</MessageBarBody>
        </MessageBar>
      )}
      {failure && (
        <MessageBar intent="error">
          <MessageBarBody>{failure}</MessageBarBody>
        </MessageBar>
      )}
      {design && managed && (
        <MessageBar intent="info">
          <MessageBarBody>{t('forms.designer.managed')}</MessageBarBody>
        </MessageBar>
      )}
      {checking && (customControls.error === undefined ? (
        <MessageBar id="form-save-check" intent="info">
          <MessageBarBody>{t('forms.designer.checkPending')}</MessageBarBody>
        </MessageBar>
      ) : (
        <MessageBar id="form-save-check" intent="error">
          <MessageBarBody>{t('forms.designer.checkFailed', { detail: customControls.error })}</MessageBarBody>
          <MessageBarActions>
            <Button onClick={() => customControls.reload()}>{t('forms.designer.retry')}</Button>
          </MessageBarActions>
        </MessageBar>
      ))}
      {design && meta && missing.length > 0 && (
        <MessageBar id="form-save-pcf" intent="warning">
          <MessageBarBody>
            {t('forms.pcf.saveBlocked')}
            <ul>
              {missing.map((m) => (
                <li key={m.cell.key}>
                  {t('forms.pcf.saveBlocked.item', {
                    cell: cellLabel(m.cell, design.lcid, meta.attributes),
                    control: m.cell.pcf!.name,
                    properties: m.properties.join(', '),
                  })}
                </li>
              ))}
            </ul>
          </MessageBarBody>
        </MessageBar>
      )}
      {design && readOnly && (
        <MessageBar intent="warning">
          <MessageBarBody>{t('forms.designer.notDesigned')}</MessageBarBody>
        </MessageBar>
      )}
      {!design && !loadError && <Spinner label={t('common.loading')} />}
      {design && card && (
        <DesignerContext.Provider value={hooks}>
          <Fragment key={generation}>
            <Card id="form-properties" title={t('forms.designer.properties')}>
              <Field label={t('forms.designer.name')} required validationMessage={nameMessage && { id: NAME_MESSAGE_ID, children: nameMessage }}>
                <Input value={design.name} maxLength={MAX_NAME} disabled={readOnly} onChange={(_, d) => change({ ...design, name: d.value })} />
              </Field>
              <Field label={t('forms.designer.description')}>
                <Textarea value={design.description} disabled={readOnly} onChange={(_, d) => change({ ...design, description: d.value })} />
              </Field>
            </Card>
            {/* Quick view and Quick create forms have no header at run time, and the Modern look applies to Main forms. */}
            {!QUICK.has(design.type) && (
              <Card id="form-header" title={t('forms.header.title')}>
                <HeaderEditor {...card} readOnly={readOnly} selection={selection} onSelect={setSelection} />
              </Card>
            )}
            {editable && !QUICK.has(design.type) && (
              <Card id="form-look" title={t('forms.look.title')}>
                <FormLookEditor design={design} formId={heldId?.startsWith(`${NEW}:`) ? undefined : heldId} />
              </Card>
            )}
            {/* Dataverse offers no form libraries or events on a Quick view form. */}
            {editable && design.type !== FORM_TYPE.quickView && (
              <Card id="form-events" title={t('forms.events.title')}>
                <EventsEditor {...card} readOnly={readOnly} />
              </Card>
            )}
            <div className={styles.body}>
              <Card id="form-structure" title={t('forms.canvas.title')} className={styles.structure}>
                <FormCanvas {...card} readOnly={readOnly} selection={selection} onSelect={setSelection} />
              </Card>
              <Card id="form-selection" title={t('forms.props.title')} className={styles.properties}>
                <FormPropertiesPanel {...card} readOnly={readOnly} selection={selection} customControls={customControls} />
              </Card>
            </div>
          </Fragment>
        </DesignerContext.Provider>
      )}
      <ConfirmDialog
        open={leaving}
        title={t('forms.designer.leave.title')}
        body={t('forms.designer.leave.body')}
        confirmLabel={t('forms.designer.leave.confirm')}
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
