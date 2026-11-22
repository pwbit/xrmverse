import { Field, Input } from '@fluentui/react-components';
import { useState } from 'react';
import { t } from '../../shell/i18n/t';
import { deriveSchemaName, logicalNameOf, schemaNameError } from './schemaNames';
import { loadDefaultPrefix } from './tableApi';
import { useLoad } from './useLoad';

export interface SchemaName {
  /** The SchemaName to send (trimmed); empty until there is one. */
  schemaName: string;
  /** Its LogicalName (lowercase). */
  logicalName: string;
  /** Why the SchemaName cannot be used; undefined when it can (or is still empty). */
  error?: string;
  /** The prefix of the default publisher, once read. */
  prefix?: string;
  /** Why the default prefix could not be read. */
  prefixError?: string;
  /** The text in the Schema name field. */
  value: string;
  setValue: (text: string) => void;
}

/**
 * The SchemaName of a new table or column. Until the user edits it, it follows the display name:
 * `<prefix of the default publisher>_<PascalCase display name>`.
 */
export function useSchemaName(displayName: string): SchemaName {
  const prefix = useLoad('defaultPrefix', loadDefaultPrefix);
  const [edited, setEdited] = useState<string>();
  const value = edited ?? deriveSchemaName(prefix.data, displayName);
  const schemaName = value.trim();
  return {
    schemaName,
    logicalName: logicalNameOf(schemaName),
    error: schemaName ? schemaNameError(schemaName) : undefined,
    prefix: prefix.data,
    prefixError: prefix.error,
    value,
    setValue: setEdited,
  };
}

/** The editable Schema name and the read-only Logical name derived from it. */
export function SchemaNameFields({ name, scope }: { name: SchemaName; scope: 'table' | 'column' }) {
  const message = name.error ?? (name.prefixError ? t('schema.prefixFailed', { detail: name.prefixError }) : undefined);
  return (
    <>
      <Field
        label={t(`${scope}.field.schemaName`)}
        required
        hint={t(`${scope}.field.schemaName.hint`)}
        validationMessage={message}
        validationState={name.error ? 'error' : name.prefixError ? 'warning' : undefined}
      >
        <Input value={name.value} onChange={(_, d) => name.setValue(d.value)} />
      </Field>
      <Field label={t(`${scope}.field.name`)} hint={t(`${scope}.field.name.hint`)}>
        <Input value={name.logicalName} readOnly />
      </Field>
    </>
  );
}
