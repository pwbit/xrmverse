import { Checkbox, Label, Text, useId } from '@fluentui/react-components';
import { optionsOf, type FieldProps } from './fieldProps';

/** `choices` (multi-select) columns: one checkbox per option, in a group named by the column (not a Field: it labels one control). */
export function ChoicesField(p: FieldProps) {
  const labelId = useId('choices');
  const selected = Array.isArray(p.value) ? p.value : [];
  return (
    <div role="group" aria-labelledby={labelId}>
      <Label id={labelId} required={p.attr.required}>
        {p.attr.displayName}
      </Label>
      {optionsOf(p.attr, p.context).map((o) => (
        <Checkbox
          key={o.value}
          label={o.label}
          disabled={p.disabled}
          checked={selected.includes(o.value)}
          onChange={(_, d) => p.onChange(d.checked ? [...selected, o.value].sort((a, b) => a - b) : selected.filter((v) => v !== o.value))}
        />
      ))}
      {p.error && <Text role="alert">{p.error}</Text>}
    </div>
  );
}
