import { Button, MessageBar, MessageBarBody, Spinner, Subtitle2 } from '@fluentui/react-components';
import { Fragment, useEffect, useId, useMemo, useState } from 'react';
import { useNavigate, useParams } from 'react-router-dom';
import { useAuth } from '../../shell/auth/AuthProvider';
import { t } from '../../shell/i18n/t';
import { useNotify } from '../../shell/notify/Notifications';
import { AuditHistoryPanel } from '../audit/AuditHistoryPanel';
import { ConfirmDialog } from '../common/ConfirmDialog';
import { FieldEditor } from './fields/FieldEditor';
import { NoAccessField, SecuredField } from './fields/SecuredField';
import type { FieldContext } from './fields/fieldProps';
import { buildFormModel } from './formModel';
import { changedBody, createBody, FieldError, valuesFromRecord, type FormValues } from './formValues';
import { isFileKind, recordKey, type AttributeMeta } from './metadata';
import { PARTY_SELECT } from './partyLists';
import { loadEntity, loadGlobalChoices, loadRelationships, lookupsOf, navigationFor, partyNavigationOf } from './tableApi';
import { useLoad } from './useLoad';
import { PageHeader } from '../../shell/layout/PageHeader';
import { useSurfaceStyles } from '../../shell/layout/surface';
import { DeleteIcon, SaveIcon } from '../../shell/icons';

const PREFER_ANNOTATIONS = { Prefer: 'odata.include-annotations="*"' };
const GROUP_LABEL = {
  Text: 'form.group.Text',
  Numbers: 'form.group.Numbers',
  Dates: 'form.group.Dates',
  Choices: 'form.group.Choices',
  Lookups: 'form.group.Lookups',
  Files: 'form.group.Files',
  Other: 'form.group.Other',
} as const;

/** Create (`/records/new`) or edit one record of any table, from its metadata. */

