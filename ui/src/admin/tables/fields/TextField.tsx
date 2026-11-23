import { Field, Input, Textarea } from '@fluentui/react-components';
import type { FieldProps } from './fieldProps';
import { useFieldAppearance } from './fieldAppearance';

/** `string` and `memo` columns. */
export function TextField({ attr, value, onChange, disabled, error, rows }: FieldProps) {
  const appearance = useFieldAppearance(disabled);
  const text = typeof value === 'string' ? value : '';
  return (
    <Field label={attr.displayName} required={attr.required} validationMessage={error}>
      {attr.kind === 'memo' ? (
        <Textarea appearance={appearance} resize="vertical" rows={rows} value={text} disabled={disabled} maxLength={attr.maxLength} onChange={(_, d) => onChange(d.value)} />
      ) : (
        <Input appearance={appearance} value={text} disabled={disabled} maxLength={attr.maxLength} onChange={(_, d) => onChange(d.value)} />
      )}
    </Field>
  );
}
