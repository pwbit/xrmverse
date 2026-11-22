import { Field, Select } from '@fluentui/react-components';
import { t } from '../../../shell/i18n/t';
import type { FieldProps } from './fieldProps';
import { useFieldAppearance } from './fieldAppearance';

/** `bool` columns: yes, no, or empty (a Dataverse boolean can be null). Labels are the column's own True/False options. */
export function BoolField({ attr, value, onChange, disabled, error }: FieldProps) {
  const appearance = useFieldAppearance(disabled);
  const label = (v: number, fallback: string) => attr.options.find((o) => o.value === v)?.label ?? fallback;
  return (
    <Field label={attr.displayName} required={attr.required} validationMessage={error}>
      <Select
        appearance={appearance}
        value={value === true ? 'true' : value === false ? 'false' : ''}
        disabled={disabled}
        onChange={(_, d) => onChange(d.value === '' ? null : d.value === 'true')}
      >
        <option value="">{t('field.empty')}</option>
        <option value="true">{label(1, t('field.bool.yes'))}</option>
        <option value="false">{label(0, t('field.bool.no'))}</option>
      </Select>
    </Field>
  );
}
