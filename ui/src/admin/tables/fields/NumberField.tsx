import { Field, Input } from '@fluentui/react-components';
import { useState } from 'react';
import { uiLocale } from '../../../shell/i18n/t';
import { isWholeNumber } from '../metadata';
import type { FieldProps } from './fieldProps';
import { displayNumber, editNumber, parseNumberText } from './numberText';
import { useFieldAppearance } from './fieldAppearance';

/**
 * `int`, `bigint`, `decimal`, `double` and `money` columns. Out of focus the value shows with digit grouping and the
 * column's fraction digits in the shell language; while focused it is edited without grouping, with the language's decimal
 * mark, and reported in the canonical form (`1234.5`), parsed when the form is saved.
 */
export function NumberField({ attr, value, onChange, disabled, error }: FieldProps) {
  const locale = uiLocale();
  const appearance = useFieldAppearance(disabled);
  const canonical = typeof value === 'string' ? value : typeof value === 'number' ? String(value) : '';
  // The text as typed while the field has focus; undefined out of focus.
  const [draft, setDraft] = useState<string>();
  return (
    <Field label={attr.displayName} required={attr.required} validationMessage={error}>
      <Input
        appearance={appearance}
        value={draft ?? displayNumber(canonical, attr, locale)}
        disabled={disabled}
        inputMode={isWholeNumber(attr.kind) ? 'numeric' : 'decimal'}
        onFocus={() => setDraft(editNumber(canonical, locale))}
        onBlur={() => setDraft(undefined)}
        onChange={(_, d) => {
          setDraft(d.value);
          onChange(parseNumberText(d.value, locale));
        }}
      />
    </Field>
  );
}