export function RecordForm() {
  const surface = useSurfaceStyles();
  const formId = useId();
  const { table = '', id = '' } = useParams();
  const { api } = useAuth();
  const notify = useNotify();
  const navigate = useNavigate();
  const creating = id === 'new';

  const meta = useLoad(table, (a) => loadEntity(a, table));
  const rels = useLoad(table, (a) => loadRelationships(a));
  const entity = meta.data;
  const model = useMemo(() => (entity ? buildFormModel(entity) : []), [entity]);
  const fields = useMemo(() => model.flatMap((g) => g.fields), [model]);
  const needsGlobalChoices = fields.some((a) => a.globalOptionSet && a.options.length === 0);
  const choices = useLoad(entity && needsGlobalChoices ? 'global' : undefined, (a) => loadGlobalChoices(a));

  const readable = fields.filter((a) => a.validForRead && a.kind !== 'unsupported');
  // A file column's name is its `<column>_name` companion (always created with it). A party list is no property: its rows
  // come with the party navigation (plan 2.11, A8).
  const properties = (a: AttributeMeta) =>
    a.kind === 'file' ? [a.logicalName, `${a.logicalName}_name`] : a.kind === 'partylist' ? [] : [recordKey(a)];
  const partyNavigation = entity && rels.data && fields.some((a) => a.kind === 'partylist') ? partyNavigationOf(rels.data, table) : undefined;
  // The party rows are expanded once the relationships tell their navigation property's name (never guessed).
  const waitsForParties = !!entity && fields.some((a) => a.kind === 'partylist') && !rels.data;
  const record = useLoad(entity && !creating && entity.entitySetName && !waitsForParties ? `${entity.logicalName}/${id}` : undefined, (a) => {
    const select = [entity!.primaryIdAttribute, ...readable.flatMap(properties)].join(',');
    const expand = partyNavigation ? `&$expand=${partyNavigation}($select=${PARTY_SELECT})` : '';
    return a.get<Record<string, unknown>>(`${entity!.entitySetName}(${id})?$select=${select}${expand}`, { headers: PREFER_ANNOTATIONS });
  });

  const [values, setValues] = useState<FormValues>({});
  const [original, setOriginal] = useState<FormValues>({});
  const [errors, setErrors] = useState<Record<string, string>>({});
  // The editors mount only once `values` hold this record's values (`id`, or `new` for the create form): otherwise the
  // first render shows an empty form. Per id, because a create followed by its new record keeps this component.
  const [hydratedFor, setHydratedFor] = useState<string>();
  const [busy, setBusy] = useState(false);
  const [confirmingDelete, setConfirmingDelete] = useState(false);

  useEffect(() => {
    if (!rels.data || !entity) return;
    if (creating) {
      setValues({});
      setOriginal({});
    } else if (record.data) {
      const loaded = valuesFromRecord(fields, record.data, (attribute, target) => navigationFor(rels.data!, table, attribute, target));
      setValues(loaded);
      setOriginal(loaded);
    } else {
      // The record is still loading: not hydrated yet, or its arrival would render the editors once without its values.
      return;
    }
    setErrors({});
    setHydratedFor(id);
    // fields derives from entity; the record and relationships are what a reload replaces.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [record.data, rels.data, entity, creating]);

  const set = entity?.entitySetName;
  const context: FieldContext = useMemo(
    () => ({
      table,
      lookups: rels.data ? lookupsOf(rels.data, table) : [],
      globalChoices: choices.data ?? {},
      record: !creating && set && record.data ? { set, id, data: record.data } : undefined,
      partyNavigation,
    }),
    [table, rels.data, choices.data, creating, set, id, record.data, partyNavigation],
  );

  const loadError = meta.error ?? rels.error ?? choices.error ?? record.error;
  const ready = !!entity && !!rels.data && (!needsGlobalChoices || !!choices.data) && (creating || !!record.data) && hydratedFor === id;
  const primaryName = entity?.primaryNameAttribute ? original[entity.primaryNameAttribute] : undefined;
  const recordName = typeof primaryName === 'string' && primaryName ? primaryName : id;
  const backToTable = `/manage/tables/${encodeURIComponent(table)}`;
  // A file or image is written by its own upload on an existing record, whatever its IsValidFor flags.
  // A secured column the caller cannot read is left out of the record by the server (it is always in the $select).
  // "No access" only for a secured column the form asked for: the server leaves out what the caller may not read.
  const isUnreadable = (a: AttributeMeta) =>
    a.secured && readable.includes(a) && !creating && !!record.data && !properties(a).some((p) => p in record.data!);
  const isDisabled = (a: AttributeMeta) => busy || (isFileKind(a.kind) ? creating : creating ? !a.validForCreate : !a.validForUpdate);

  async function save() {
    if (!api || !entity?.entitySetName) return;
    setErrors({});
    let body: Record<string, unknown>;
    try {
      body = creating ? createBody(fields, values) : changedBody(fields, original, values);
    } catch (e) {
      if (e instanceof FieldError) {
        setErrors({ [e.field]: t(e.key, e.params) });
        return;
      }
      throw e;
    }
    if (!creating && Object.keys(body).length === 0) {
      notify.success(t('form.noChanges'));
      return;
    }
    setBusy(true);
    try {
      if (creating) {
        const created = await api.post(entity.entitySetName, body);
        notify.success(t('form.created'));
        navigate(created.id ? `${backToTable}/records/${created.id}` : backToTable);
      } else {
        await api.patch(`${entity.entitySetName}(${id})`, body);
        notify.success(t('form.saved'));
        record.reload();
      }
    } catch (e) {
      notify.error(e);
    } finally {
      setBusy(false);
    }
  }

  async function remove() {
    setConfirmingDelete(false);
    if (!api || !entity?.entitySetName) return;
    setBusy(true);
    try {
      await api.delete(`${entity.entitySetName}(${id})`);
      notify.success(t('records.deleted'));
      navigate(backToTable);
    } catch (e) {
      notify.error(e);
      setBusy(false);
    }
  }

  return (
    <section>
      <PageHeader
        title={creating ? t('form.title.new', { table: entity?.displayName ?? table }) : t('form.title.edit', { table: entity?.displayName ?? table })}
        breadcrumb={[
          { label: t('nav.tables'), to: '/manage/tables' },
          { label: entity?.displayName ?? table, to: backToTable },
        ]}
        actions={
          ready && entity?.entitySetName ? (
            <>
              {/* The commands sit in the page's command bar; Save submits the form below through its id. */}
              <Button appearance="primary" type="submit" form={formId} icon={<SaveIcon />} disabled={busy}>
                {creating ? t('form.create') : t('form.save')}
              </Button>
              {!creating && (
                <Button icon={<DeleteIcon />} disabled={busy} onClick={() => setConfirmingDelete(true)}>
                  {t('records.delete.button')}
                </Button>
              )}
            </>
          ) : undefined
        }
      />
      {loadError && (
        <MessageBar intent="error">
          <MessageBarBody>{t('form.loadFailed', { detail: loadError })}</MessageBarBody>
        </MessageBar>
      )}
      {entity && !entity.entitySetName && (
        <MessageBar intent="warning">
          <MessageBarBody>{t('records.noSet', { table })}</MessageBarBody>
        </MessageBar>
      )}
      {!ready && !loadError && <Spinner label={t('common.loading')} />}
      {ready && entity?.entitySetName && (
        <form
          id={formId}
          noValidate
          onSubmit={(e) => {
            e.preventDefault();
            void save();
          }}
        >
          {model.map((g) => (
            <div key={g.group} role="group" aria-labelledby={`${formId}-${g.group}`} className={surface.section}>
              <Subtitle2 as="h3" block id={`${formId}-${g.group}`} className={surface.sectionTitle}>
                {t(GROUP_LABEL[g.group])}
              </Subtitle2>
              {g.fields.map((a) => {
                const editor = isUnreadable(a) ? (
                  <NoAccessField attr={a} />
                ) : (
                  <FieldEditor
                    attr={a}
                    value={values[a.logicalName]}
                    onChange={(v) => setValues((prev) => ({ ...prev, [a.logicalName]: v }))}
                    disabled={isDisabled(a)}
                    error={errors[a.logicalName]}
                    context={context}
                  />
                );
                return a.secured ? <SecuredField key={a.logicalName}>{editor}</SecuredField> : <Fragment key={a.logicalName}>{editor}</Fragment>;
              })}
            </div>
          ))}
        </form>
      )}
      {ready && !creating && entity?.entitySetName && entity.audit?.enabled && (
        <AuditHistoryPanel id={id} reloadKey={record.data} />
      )}
      <ConfirmDialog
        open={confirmingDelete}
        title={t('records.delete.title')}
        body={t('records.delete.body', { name: recordName })}
        confirmLabel={t('records.delete.confirm')}
        cancelLabel={t('common.cancel')}
        onConfirm={() => void remove()}
        onCancel={() => setConfirmingDelete(false)}
      />
    </section>
  );
}
