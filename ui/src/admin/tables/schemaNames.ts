import { t } from '../../shell/i18n/t';

/**
 * Schema names of new tables and columns, as Dataverse takes them: the caller gives the SchemaName (publisher prefix,
 * `_`, then any casing, e.g. `xv_Project`), the LogicalName is the SchemaName in lowercase, and the table's privileges are
 * named from the SchemaName (`prvReadxv_Project`).
 *
 * The rules mirror the server and the CLI: the logical name must match `^[a-z][a-z0-9_]{0,63}$`
 * (src/Xrmd.Metadata/LogicalNameValidator.cs), and every name starts with a publisher prefix of 2 to 8 lowercase
 * letters or digits, starting with a letter, followed by `_` and at least one character (src/Xrmd.Schema/SchemaYaml.cs).
 */

const SCHEMA_NAME = /^[A-Za-z][A-Za-z0-9_]{0,63}$/;
const PREFIXED = /^[a-z][a-z0-9]{1,7}_[A-Za-z0-9_]+$/;
const PREFIX = /^[a-z][a-z0-9]{1,7}$/;

/** The LogicalName of a SchemaName: the same name in lowercase. */
export const logicalNameOf = (schemaName: string): string => schemaName.trim().toLowerCase();

/** True for a publisher prefix (`customizationprefix`) the names can start with. */
export const isPrefix = (prefix: string | undefined): prefix is string => !!prefix && PREFIX.test(prefix);

/** `Due date (UTC)` → `DueDateUTC`: each word's first letter in upper case, everything but ASCII letters and digits dropped. */
export function pascalCase(text: string): string {
  return text
    .split(/[^A-Za-z0-9]+/)
    .filter(Boolean)
    .map((word) => word[0].toUpperCase() + word.slice(1))
    .join('');
}

/** The default SchemaName for a display name: `<prefix>_<PascalCase>`; empty when there is no prefix or no usable letter. */
export function deriveSchemaName(prefix: string | undefined, displayName: string): string {
  const body = pascalCase(displayName);
  return isPrefix(prefix) && body ? `${prefix}_${body}` : '';
}

/** Why `schemaName` cannot be used, or undefined when it can. */
export function schemaNameError(schemaName: string): string | undefined {
  const name = schemaName.trim();
  if (!SCHEMA_NAME.test(name)) return t('schema.err.format');
  if (!PREFIXED.test(name)) return t('schema.err.prefix');
  return undefined;
}

/** The publisher prefix a SchemaName starts with (the text before the first `_`). */
export function prefixOf(schemaName: string): string | undefined {
  const underscore = schemaName.indexOf('_');
  const prefix = underscore > 0 ? schemaName.slice(0, underscore) : undefined;
  return isPrefix(prefix) ? prefix : undefined;
}
