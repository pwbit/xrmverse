import { Field, Select } from '@fluentui/react-components';
import { t } from '../../../shell/i18n/t';
import { optionsOf, type FieldProps } from './fieldProps';

/** `choice` columns (and state/status): one option by its label. */
export function ChoiceField(p: FieldProps) {
  const options = optionsOf(p.attr, p.context);
  return (
    <Field label={p.attr.displayName} required={p.attr.required} validationMessage={p.error}>
      <Select
        value={typeof p.value === 'number' ? String(p.value) : ''}
        disabled={p.disabled}
        onChange={(_, d) => p.onChange(d.value === '' ? null : Number(d.value))}
      >
        <option value="">{t('field.empty')}</option>
        {options.map((o) => (
          <option key={o.value} value={o.value}>
            {o.label}
          </option>
        ))}
      </Select>
    </Field>
  );
}
