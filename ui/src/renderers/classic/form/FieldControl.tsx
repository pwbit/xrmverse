import { Field, makeStyles, Text, tokens } from '@fluentui/react-components';
import { useRef, type FocusEvent } from 'react';
import { FieldEditor } from '../../../admin/tables/fields/FieldEditor';
import { FieldAppearanceProvider } from '../../../admin/tables/fields/fieldAppearance';
import { NoAccessField, SecuredField } from '../../../admin/tables/fields/SecuredField';
import { optionsOf } from '../../../admin/tables/fields/fieldProps';
import { sameValue, type FieldValue } from '../../../admin/tables/formValues';
import { isNumeric, recordKey, type AttrKind, type AttributeMeta } from '../../../admin/tables/metadata';
import type { Cell, FieldControl as FieldControlModel } from '../../../model/form';
import { labelFor } from '../../../parsers/formXml';
import { PcfFieldControl } from '../../../pcf/PcfFieldControl';
import { t } from '../../../shell/i18n/t';
import type { ControlEnv } from './controlEnv';
import { PARTY_TYPE_OF, type Party } from '../../../admin/tables/partyLists';
import { PartyListControl } from './PartyListControl';

const useStyles = makeStyles({
  recommendation: { display: 'block', marginBlockStart: tokens.spacingVerticalXXS, color: tokens.colorPaletteMarigoldForeground2 },
});

/** Typed columns: their OnChange runs when the user leaves the field, not on every keystroke (as in Dataverse). */
const typed = (kind: AttrKind) => kind === 'string' || kind === 'memo' || isNumeric(kind);

/** The reasons of the record's current state only (a `statuscode` option belongs to the `statecode` it names). */
function statusOptions(attr: AttributeMeta, env: ControlEnv) {
  if (attr.logicalName !== 'statuscode' || !attr.options.some((o) => o.state !== undefined)) return attr.options;
  const fromForm = env.state.values.statecode;
  const raw = typeof fromForm === 'number' ? fromForm : env.state.record?.statecode ?? env.ctx.record?.statecode;
  const current = typeof raw === 'number' ? raw : 0;
  return attr.options.filter((o) => o.state === current);
}

/**
 * A cell bound to a column: the admin field editor for its type, with the form's label (else the column's display label), the
 * required marker and the read-only state. A column the table does not have says so and leaves the rest of the form alone.
 */
