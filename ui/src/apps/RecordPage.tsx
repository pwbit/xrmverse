import { Body1, makeStyles, MessageBar, MessageBarBody, Spinner, tokens } from '@fluentui/react-components';
import { useCallback, useContext, useEffect, useMemo, useRef, useState } from 'react';
import { Link, useLocation, useNavigate } from 'react-router-dom';
import { ConfirmDialog } from '../admin/common/ConfirmDialog';
import { SaveMode } from '../client-api/constants';
import { controlCells } from '../client-api/control';
import type { AsyncHandlers, FormContext, SaveOutcome } from '../client-api/formContext';
import { formParameterValues } from '../client-api/navigation';
import { XrmHostContext } from '../client-api/xrm';
import { createUiOverlay, useUiOverlay } from '../client-api/uiOverlay';
import type { AppModel, TimelineOptions } from '../model/app';
import type { FormModel } from '../model/form';
import { normaliseId } from '../parsers/formXml';
import { useRenderer } from '../renderers/RendererContext';
import type { XrmApi } from '../shell/api/client';
import { XrmError } from '../shell/api/errors';
import { useAuth } from '../shell/auth/AuthProvider';
import { collect, orFilter, SYSTEM_ADMINISTRATOR_TEMPLATE, userRoleIds } from '../shell/auth/roles';
import { t } from '../shell/i18n/t';
import { mainAspxUrl } from './mainAspx';
import { readLastForm, writeLastForm } from './record/lastForm';
import { loadForms, pickForm } from './record/loadForms';
import { formColumns, loadRecord, type RecordContext } from './record/loadRecord';
import { loadCurrency, type Currency } from './record/currency';
import { useRecordForm } from './record/useRecordForm';
import { useFormScripts, type FormScripts } from './record/useFormScripts';
import { useRibbonCommands } from './record/useRibbonCommands';
import type { FormNotification } from '../renderers/classic/form/FormNotifications';
import { associateNew } from './record/subgridCommands';
import { initializeFromPath, mappedValues, nameLookups, type CreateFrom } from './record/initializeFrom';
import { useAutoSave } from './record/useAutoSave';
import { useUnsavedGuard } from './record/useUnsavedGuard';
import { useUserSettings } from './userSettings';
import { AttachmentsSection } from './email/AttachmentsSection';
import { emailSendCommands, sendEmail, SEND_NOTIFICATION } from './email/emailCommands';
import { EmailStatusBanner } from './email/EmailStatusBanner';

const errorText = (e: unknown) => (e instanceof Error ? e.message : String(e));

/** The user's roles and whether one of them is System Administrator, for the form security of {@link loadForms}. */
async function loadFormUser(api: XrmApi, userId: string): Promise<{ roleIds: string[]; isSystemAdministrator: boolean }> {
  const roleIds = await userRoleIds(api, userId);
  const roles = roleIds.length
    ? await collect<{ roleid: string; roletemplateid?: string | null }>(api, `roles?$select=roleid,roletemplateid&$filter=${orFilter('roleid', roleIds)}`)
    : [];
  return { roleIds, isSystemAdministrator: roles.some((r) => r.roletemplateid?.toLowerCase() === SYSTEM_ADMINISTRATOR_TEMPLATE) };
}

type FormsState =
  | { status: 'loading' }
  | { status: 'error'; error: string }
  /** `lastFormId`: the user's last form of the table (#119), normalised. */
  | { status: 'ready'; forms: FormModel[]; lastFormId?: string };

/** Writing the last form used is best effort (ruling R2): a failure is logged and the form opens anyway. */
const warnLastForm = (e: unknown) => console.warn('xrmverse: the last form used could not be read or stored.', e);
type RecordState = { key: string; ctx?: RecordContext; notFound?: boolean; error?: string };

const useStyles = makeStyles({
  link: { color: tokens.colorBrandForegroundLink },
});

