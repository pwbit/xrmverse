import { Field, Input, Select, Switch } from '@fluentui/react-components';
import type { ReactNode } from 'react';
import { t } from '../../shell/i18n/t';
import type { DesignLibrary, HandlerFields } from './formEvents';

export interface HandlerFieldsEditorProps {
  /** The function name as typed; the caller decides when it reaches the model. */
  name: string;
  onName: (value: string) => void;
  onNameBlur?: () => void;
  /** The function name's error, shown on its field. */
  nameValidation?: string;
  fields: HandlerFields;
  /** The libraries the library list offers, in order. */
  libraries: DesignLibrary[];
  /** The function name, parameters and switches cannot be edited. */
  locked: boolean;
  /** The library cannot be changed. */
  libraryLocked: boolean;
  /** What shows between the function name and the library: why a handler is locked. */
  libraryHint?: ReactNode;
  onPatch: (p: Partial<HandlerFields>) => void;
}

/**
 * The fields of a handler (G6): function name (required), library, parameters, enabled and pass execution context. Shared
 * by a handler of the list and the new-handler form.
 */
export function HandlerFieldsEditor({
  name, onName, onNameBlur, nameValidation, fields, libraries, locked, libraryLocked, libraryHint, onPatch,
}: HandlerFieldsEditorProps) {
  return (
    <>
      <Field label={t('forms.events.function')} required validationMessage={nameValidation}>
        <Input value={name} disabled={locked} onChange={(_, d) => onName(d.value)} onBlur={onNameBlur} />
      </Field>
      {libraryHint}
      <Field label={t('forms.events.library')}>
        <Select value={fields.library} disabled={libraryLocked} onChange={(_, d) => onPatch({ library: d.value })}>
          {libraries.map((l) => (
            <option key={l.key || l.name} value={l.name}>
              {l.name}
            </option>
          ))}
        </Select>
      </Field>
      <Field label={t('forms.events.parameters')}>
        <Input value={fields.parameters} disabled={locked} onChange={(_, d) => onPatch({ parameters: d.value })} />
      </Field>
      <Switch label={t('forms.events.enabled')} checked={fields.enabled} disabled={locked} onChange={(_, d) => onPatch({ enabled: d.checked })} />
      <Switch
        label={t('forms.events.passContext')}
        checked={fields.passContext}
        disabled={locked}
        onChange={(_, d) => onPatch({ passContext: d.checked })}
      />
    </>
  );
}
