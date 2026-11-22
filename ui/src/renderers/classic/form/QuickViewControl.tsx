import { makeStyles, MessageBar, MessageBarBody, Spinner, Text, tokens } from '@fluentui/react-components';
import { useEffect, useState } from 'react';
import type { LookupValue } from '../../../admin/tables/formValues';
import { formColumns, loadRecord, type RecordContext } from '../../../apps/record/loadRecord';
import { useRecordForm } from '../../../apps/record/useRecordForm';
import type { FormModel, QuickViewControl as QuickViewModel } from '../../../model/form';
import { normaliseId, parseFormXml, type SystemFormRow } from '../../../parsers/formXml';
import { useAuth } from '../../../shell/auth/AuthProvider';
import { t } from '../../../shell/i18n/t';
import type { ControlEnv } from './controlEnv';
import { FormTab } from './FormTab';

const QUICK_VIEW_FORM = 6;

class InvalidForm extends Error {
  readonly invalidForm = true;
}

const LOOKUP_LOGICAL_NAME = '@Microsoft.Dynamics.CRM.lookuplogicalname';

const useStyles = makeStyles({
  card: {
    borderInlineStartWidth: '3px',
    borderInlineStartStyle: 'solid',
    borderInlineStartColor: tokens.colorNeutralStroke2,
    paddingInlineStart: tokens.spacingHorizontalM,
  },
  label: { display: 'block', marginBlockEnd: tokens.spacingVerticalXXS },
});

/**
 * The record the quick view's lookup holds: the form's live value when the form has the lookup as a field (so a change
 * of the lookup shows at once), else the record as loaded. Undefined when the lookup is empty.
 */
function lookupOf(attribute: string, env: ControlEnv): { id: string; target: string } | undefined {
  const live = env.state.values[attribute];
  if (live !== undefined) {
    const value = live as LookupValue | null;
    return value && typeof value === 'object' && value.id && value.target ? { id: value.id, target: value.target } : undefined;
  }
  const record = env.state.record ?? env.ctx.record;
  const id = record?.[`_${attribute}_value`];
  const target = record?.[`_${attribute}_value${LOOKUP_LOGICAL_NAME}`];
  return typeof id === 'string' && id && typeof target === 'string' && target ? { id, target } : undefined;
}

/**
 * A quick view: the related record of a lookup, drawn read-only with the quick view form (`QuickForms`) of the record's
 * table. Nothing is drawn for an empty lookup, or for a table the control has no quick form for. A form or record that
 * cannot be read shows the reason; it never breaks the host form. `label` is the cell's label, drawn only with the card
 * (a quick view that draws nothing leaves no label behind).
 */
export function QuickViewControl({ control, env, label }: { control: QuickViewModel; env: ControlEnv; label?: string }) {
  const lookup = control.lookupAttribute ? lookupOf(control.lookupAttribute, env) : undefined;
  const formId = lookup && Object.entries(control.forms).find(([table]) => table.toLowerCase() === lookup.target.toLowerCase())?.[1];
  if (!lookup || !formId) return null;
  return (
    <QuickViewLoader
      key={`${lookup.target}|${lookup.id}|${formId}`}
      table={lookup.target}
      id={lookup.id}
      formId={normaliseId(formId)}
      env={env}
      label={label}
    />
  );
}

type Loaded = { ctx: RecordContext; form: FormModel } | { message: string };

function QuickViewLoader({ table, id, formId, env, label }: { table: string; id: string; formId: string; env: ControlEnv; label?: string }) {
  const styles = useStyles();
  const { api } = useAuth();
  const [loaded, setLoaded] = useState<Loaded>();

  useEffect(() => {
    if (!api) return;
    let cancelled = false;
    (async (): Promise<Loaded> => {
      const row = await api.get<SystemFormRow>(`systemforms(${formId})?$select=systemformid,name,objecttypecode,type,formxml`);
      // Only a quick view form (type 6) of the target table is drawn.
      if (row.type !== QUICK_VIEW_FORM || row.objecttypecode !== table) throw new InvalidForm(t('record.quickView.invalidForm'));
      const form = parseFormXml(row);
      return { form, ctx: await loadRecord(api, table, id, formColumns(form), { readOnly: true }) };
    })().then(
      (result) => !cancelled && setLoaded(result),
      (e: unknown) =>
        !cancelled &&
        setLoaded({
          message: (e as Partial<InvalidForm>).invalidForm ? (e as Error).message : t('record.quickView.cannotShow', { reason: e instanceof Error ? e.message : String(e) }),
        }),
    );
    return () => {
      cancelled = true;
    };
  }, [api, table, id, formId]);

  return (
    <>
      {label && (
        <Text weight="semibold" className={styles.label}>
          {label}
        </Text>
      )}
      {!loaded ? (
        <Spinner size="tiny" label={t('record.loading')} />
      ) : 'message' in loaded ? (
        <MessageBar intent="warning">
          <MessageBarBody>{loaded.message}</MessageBarBody>
        </MessageBar>
      ) : (
        <QuickViewForm ctx={loaded.ctx} form={loaded.form} env={env} />
      )}
    </>
  );
}

/** The quick form's tabs one after another (a quick view draws no tab list), through the form renderer in read-only mode. */
function QuickViewForm({ ctx, form, env }: { ctx: RecordContext; form: FormModel; env: ControlEnv }) {
  const styles = useStyles();
  const state = useRecordForm(ctx, form, { lcid: env.lcid });
  // The host form's subgrid modes belong to the host's subgrids (ruling S1): the quick view's subgrids stay grids.
  const inner: ControlEnv = { ...env, ctx, state, currencySymbol: undefined, subgridModes: undefined };
  return (
    <div className={styles.card} data-testid="quickview">
      {form.tabs
        .filter((tab) => tab.visible)
        .map((tab, i) => (
          <FormTab key={`${tab.name}-${i}`} tab={tab} env={inner} readOnly />
        ))}
    </div>
  );
}
