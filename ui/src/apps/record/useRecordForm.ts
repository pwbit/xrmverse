import { useCallback, useContext, useEffect, useMemo, useRef, useState } from 'react';
import { changedBody, createBody, FieldError, sameValue, valuesFromRecord, type FieldValue, type FormValues, type LookupValue } from '../../admin/tables/formValues';
import { isFileKind, type AttributeMeta } from '../../admin/tables/metadata';
import { PARTY_TYPE_OF, partyItems, partyValuesOf, type Party, type PartyRow } from '../../admin/tables/partyLists';
import { entityPath } from '../../admin/tables/tableApi';
import type { UiOverlay } from '../../client-api/uiOverlay';
import type { Cell, FormModel } from '../../model/form';
import { labelFor } from '../../parsers/formXml';
import { useAuth } from '../../shell/auth/AuthProvider';
import { t } from '../../shell/i18n/t';
import { DisplayZoneContext } from '../displayZone';
import { fetchRecord, formColumns, type RecordContext } from './loadRecord';
import { stateOfRecord } from './recordStates';

const DEFAULT_LCID = 1033;

/** Who changed a value: a user change raises the column's OnChange, a script's `setValue` does not (Learn, OnChange). */
export type ChangeSource = 'user' | 'script';

export interface RecordForm {
  values: FormValues;
  /** The values as loaded (with the create defaults): what `dirty` compares with. */
  original: FormValues;
  /**
   * The values and original values including changes made since the last render (a script reads back what it just set), and
   * the record id, which a create form has as soon as the record is created.
   */
  latest(): { values: FormValues; original: FormValues; id?: string };
  /** The record as last loaded (read or saved); undefined on the create form. */
  record?: Record<string, unknown>;
  /** A field differs from the record as loaded. */
  dirty: boolean;
  /** Messages by attribute (a required field left empty, a value the form cannot send). */
  errors: Record<string, string>;
  /** The form notification: a save error or the required-field messages; cleared by the next save, refresh or edit. */
  notification?: string;
  busy: boolean;
  /** `busy` as of now (not as of the last render): a request runs, or a created record waits for its read. */
  isBusy(): boolean;
  /** The user lacks write access to the record, or the record is inactive (an existing record only). */
  readOnly: boolean;
  /** The label of the record's state when it is not active (`statecode` other than 0): the form is read-only. */
  inactiveLabel?: string;
  isReadOnly(attribute: string): boolean;
  isRequired(attribute: string): boolean;
  /**
   * The save's required check on `attributes` only (a wizard step): sets the field errors of the empty required ones and
   * clears those of the others it checks; sets no form notification and sends nothing. True when none is missing.
   */
  checkRequired(attributes: string[]): boolean;
  /** Changes a value; `source` `user` (the default) raises the column's OnChange through `onUserChange`. */
  setValue(attribute: string, value: FieldValue, source?: ChangeSource): void;
  /**
   * Sets a create default: part of the original values, so it is not a user change (the form stays clean) but is still saved.
   * A field the user has already changed keeps the user's value (a default read asynchronously arrives late).
   */
  setDefault(attribute: string, value: FieldValue): void;
  /** Creates (POST) or updates (PATCH the changed columns); the new or current id, or undefined when it did not save. */
  save(): Promise<{ id: string } | undefined>;
  /** Deletes the record; true when it was deleted. */
  remove(): Promise<boolean>;
  /** Reads the record again; its values replace the form's. */
  refresh(): Promise<void>;
  /**
   * Sets `ownerid` (the Web API Assign: a PATCH with a bind); true when it was assigned. It does not read the record again: the
   * caller reloads the record with its rights, which change with the owner, and the form state follows the new context.
   */
  assign(owner: LookupValue): Promise<boolean>;
  /**
   * Activate or Deactivate: one PATCH of `statecode` and `statuscode` (only `statecode` without a status); true when it
   * changed. Like {@link assign}, the caller reloads the record (the form becomes read-only or editable with it).
   */
  setState(state: number, status?: number): Promise<boolean>;
}

const cellsOf = (form: FormModel): Cell[] => form.tabs.flatMap((tab) => tab.columns.flatMap((c) => c.sections.flatMap((s) => s.rows.flat())));
const fieldCells = (cells: Cell[]) => cells.flatMap((c) => (c.control?.kind === 'field' ? [{ cell: c, attribute: c.control.attribute, disabled: c.control.disabled }] : []));

const isEmpty = (v: FieldValue | undefined): boolean =>
  v === null || v === undefined || (typeof v === 'string' && v.trim() === '') || (Array.isArray(v) && v.length === 0);

