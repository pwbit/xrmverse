import { Field, Text } from '@fluentui/react-components';
import { t } from '../../../shell/i18n/t';
import type { FieldProps } from './fieldProps';

/** A column the generic UI cannot edit (a non-key Uniqueidentifier, ...): shown, read-only, with its type named. Never hidden. */
export function UnsupportedField({ attr }: FieldProps) {
  return (
    <Field label={attr.displayName}>
      <Text>{t('field.unsupported', { type: attr.typeName })}</Text>
    </Field>
  );
}