export interface RecordPageProps {
  app: AppModel;
  etn: string;
  /** The record to open; without it the form opens in create mode. */
  id?: string;
  /** The `formid` of the URL. */
  formId?: string;
  /** The URL's `extraqs`: the values a create form starts with (`Xrm.Navigation.openForm` form parameters). */
  parameters?: Record<string, string>;
  /** Called after each save that succeeded, with the record's id (a record dialog reports the record it saved, #133). */
  onSaved?: (id: string) => void;
}

/**
 * `pagetype=entityrecord`: the forms of the table are loaded and one is chosen (the URL's `formid` when listed, else the
 * user's last form of the table when still listed, else the first), then the record with only the columns that form shows.
 * Create mode without `id`.
 */
export function RecordPage({ app, etn, id, formId, parameters, onSaved }: RecordPageProps) {
  const styles = useStyles();
  const { api } = useAuth();
  const { userId, lcid } = useUserSettings();
  const navigate = useNavigate();
  const [formsState, setFormsState] = useState<FormsState>({ status: 'loading' });

  useEffect(() => {
    if (!api) return;
    let cancelled = false;
    setFormsState({ status: 'loading' });
    const lastForm = readLastForm(api, userId, etn).catch((e: unknown) => {
      warnLastForm(e);
      return undefined;
    });
    Promise.all([loadFormUser(api, userId).then((user) => loadForms(api, app, etn, user)), lastForm]).then(
      ([forms, lastFormId]) => !cancelled && setFormsState({ status: 'ready', forms, lastFormId }),
      (e: unknown) => !cancelled && setFormsState({ status: 'error', error: errorText(e) }),
    );
    return () => {
      cancelled = true;
    };
  }, [api, app, etn, userId]);

  // The parsed forms are kept by identity: the form state resets when its form changes, so a re-render must hand it the same model.
  const forms = formsState.status === 'ready' ? formsState.forms : undefined;
  const lastFormId = formsState.status === 'ready' ? formsState.lastFormId : undefined;
  // The last form is read once per table: a form remembered later in this visit does not switch the form shown.
  const picked = useMemo(
    () => (forms ? pickForm(forms, formId, lastFormId) : undefined),
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [forms, formId],
  );
  // A form asked for by the URL (the form selector puts it there) becomes the user's last form of the table (#119).
  const chosen = formId && picked?.form && !picked.notice ? normaliseId(picked.form.id) : undefined;
  useEffect(() => {
    if (!api || !chosen || chosen === lastFormId) return;
    setFormsState((prev) => (prev.status === 'ready' ? { ...prev, lastFormId: chosen } : prev));
    writeLastForm(api, userId, etn, chosen).catch(warnLastForm);
    // lastFormId is what this effect updates.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [api, userId, etn, chosen]);
  const form = picked?.form;
  const key = form ? `${form.id}|${id ?? ''}` : '';
  const [loaded, setLoaded] = useState<RecordState>({ key: '' });
  // Refresh bumps it to reload the subgrids.
  const [refreshKey, setRefreshKey] = useState(0);
  // Only a Refresh bumps this; Assign reloads the record, and OnLoad follows the new record instead.
  const [loadKey, setLoadKey] = useState(0);
  // Assign reads the record again (its rights and owner change with it).
  const [reloadCount, setReloadCount] = useState(0);
  const location = useLocation();
  // A create form that saved stays mounted as the form of the new record (Learn, form OnLoad: it runs again after the first
  // save), so its scripts, formContext and UI overlay carry on: the new record's key maps to the create form's key. It only
  // bridges the stay on that record: `handed` once its read reached the form, and dropped when the page leaves the record or
  // the form, so a later return opens the record afresh.
  const created = useRef<{ formId: string; id: string; key: string; ctx: RecordContext; handed?: boolean }>();

  useEffect(() => {
    if (!api || !form) return;
    let cancelled = false;
    // ctx.record holds only the columns of the form it was read for: a different form reads the record again.
    loadRecord(api, etn, id, formColumns(form)).then(
      (ctx) => !cancelled && setLoaded({ key, ctx }),
      (e: unknown) => {
        if (cancelled) return;
        setLoaded(e instanceof XrmError && (e.status === 404 || e.status === 403) ? { key, notFound: true } : { key, error: errorText(e) });
      },
    );
    return () => {
      cancelled = true;
    };
    // `key` stands for the form and the record.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [api, etn, form, id, reloadCount]);

  // Once the created record's read is shown, the create form's context is no longer a stand-in.
  useEffect(() => {
    const c = created.current;
    if (c && loaded.ctx && loaded.key === `${c.formId}|${c.id}`) created.current = { ...c, handed: true };
  }, [loaded]);

  const notice = (text: string, intent: 'error' | 'warning' = 'error') => (
    <MessageBar intent={intent}>
      <MessageBarBody>{text}</MessageBarBody>
    </MessageBar>
  );
  if (formsState.status === 'error') return notice(t('record.loadFailed', { detail: formsState.error }));
  if (formsState.status === 'loading') return <Spinner label={t('record.loading')} />;
  if (!picked?.form) return notice(t('record.noForm'), 'warning');
  // Until the created record is read, the create form keeps its context (the router's URL change renders before the read).
  if (created.current && (created.current.id !== id || created.current.formId !== picked.form.id)) created.current = undefined;
  const fromCreate = created.current;
  const current = loaded.key === key ? loaded : fromCreate && !fromCreate.handed ? { key, ctx: fromCreate.ctx } : undefined;
  if (current?.notFound) {
    return (
      <>
        {notice(t('record.notFound'), 'warning')}
        <Body1 block>
          <Link className={styles.link} to={mainAspxUrl({ appId: app.id, pageType: 'entitylist', etn })}>
            {t('record.backToList')}
          </Link>
        </Body1>
      </>
    );
  }
  if (current?.error) return notice(t('record.loadFailed', { detail: current.error }));
  if (!current?.ctx) return <Spinner label={t('record.loading')} />;
  const shownForm = picked.form;
  // New from the create form (same address, a new location) opens an empty form again.
  const mountKey = !id ? `${key}|${location.key}` : (fromCreate?.key ?? key);
  return (
    <LoadedRecord
      key={mountKey}
      ctx={current.ctx}
      form={picked.form}
      forms={formsState.forms}
      notice={picked.notice === 'formNotAvailable' ? 'formNotAvailable' : undefined}
      lcid={lcid}
      appId={app.id}
      asyncHandlers={app.asyncHandlers}
      timeline={app.timeline}
      emailSendFrom={app.emailSendFrom}
      refreshKey={refreshKey}
      loadKey={loadKey}
      formId={formId}
      parameters={id ? undefined : parameters}
      onRefreshed={() => {
        setRefreshKey((k) => k + 1);
        setLoadKey((k) => k + 1);
      }}
      onReload={() => {
        setReloadCount((c) => c + 1);
        setRefreshKey((k) => k + 1);
      }}
      onSelectForm={(next) =>
        navigate(mainAspxUrl({ appId: app.id, pageType: 'entityrecord', etn, id, formId: next.id }), { state: location.state })
      }
      onSaved={onSaved}
      onCreated={(newId) => {
        // The create form keeps showing until the new record is read; then it receives it.
        created.current = { formId: shownForm.id, id: newId, key: mountKey, ctx: current.ctx! };
      }}
    />
  );
}

interface LoadedProps {
  ctx: RecordContext;
  form: FormModel;
  forms: FormModel[];
  notice?: 'formNotAvailable';
  lcid: number;
  appId: string;
  /** The app settings that make OnLoad and OnSave wait for their handlers' promises. */
  asyncHandlers?: AsyncHandlers;
  /** The app's timeline options; the form's timeline control reads them. */
  timeline?: TimelineOptions;
  /** The email sender policy (plan 4.1b): who an email's From offers. */
  emailSendFrom?: AppModel['emailSendFrom'];
  refreshKey: number;
  /** Bumped when a Refresh finished; the form's OnLoad runs again. */
  loadKey: number;
  /** The URL's `formid`, kept when the page replaces its own URL. */
  formId?: string;
  /** The create form's starting values (`extraqs`). */
  parameters?: Record<string, string>;
  onRefreshed: () => void;
  /**
   * The record was changed on the server in a way that changes what the user may do (Assign, Activate, Deactivate): read it and
   * its rights again.
   */
  onReload: () => void;
  onSelectForm: (form: FormModel) => void;
  /** The create form created the record `id`: the page keeps this form mounted for it. */
  onCreated: (id: string) => void;
  onSaved?: (id: string) => void;
}

/** The form of a loaded record, with its state. */
function LoadedRecord({ ctx, form, forms, notice, lcid, appId, asyncHandlers, timeline, emailSendFrom, refreshKey, loadKey, formId, parameters, onRefreshed, onReload, onSelectForm, onCreated, onSaved }: LoadedProps) {
  const { CommandBar, FormView } = useRenderer();
  const { api } = useAuth();
  const xrmHost = useContext(XrmHostContext);
  const { userId, timeZone } = useUserSettings();
  // What the form's scripts changed (visibility, labels, required levels, notifications): the form renders from it first.
  const overlay = useMemo(createUiOverlay, []);
  useUiOverlay(overlay);
  const scriptsRef = useRef<FormScripts>();
  const formContextRef = useRef<FormContext>();
  // The same formContext as state: the custom commands evaluate their rules once it exists (UI-1e).
  const [formContext, setFormContext] = useState<FormContext>();
  const ribbonRefresh = useRef<() => void>();
  const state = useRecordForm(ctx, form, { lcid, overlay, onUserChange: (attribute) => scriptsRef.current?.onChange(attribute) });
  const cells = useMemo(() => controlCells(form), [form]);
  const reloadControl = (name: string) => {
    const cell = cells.get(name);
    const kind = cell?.control?.kind;
    if (cell && (kind === 'subgrid' || kind === 'timeline')) overlay.setElement(cell, { reload: (overlay.element(cell).reload ?? 0) + 1 });
  };
  const creating = !ctx.record;
  const navigate = useNavigate();
  const location = useLocation();
  const { blocker, navigateAnyway } = useUnsavedGuard(state.dirty);
  const etn = ctx.entity.logicalName;
  // The list the user came from (a list row or New passes it), else the table's default list.
  const returnTo = (location.state as { returnTo?: string } | null)?.returnTo;
  // A create form opened by a subgrid's New (#118): it goes back to the parent after its first save, relating a many-to-many row;
  // for a one-to-many it starts with the values the parent's maps give (#143, createFrom).
  const fromSubgrid = creating
    ? (location.state as { fromSubgrid?: { associate?: { nav: string; target: string }; createFrom?: CreateFrom } } | null)?.fromSubgrid
    : undefined;
  const createFrom = fromSubgrid?.createFrom;
  const [associateError, setAssociateError] = useState<string>();
  const toList = () => navigateAnyway(() => navigate(returnTo ?? mainAspxUrl({ appId, pageType: 'entitylist', etn })));
  // A save started while another one runs (a script's data.save() inside OnSave) does not run.
  const saving = useRef(false);
  const save = async (andClose: boolean, mode: number = andClose ? SaveMode.SaveAndClose : SaveMode.Save): Promise<SaveOutcome> => {
    if (saving.current) return 'failed';
    saving.current = true;
    try {
      // OnSave runs first (awaiting its handlers' promises when the app says so); a handler that calls preventDefault() stops the save.
      if (await scriptsRef.current?.onSave(mode)) return 'prevented';
      const saved = await state.save();
      scriptsRef.current?.onPostSave(!!saved);
      if (!saved) return 'failed';
      onSaved?.(saved.id);
      if (fromSubgrid) {
        if (fromSubgrid.associate && api && ctx.entity.entitySetName) {
          try {
            await associateNew(api, ctx.entity.entitySetName, saved.id, fromSubgrid.associate);
          } catch (e) {
            // The row exists: its form opens with the reason, as after any create.
            setAssociateError(t('record.subgrid.associateFailed', { target: fromSubgrid.associate.target, detail: e instanceof Error ? e.message : String(e) }));
            onCreated(saved.id);
            navigateAnyway(() => navigate(mainAspxUrl({ appId, pageType: 'entityrecord', etn, id: saved.id, formId }), { replace: true }));
            return 'saved';
          }
        }
        toList();
        return 'saved';
      }
      if (andClose) toList();
      // Create: the URL now names the new record (no history entry for the form that no longer exists).
      else if (creating) {
        onCreated(saved.id);
        navigateAnyway(() => navigate(mainAspxUrl({ appId, pageType: 'entityrecord', etn, id: saved.id, formId }), { replace: true, state: location.state }));
      }
      return 'saved';
    } finally {
      saving.current = false;
    }
  };
  // Auto-save (#117, ruling R4): an existing record with changes saves 30 seconds after the last change, and when it is left.
  const autoSave = useAutoSave({
    enabled: xrmHost?.org.isAutoSaveEnabled === true,
    creating,
    dirty: state.dirty,
    busy: state.busy,
    changeKey: state.values,
    autoSave: () => save(false, SaveMode.AutoSave),
  });
  // Leaving a changed form saves it when auto-save is on; the unsaved-changes prompt is shown when it is off, prevented or fails.
  const [askDiscard, setAskDiscard] = useState(false);
  useEffect(() => {
    if (blocker.state !== 'blocked') {
      setAskDiscard(false);
      return;
    }
    const pending = autoSave.saveNow();
    if (!pending) {
      setAskDiscard(true);
      return;
    }
    let live = true;
    void pending.then((outcome) => {
      if (!live) return;
      if (outcome === 'saved') blocker.proceed();
      else setAskDiscard(true);
    });
    return () => {
      live = false;
    };
    // Once per blocked navigation.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [blocker.state]);
  // The script host lives as long as this component: it is keyed by form and record (a created record keeps its create
  // form), so leaving either disposes it.
  const scripts = useFormScripts(form, loadKey, ctx, {
    install: xrmHost?.install,
    // Xrm.Page is this form's formContext from before its OnLoad until the form is left.
    onFormContext: (fc) => {
      if (fc) xrmHost?.setCurrentForm(fc);
      else if (xrmHost && xrmHost.currentForm() === formContextRef.current) xrmHost.setCurrentForm(null);
      formContextRef.current = fc;
      setFormContext(fc);
    },
    env: {
      form,
      ctx,
      globalContext: xrmHost?.globalContext,
      asyncHandlers,
      state,
      overlay,
      forms,
      lcid,
      timeZone,
      selectForm: (id) => {
        const next = forms.find((f) => f.id === id);
        if (next) onSelectForm(next);
      },
      refreshControl: reloadControl,
      save: (mode, andClose) => save(andClose, mode),
      // data.refresh() reads the record, the subgrids and the timelines again; the form's OnLoad does not run again.
      refresh: async () => {
        await state.refresh();
        for (const name of cells.keys()) reloadControl(name);
      },
      // Leaving through the router: the unsaved-changes prompt applies.
      close: () => navigate(returnTo ?? mainAspxUrl({ appId, pageType: 'entitylist', etn })),
      // formContext.ui.refreshRibbon(): the custom commands evaluate their rules again (UI-1e ruling E5).
      onRefreshRibbon: () => ribbonRefresh.current?.(),
    },
  });
  scriptsRef.current = scripts;
  // Custom commands (UI-1e): their warnings and action errors are form notifications, like a custom control's fallback notice.
  const notifyForm = useCallback(
    (n: FormNotification) => overlay.setFormNotification({ uniqueId: n.key, level: n.intent === 'error' ? 'ERROR' : n.intent === 'warning' ? 'WARNING' : 'INFO', message: n.text }),
    [overlay],
  );
  const ribbon = useRibbonCommands({
    entity: etn,
    lcid,
    form: state,
    formContext,
    formId: form.id,
    refreshKey,
    notify: notifyForm,
    clear: (key) => void overlay.clearFormNotification(key),
    // A save with its OnSave handlers: a custom command waits for none of it, it does not run.
    saving: () => saving.current,
    install: xrmHost?.install,
    globalContext: xrmHost?.globalContext,
  });
  ribbonRefresh.current = ribbon.refresh;
  // The create form starts with the URL's form parameters (extraqs); those that fit no column of the form are reported.
  const [ignoredParameters, setIgnoredParameters] = useState<string[]>([]);
  useEffect(() => {
    if (!creating || !parameters) return;
    const navFor = (attribute: string, target: string) =>
      ctx.fieldContext.lookups.find((r) => r.referencingAttribute === attribute && r.referencedEntity === target)?.nav;
    const { values, ignored } = formParameterValues(ctx.entity.attributes, formColumns(form), parameters, navFor);
    for (const [name, value] of Object.entries(values)) state.setDefault(name, value);
    setIgnoredParameters(ignored);
    // setDefault changes with every render; the parameters are applied once per create form.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [creating, parameters, ctx, form]);
  // #143: the create form opened from a subgrid starts with the values InitializeFrom maps from the parent, for the columns it shows
  // and that no form parameter set (the lookup to the parent comes with its name through extraqs). A failure is a warning.
  const [mappingError, setMappingError] = useState<string>();
  useEffect(() => {
    if (!api || !creating || !createFrom) return;
    let cancelled = false;
    const skip = new Set(Object.keys(parameters ?? {}).map((k) => k.toLowerCase()));
    (async () => {
      const response = await api.get<Record<string, unknown>>(initializeFromPath(createFrom, etn));
      const { values, lookups } = mappedValues(ctx.entity.attributes, formColumns(form), response, ctx.fieldContext.lookups, skip);
      await nameLookups(api, values, lookups, createFrom);
      if (cancelled) return;
      for (const [name, value] of Object.entries(values)) state.setDefault(name, value);
    })().catch((e: unknown) => !cancelled && setMappingError(t('record.subgrid.mappingFailed', { reason: e instanceof Error ? e.message : String(e) })));
    return () => {
      cancelled = true;
    };
    // setDefault changes with every render; the maps are applied once per create form.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [api, creating, createFrom?.set, createFrom?.id, ctx, form]);
  const recordCurrency = ctx.record?._transactioncurrencyid_value;
  const [currency, setCurrency] = useState<Currency>();
  const [currencyFailed, setCurrencyFailed] = useState(false);
  const hasMoney = ctx.entity.attributes.some((a) => a.kind === 'money');
  const { setDefault } = state;
  const onForm = (attribute: string) =>
    ctx.entity.attributes.some((a) => a.logicalName === attribute) && formColumns(form).includes(attribute);
  const ownerOnForm = creating && onForm('ownerid');

  // The create form starts with the current user as Owner, as Dataverse fills it, when the form carries the column. A read
  // failure leaves the field empty for the user to pick.
  useEffect(() => {
    if (!api || !ownerOnForm) return;
    let cancelled = false;
    api.get<{ fullname?: string | null }>(`systemusers(${userId})?$select=fullname`).then(
      (user) => {
        if (cancelled) return;
        const nav = ctx.fieldContext.lookups.find((r) => r.referencingAttribute === 'ownerid' && r.referencedEntity === 'systemuser')?.nav;
        setDefault('ownerid', { id: userId, target: 'systemuser', set: 'systemusers', name: user.fullname ?? undefined, nav });
      },
      () => undefined,
    );
    return () => {
      cancelled = true;
    };
    // setDefault changes with every render; the owner is read once per create form.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [api, userId, ownerOnForm, ctx, form]);

  useEffect(() => {
    if (!api || !hasMoney) return;
    let cancelled = false;
    loadCurrency(api, userId, typeof recordCurrency === 'string' ? recordCurrency : undefined, creating).then(
      (found) => {
        if (cancelled) return;
        setCurrency(found);
        // The create form starts with the default currency, when the form carries the column.
        if (creating && found && onForm('transactioncurrencyid')) {
          const nav = ctx.fieldContext.lookups.find((r) => r.referencingAttribute === 'transactioncurrencyid')?.nav;
          setDefault('transactioncurrencyid', { id: found.id, target: 'transactioncurrency', set: 'transactioncurrencies', nav });
        }
      },
      () => !cancelled && setCurrencyFailed(true),
    );
    return () => {
      cancelled = true;
    };
    // setDefault changes with every render; the currency is read once per record.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [api, userId, hasMoney, recordCurrency, creating, ctx, form]);

  // Plan 4.1b, task 9: an email has Send first in its bar, its send status under the title and its attachments beside the form.
  const isEmail = etn === 'email';
  const emailId = isEmail ? ctx.fieldContext.record?.id : undefined;
  const sendCommands = emailSendCommands({
    ctx,
    record: state.record,
    busy: state.busy,
    onSend: () => {
      if (!api || !emailId) return;
      void sendEmail({
        api,
        id: emailId,
        save: async () => (await save(false).then((outcome) => (autoSave.noteSaved(outcome), outcome))) === 'saved',
        values: () => state.latest().values,
        notify: (text) => notifyForm({ key: SEND_NOTIFICATION, intent: 'error', text }),
        clear: () => void overlay.clearFormNotification(SEND_NOTIFICATION),
        reload: onReload,
        // An auto-save in flight: Send waits for it (a second save would not run, and Send would stop silently).
        saving: () => saving.current,
      });
    },
  });

  const commandBar = (
    <CommandBar
      leading={sendCommands}
      ctx={ctx}
      state={state}
      onNew={() => navigate(mainAspxUrl({ appId, pageType: 'entityrecord', etn, formId: form.id }), { state: location.state })}
      onSave={() => void save(false).then(autoSave.noteSaved)}
      onSaveAndClose={() => void save(true)}
      onDelete={() => void state.remove().then((removed) => removed && toList())}
      onRefresh={() => void state.refresh().then(onRefreshed)}
      // Back to the list or form it came from (a side pane closes); the unsaved-changes prompt runs first.
      onClose={() => navigate(returnTo ?? mainAspxUrl({ appId, pageType: 'entitylist', etn }))}
      onAssign={async (owner) => {
        const assigned = await state.assign(owner);
        if (assigned) onReload();
        return assigned;
      }}
      onSetState={async (stateCode, status) => {
        // The record is read again with its rights: an inactive record is read-only, an activated one editable.
        const changed = await state.setState(stateCode, status);
        if (changed) onReload();
        return changed;
      }}
      custom={ribbon.visible}
    />
  );

  return (
    <>
      <FormView
        form={form}
        state={state}
        ctx={ctx}
        lcid={lcid}
        forms={forms}
        notice={notice}
        commandBar={commandBar}
        currencySymbol={currency?.symbol}
        appId={appId}
        timeline={timeline}
        refreshKey={refreshKey}
        scriptNotifications={scripts.notifications}
        overlay={overlay}
        saveStatus={autoSave.status}
        onSave={() => void save(false).then(autoSave.noteSaved)}
        senderPolicy={isEmail ? { mode: emailSendFrom ?? 'userOrQueue', userId } : undefined}
        banner={emailId ? <EmailStatusBanner id={emailId} record={state.record} onChanged={onReload} /> : undefined}
        aside={emailId ? <AttachmentsSection emailId={emailId} readOnly={state.readOnly} /> : undefined}
        warnings={
          currencyFailed || ignoredParameters.length || mappingError || associateError
            ? [
                ...(currencyFailed ? [t('record.currencyFailed')] : []),
                ...(ignoredParameters.length ? [t('xrm.formParameters.ignored', { names: ignoredParameters.join(', ') })] : []),
                ...(mappingError ? [mappingError] : []),
                ...(associateError ? [associateError] : []),
              ]
            : undefined
        }
        onSelectForm={onSelectForm}
      />
      <ConfirmDialog
        open={blocker.state === 'blocked' && askDiscard}
        title={t('record.discard.title')}
        body={t('record.discard.body')}
        confirmLabel={t('record.discard.confirm')}
        cancelLabel={t('common.cancel')}
        onCancel={() => blocker.state === 'blocked' && blocker.reset()}
        onConfirm={() => blocker.state === 'blocked' && blocker.proceed()}
      />
    </>
  );
}
