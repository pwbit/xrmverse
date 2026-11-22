import { Field, Input, Textarea } from '@fluentui/react-components';
import type { FieldProps } from './fieldProps';

/** `string` and `memo` columns. */
export function TextField({ attr, value, onChange, disabled, error }: FieldProps) {
  const text = typeof value === 'string' ? value : '';
  return (
    <Field label={attr.displayName} required={attr.required} validationMessage={error}>
      {attr.kind === 'memo' ? (
        <Textarea resize="vertical" value={text} disabled={disabled} maxLength={attr.maxLength} onChange={(_, d) => onChange(d.value)} />
      ) : (
        <Input value={text} disabled={disabled} maxLength={attr.maxLength} onChange={(_, d) => onChange(d.value)} />
      )}
    </Field>
  );
}
