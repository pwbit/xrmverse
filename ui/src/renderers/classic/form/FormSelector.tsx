import { Field, Select } from '@fluentui/react-components';
import type { FormModel } from '../../../model/form';
import { t } from '../../../shell/i18n/t';

export interface FormSelectorProps {
  forms: FormModel[];
  current: FormModel;
  onSelect: (form: FormModel) => void;
}

/** Picks one of the table's forms. A table with a single form shows no selector. */
export function FormSelector({ forms, current, onSelect }: FormSelectorProps) {
  if (forms.length < 2) return null;
  return (
    <Field label={t('record.formSelector')} orientation="horizontal">
      <Select
        value={current.id}
        onChange={(_, d) => {
          const next = forms.find((f) => f.id === d.value);
          if (next) onSelect(next);
        }}
      >
        {forms.map((f) => (
          <option key={f.id} value={f.id}>
            {f.name}
          </option>
        ))}
      </Select>
    </Field>
  );
}