export function FieldControl({ cell, control, env, forceReadOnly = false }: { cell: Cell; control: FieldControlModel; env: ControlEnv; forceReadOnly?: boolean }) {
  const styles = useStyles();
  // A typed value not yet committed: its OnChange runs when focus leaves the field, if the value differs from the one the
  // field had when it got focus.
  const uncommitted = useRef(false);
  const onFocusValue = useRef<{ value: FieldValue | undefined }>();
  const { ctx, state, lcid } = env;
  const own = env.overlay?.element(cell) ?? {};
  const column = ctx.entity.attributes.find((a) => a.logicalName === control.attribute);
  const given = labelFor(cell.labels, lcid);
  if (!column) {
    return (
      <Field label={given ?? control.attribute}>
        <Text>{t('record.field.notAvailable')}</Text>
      </Field>
    );
  }
  const label = `${own.label ?? given ?? column.displayName}${column.kind === 'money' && env.currencySymbol ? ` (${env.currencySymbol})` : ''}`;
  // A script's addOption/removeOption/clearOptions leave this control its own list of options.
  const options = own.options ?? (column.options.length > 0 ? statusOptions(column, env) : optionsOf(column, ctx.fieldContext));
  const attr: AttributeMeta = { ...column, displayName: label, required: state.isRequired(column.logicalName), options };
  const notes = env.overlay?.controlNotifications(cell) ?? [];
  const error = state.errors[column.logicalName] ?? notes.find((n) => n.level === 'ERROR')?.message;
  const recommendation = notes.find((n) => n.level === 'RECOMMENDATION')?.message;
  // A control a script disabled or enabled; else the FormXml and the record decide.
  const disabled = forceReadOnly || state.busy || (own.disabled ?? control.disabled) || state.isReadOnly(column.logicalName);
  const name = column.logicalName;
  const onChange = (v: Parameters<typeof state.setValue>[1]) => {
    if (!typed(column.kind)) return state.setValue(name, v);
    // Keystrokes change the value without OnChange (as a script's setValue does); leaving the field raises it once.
    uncommitted.current = true;
    state.setValue(name, v, 'script');
  };
  /** Focus moves from or to outside the field's wrapper (not between the parts of one editor). */
  const crossesWrapper = (e: FocusEvent<HTMLDivElement>) => !(e.relatedTarget instanceof Node && e.currentTarget.contains(e.relatedTarget));
  const entered = (e: FocusEvent<HTMLDivElement>) => {
    if (crossesWrapper(e)) onFocusValue.current = { value: state.latest().values[name] };
  };
  const left = (e: FocusEvent<HTMLDivElement>) => {
    if (!crossesWrapper(e) || !uncommitted.current) return;
    uncommitted.current = false;
    const now = state.latest().values[name];
    const before = onFocusValue.current;
    onFocusValue.current = undefined;
    if (before && sameValue(column, before.value, now)) return;
    state.setValue(name, now ?? null, 'user');
  };
  const record = state.record ?? ctx.record;
  // The server leaves out a secured column the caller may not read.
  const properties = column.kind === 'file' ? [column.logicalName, `${column.logicalName}_name`] : [recordKey(column)];
  const unreadable = column.secured && !!record && !properties.some((p) => p in record);
  // Plan 4.1b, task 9: a party list the form can write through the party navigation gets its chips and search; any other one
  // keeps the admin's read-only list.
  const partyList = column.kind === 'partylist' && PARTY_TYPE_OF[column.logicalName] !== undefined && !!ctx.fieldContext.partyNavigation;
  const value = state.values[column.logicalName];
  const editor = unreadable ? (
    <NoAccessField attr={attr} />
  ) : partyList ? (
    <PartyListControl
      key={env.refreshKey ?? 0}
      attr={attr}
      value={Array.isArray(value) ? (value as Party[]) : []}
      onChange={(v) => state.setValue(name, v)}
      disabled={disabled}
      error={error}
      sender={ctx.entity.logicalName === 'email' && name === 'from' ? env.senderPolicy : undefined}
    />
  ) : (
    <FieldEditor
      // A Refresh mounts the editors again: what was typed and not stored (a time without a date) goes with the record.
      key={env.refreshKey ?? 0}
      attr={attr}
      value={state.values[column.logicalName]}
      onChange={onChange}
      disabled={disabled}
      error={error}
      context={ctx.fieldContext}
      preSearch={env.overlay?.preSearch(cell)}
      entityTypes={own.entityTypes}
      // A memo cell spanning rows is taller: two lines per row.
      rows={column.kind === 'memo' && cell.rowSpan > 1 ? cell.rowSpan * 2 : undefined}
    />
  );
  const standard = column.secured ? <SecuredField>{editor}</SecuredField> : editor;
  // UI-1d: the PCF control the FormXml names for the web; the standard control when it cannot run (ruling R7). A column the
  // user may not read keeps the standard "no access" field.
  const custom = control.customControl;
  // The app forms draw their editable fields with a soft gray fill and no border (the owner's choice, 2026-11-22).
  return (
    <FieldAppearanceProvider value="filled-darker">
      <div onFocus={entered} onBlur={left}>
        {custom && !unreadable ? (
          <PcfFieldControl
            key={custom.name}
            cell={cell}
            control={{ ...control, customControl: custom }}
            env={env}
            column={column}
            label={label}
            required={attr.required}
            disabled={disabled}
            error={error}
            fallback={standard}
          />
        ) : (
          standard
        )}
        {recommendation && (
          <Text size={200} role="note" className={styles.recommendation}>
            {recommendation}
          </Text>
        )}
      </div>
    </FieldAppearanceProvider>
  );
}