export interface RecordFormOptions {
  /** The user's language for the labels of the required messages (1033 when absent). */
  lcid?: number;
  /** What the form's scripts changed: the required level, the submit mode, the control notifications that block a save. */
  overlay?: UiOverlay;
  /** Called after a user change of `attribute` (the form's OnChange). */
  onUserChange?: (attribute: string) => void;
}

/**
 * The state of one record form (Global Constraints, "Required and read-only"): the values and the record's original values,
 * the dirty flag, the field errors, the required check, and save, delete, refresh and assign.
 */
export function useRecordForm(ctx: RecordContext, form: FormModel, options: RecordFormOptions = {}): RecordForm {
  const { api } = useAuth();
  // #131: User Local dates are read and sent in the user's time zone (none in a test without the apps' settings: UTC).
  const zone = useContext(DisplayZoneContext);
  const lcid = options.lcid ?? DEFAULT_LCID;
  const { overlay } = options;
  const onUserChange = useRef(options.onUserChange);
  onUserChange.current = options.onUserChange;
  const creating = !ctx.fieldContext.record;
  const set = ctx.entity.entitySetName;
  const id = ctx.fieldContext.record?.id;

  const { fields, bodyFields, labels, footerOnly, disabled, cellsByAttribute } = useMemo(() => {
    const body = fieldCells([...cellsOf(form), ...form.header]);
    const footer = fieldCells(form.footer);
    const byName = new Map(ctx.entity.attributes.map((a) => [a.logicalName, a]));
    const all = [...body, ...footer];
    const attrs = [...new Set(all.map((f) => f.attribute))].flatMap((n) => byName.get(n) ?? []);
    const bodyNames = new Set(body.map((f) => f.attribute));
    return {
      fields: attrs,
      bodyFields: attrs.filter((a) => bodyNames.has(a.logicalName)),
      labels: new Map(all.map((f) => [f.attribute, labelFor(f.cell.labels, lcid)])),
      footerOnly: new Set(footer.map((f) => f.attribute).filter((n) => !bodyNames.has(n))),
      // A control marked disabled in the FormXml makes the field read-only wherever it is placed.
      disabled: new Set(all.filter((f) => f.disabled).map((f) => f.attribute)),
      cellsByAttribute: (name: string) => all.filter((f) => f.attribute === name).map((f) => f.cell),
    };
  }, [form, ctx.entity, lcid]);

  const navFor = useCallback(
    (attribute: string, target: string) =>
      ctx.fieldContext.lookups.find((r) => r.referencingAttribute === attribute && r.referencedEntity === target)?.nav,
    [ctx.fieldContext.lookups],
  );
  // Plan 4.1b, task 9: a party list is read from, and written through, the party navigation (`{activity}_activity_parties`).
  const partyNavigation = ctx.fieldContext.partyNavigation;
  const load = useCallback(
    (record: Record<string, unknown> | undefined): FormValues => {
      if (!record) return {};
      const values = valuesFromRecord(fields, record, navFor, zone);
      const raw = partyNavigation ? record[partyNavigation] : undefined;
      for (const a of fields) if (a.kind === 'partylist') values[a.logicalName] = partyValuesOf(a.logicalName, Array.isArray(raw) ? (raw as PartyRow[]) : []);
      return values;
    },
    [fields, navFor, zone, partyNavigation],
  );

  const [record, setRecord] = useState(ctx.record);
  // The record as last read, for the party rows of the lists the form does not show (sent back whole on update).
  const recordRef = useRef(record);
  recordRef.current = record;
  const [original, setOriginal] = useState<FormValues>(() => load(ctx.record));
  const [values, setValues] = useState<FormValues>(original);
  // The values as changed by calls not yet rendered: a script that sets a value reads it back at once, and a save started by
  // an OnSave handler that changed a value sends it.
  const latestRef = useRef({ values, original });
  latestRef.current = { values, original };
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [notification, setNotification] = useState<string>();
  const [busy, setBusy] = useState(false);
  // `busy` is stale inside a closure that started before it changed: a second call is refused through this ref.
  const busyRef = useRef(false);
  const columns = useMemo(() => formColumns(form), [form]);

  // The id of the record this create form created, until the page hands the form the new record.
  const createdId = useRef<string>();
  // From the create until the page hands the form the new record, the form is busy: nothing typed then could be saved.
  const [awaitingRead, setAwaitingRead] = useState(false);

  // The fields the user changed on this form: a late create default does not overwrite them.
  const touched = useRef(new Set<string>());

  // A different record or form (or the same record reloaded by the caller) replaces the state.
  const seen = useRef({ ctx, form });
  useEffect(() => {
    if (seen.current.ctx === ctx && seen.current.form === form) return;
    seen.current = { ctx, form };
    touched.current = new Set();
    createdId.current = undefined;
    setAwaitingRead(false);
    const loaded = load(ctx.record);
    latestRef.current = { values: loaded, original: loaded };
    setRecord(ctx.record);
    setOriginal(loaded);
    setValues(loaded);
    setErrors({});
    setNotification(undefined);
    // `load` follows the form's fields; only a new context or form resets.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [ctx, form]);

  // Dataverse: an inactive record is read-only until it is activated again.
  const recordState = creating ? undefined : stateOfRecord(record);
  const inactiveLabel =
    recordState !== undefined && recordState !== 0
      ? (ctx.entity.attributes.find((a) => a.logicalName === 'statecode')?.options.find((o) => o.value === recordState)?.label ??
        (typeof record?.['statecode@OData.Community.Display.V1.FormattedValue'] === 'string'
          ? (record['statecode@OData.Community.Display.V1.FormattedValue'] as string)
          : String(recordState)))
      : undefined;
  const readOnly = !creating && (!ctx.rights.includes('Write') || inactiveLabel !== undefined);
  // A control disabled in the FormXml stays read-only unless a script enabled one of the column's controls (`setDisabled(false)`).
  const formXmlDisabled = (attribute: string) =>
    disabled.has(attribute) && !cellsByAttribute(attribute).some((c) => overlay?.element(c).disabled === false);
  const neverSubmitted = (attribute: string) => overlay?.attribute(attribute).submitMode === 'never';
  const isReadOnly = (attribute: string): boolean =>
    formXmlDisabled(attribute) || footerOnly.has(attribute) || neverSubmitted(attribute) || refusedByServer(attribute);
  /**
   * The server would not take a value for the column (no Write right, not valid for create or update, field security, a
   * file, a party list): it is never sent, whatever the form or a script does. A column that is only disabled on the form is
   * still sent when a script changed it (as in Dataverse). A party list is written only through the party navigation, when
   * the relationships name it and its participation type is known.
   */
  const refusedByServer = (attribute: string): boolean => {
    if (readOnly || attribute === 'statecode') return true;
    const a = ctx.entity.attributes.find((x) => x.logicalName === attribute);
    if (!a) return true;
    if (a.kind === 'unsupported') return true;
    if (a.kind === 'partylist' && (!partyNavigation || PARTY_TYPE_OF[a.logicalName] === undefined)) return true;
    // A file or image is uploaded on its own, on a record that exists.
    if (isFileKind(a.kind)) return creating;
    if (!(creating ? a.validForCreate : a.validForUpdate)) return true;
    // Field security: the server leaves out a secured column the user may not read, and then they may not update it either.
    return !creating && a.secured && !!record && !(a.logicalName in record) && !(`_${a.logicalName}_value` in record);
  };
  const isRequired = (attribute: string): boolean => {
    const scripted = overlay?.attribute(attribute).requiredLevel;
    if (scripted) return scripted === 'required';
    const level = ctx.entity.attributes.find((a) => a.logicalName === attribute)?.requiredLevel;
    return level === 'SystemRequired' || level === 'ApplicationRequired';
  };

  const writable = (): AttributeMeta[] => bodyFields.filter((a) => !isReadOnly(a.logicalName));
  /** The required check of a save: the columns of `toCheck` that are required and empty in `values`. */
  const missingRequired = (toCheck: AttributeMeta[], values: FormValues): AttributeMeta[] =>
    toCheck.filter((a) => isRequired(a.logicalName) && isEmpty(values[a.logicalName]));
  const requiredErrors = (missing: AttributeMeta[]): Record<string, string> =>
    Object.fromEntries(missing.map((a) => [a.logicalName, t('record.required', { label: labels.get(a.logicalName) ?? a.displayName })]));
  /** The columns a save may send: Learn, setSubmitMode: `never` is never sent; a disabled column is (a script set it). */
  const sendable = (): AttributeMeta[] => bodyFields.filter((a) => !refusedByServer(a.logicalName) && !neverSubmitted(a.logicalName));
  const changed = (o: FormValues, v: FormValues) => sendable().some((a) => !sameValue(a, o[a.logicalName], v[a.logicalName]));
  const dirty = changed(original, values);

  const setValue = (attribute: string, value: FieldValue, source: ChangeSource = 'user') => {
    touched.current.add(attribute);
    latestRef.current = { ...latestRef.current, values: { ...latestRef.current.values, [attribute]: value } };
    setValues((prev) => ({ ...prev, [attribute]: value }));
    setErrors((prev) => {
      if (!(attribute in prev)) return prev;
      const { [attribute]: _gone, ...rest } = prev;
      return rest;
    });
    if (source === 'user') onUserChange.current?.(attribute);
  };

  const setDefault = (attribute: string, value: FieldValue) => {
    if (touched.current.has(attribute)) return;
    const { values: v, original: o } = latestRef.current;
    latestRef.current = { values: { ...v, [attribute]: value }, original: { ...o, [attribute]: value } };
    setOriginal((prev) => ({ ...prev, [attribute]: value }));
    setValues((prev) => ({ ...prev, [attribute]: value }));
  };

  const reload = async (): Promise<void> => {
    if (!api || !set || !id) return;
    const fresh = await fetchRecord(api, ctx.entity, id, columns, partyNavigation);
    const loaded = load(fresh);
    latestRef.current = { values: loaded, original: loaded };
    setRecord(fresh);
    setOriginal(loaded);
    setValues(loaded);
  };

  /** Runs a request with the form busy; a failure becomes the notification (the values stay). */
  async function guarded<T>(run: () => Promise<T>): Promise<T | undefined> {
    if (busyRef.current) return undefined;
    busyRef.current = true;
    setBusy(true);
    try {
      return await run();
    } catch (e) {
      setNotification(e instanceof Error ? e.message : String(e));
      return undefined;
    } finally {
      busyRef.current = false;
      setBusy(false);
    }
  }

  /** Reads the record again after a write that succeeded: a failure is a notification, not a failed write. */
  async function reloadAfterWrite(): Promise<void> {
    try {
      await reload();
    } catch (e) {
      setNotification(t('record.savedNotReloaded', { detail: e instanceof Error ? e.message : String(e) }));
    }
  }

  /** A lookup a script set carries no entity set: it is read from the target's metadata before the body is built. */
  async function withSets(current: FormValues, toSend: AttributeMeta[]): Promise<FormValues> {
    if (!api) return current;
    const missing = toSend.flatMap((a) => {
      const v = current[a.logicalName];
      return a.kind === 'lookup' && v && typeof v === 'object' && !Array.isArray(v) && !v.set ? [a.logicalName] : [];
    });
    if (missing.length === 0) return current;
    const next = { ...current };
    for (const name of missing) {
      const v = next[name] as LookupValue;
      const meta = await api.get<{ EntitySetName?: string }>(`${entityPath(v.target)}?$select=EntitySetName`);
      next[name] = { ...v, set: meta.EntitySetName ?? undefined };
    }
    return next;
  }

  /** Entity sets of the party tables, read once per form from their metadata (never guessed from the table name). */
  const partySets = useRef(new Map<string, string>());

  /**
   * The party navigation's items for the party lists to send. On create: every non-empty list. On update, when any list changed:
   * the whole party set (on PATCH the server replaces every party list type, plan 4.1b ruling P12): each list of the form as it
   * now is, and each other party list of the table as the record holds it; an emptied list sends no item and is cleared.
   */
  async function partyBody(toSend: AttributeMeta[], original: FormValues, values: FormValues, always: ReadonlySet<string>): Promise<Record<string, unknown>> {
    if (!api || !partyNavigation) return {};
    const lists = toSend.filter((a) => a.kind === 'partylist');
    const changedLists = lists.filter((a) => {
      const now = (values[a.logicalName] as Party[] | null) ?? [];
      if (creating) return now.length > 0;
      return always.has(a.logicalName) || !sameValue(a, original[a.logicalName], values[a.logicalName]);
    });
    if (changedLists.length === 0) return {};
    const rawRows = recordRef.current?.[partyNavigation];
    const rows = Array.isArray(rawRows) ? (rawRows as PartyRow[]) : [];
    const columns = creating
      ? changedLists.map((a) => a.logicalName)
      : ctx.entity.attributes.filter((a) => a.kind === 'partylist' && PARTY_TYPE_OF[a.logicalName] !== undefined).map((a) => a.logicalName);
    const partiesOf = (column: string): Party[] =>
      column in values ? ((values[column] as Party[] | null) ?? []) : partyValuesOf(column, rows);
    const tables = new Set(columns.flatMap((c) => partiesOf(c).flatMap((p) => (p.id && p.logicalName ? [p.logicalName] : []))));
    for (const table of tables) {
      if (partySets.current.has(table)) continue;
      const meta = await api.get<{ EntitySetName?: string | null }>(`${entityPath(table)}?$select=EntitySetName`);
      if (meta.EntitySetName) partySets.current.set(table, meta.EntitySetName);
    }
    const items = columns.flatMap((c) => partyItems(c, partiesOf(c), (table) => partySets.current.get(table)));
    return { [partyNavigation]: items };
  }

  async function save(): Promise<{ id: string } | undefined> {
    if (!api || !set || readOnly || busyRef.current) return undefined;
    // Created already: the page is loading the new record; the form does not create it twice. A value a script set since the
    // create cannot be sent yet: the save fails with a message instead of losing it silently.
    if (creating && createdId.current) {
      const { original: o, values: v } = latestRef.current;
      if (!changed(o, v)) return { id: createdId.current };
      setNotification(t('record.createdNotLoaded'));
      return undefined;
    }
    setNotification(undefined);
    // Learn, setNotification: a control error notification blocks the save and is shown at the top of the form.
    const blocking = overlay?.blockingMessages() ?? [];
    if (blocking.length > 0) {
      setNotification(blocking.join(' '));
      return undefined;
    }
    const { original, values } = latestRef.current;
    const missing = missingRequired(writable(), values);
    const found: Record<string, string> = requiredErrors(missing);
    let body: Record<string, unknown> = {};
    if (missing.length === 0) {
      const toSend = sendable();
      // Learn, setSubmitMode: `always` sends the column even when it did not change.
      const always = new Set(toSend.filter((a) => overlay?.attribute(a.logicalName).submitMode === 'always').map((a) => a.logicalName));
      try {
        const withSet = await withSets(values, toSend);
        body = creating ? createBody(toSend, withSet, zone) : changedBody(toSend, original, withSet, always, zone);
        Object.assign(body, await partyBody(toSend, original, values, always));
      } catch (e) {
        if (!(e instanceof FieldError)) {
          setNotification(e instanceof Error ? e.message : String(e));
          return undefined;
        }
        found[e.field] = t(e.key, e.params);
      }
    }
    setErrors(found);
    if (Object.keys(found).length > 0) {
      setNotification(Object.values(found).join(' '));
      return undefined;
    }
    if (creating) {
      return guarded(async () => {
        const created = await api.post(set, body);
        if (!created.id) throw new Error(t('record.noCreatedId'));
        createdId.current = created.id;
        // What was sent is now the record's value: the form is clean until the user or a script changes it.
        latestRef.current = { values, original: values };
        setOriginal(values);
        setAwaitingRead(true);
        return { id: created.id };
      });
    }
    if (Object.keys(body).length === 0) return { id: id! };
    return guarded(async () => {
      await api.patch(`${set}(${id})`, body);
      await reloadAfterWrite();
      return { id: id! };
    });
  }

  const checkRequired = (attributes: string[]): boolean => {
    const wanted = new Set(attributes);
    const toCheck = writable().filter((a) => wanted.has(a.logicalName));
    const found = requiredErrors(missingRequired(toCheck, latestRef.current.values));
    setErrors((prev) => {
      const next = { ...prev };
      for (const a of toCheck) delete next[a.logicalName];
      return { ...next, ...found };
    });
    return Object.keys(found).length === 0;
  };

  async function remove(): Promise<boolean> {
    if (!api || !set || !id) return false;
    setNotification(undefined);
    return (await guarded(async () => {
      await api.delete(`${set}(${id})`);
      return true;
    })) === true;
  }

  async function refresh(): Promise<void> {
    setNotification(undefined);
    setErrors({});
    await guarded(reload);
  }

  async function assign(owner: LookupValue): Promise<boolean> {
    if (!api || !set || !id) return false;
    setNotification(undefined);
    const ownerSet = owner.set ?? (owner.target === 'team' ? 'teams' : 'systemusers');
    return (await guarded(async () => {
      await api.patch(`${set}(${id})`, { 'ownerid@odata.bind': `/${ownerSet}(${owner.id})` });
      return true;
    })) === true;
  }

  async function setState(state: number, status?: number): Promise<boolean> {
    if (!api || !set || !id) return false;
    setNotification(undefined);
    const body = status === undefined ? { statecode: state } : { statecode: state, statuscode: status };
    return (await guarded(async () => {
      await api.patch(`${set}(${id})`, body);
      return true;
    })) === true;
  }

  const latest = () => ({ ...latestRef.current, id: id ?? createdId.current });
  const isBusy = () => busyRef.current || (creating && createdId.current !== undefined);

  return { values, original, latest, record, dirty, errors, notification, busy: busy || awaitingRead,
    isBusy,
    readOnly,
    inactiveLabel,
    isReadOnly,
    isRequired,
    checkRequired,
    setValue,
    setDefault,
    save,
    remove,
    refresh,
    assign,
    setState,
  };
}
