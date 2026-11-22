import { BoolField } from './BoolField';
import { ChoiceField } from './ChoiceField';
import { ChoicesField } from './ChoicesField';
import { DateField } from './DateField';
import { FileField } from './FileField';
import { optionsOf, type FieldProps } from './fieldProps';
import { ImageField } from './ImageField';
import { LookupField } from './LookupField';
import { NumberField } from './NumberField';
import { PartyListField } from './PartyListField';
import { TextField } from './TextField';
import { UnsupportedField } from './UnsupportedField';

/** The editor of one attribute, by its kind. */
export function FieldEditor(p: FieldProps) {
  switch (p.attr.kind) {
    case 'string':
    case 'memo':
      return <TextField {...p} />;
    case 'int':
    case 'bigint':
    case 'decimal':
    case 'double':
    case 'money':
      return <NumberField {...p} />;
    case 'datetime':
      return <DateField {...p} />;
    case 'bool':
      return <BoolField {...p} />;
    case 'choice':
      // A choice whose options cannot be found stays visible and read-only: no guessed options.
      return optionsOf(p.attr, p.context).length > 0 ? <ChoiceField {...p} /> : <UnsupportedField {...p} />;
    case 'choices':
      return optionsOf(p.attr, p.context).length > 0 ? <ChoicesField {...p} /> : <UnsupportedField {...p} />;
    case 'lookup':
      return <LookupField {...p} />;
    case 'partylist':
      return <PartyListField {...p} />;
    case 'file':
      return <FileField {...p} />;
    case 'image':
      return <ImageField {...p} />;
    default:
      return <UnsupportedField {...p} />;
  }
}
