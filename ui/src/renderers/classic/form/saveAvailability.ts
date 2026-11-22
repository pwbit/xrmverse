import type { RecordContext } from '../../../apps/record/loadRecord';
import type { RecordForm } from '../../../apps/record/useRecordForm';

/**
 * Whether the form's Save is drawn and whether it is disabled: the command bar's Save and Save & Close, and the wizard's Save
 * (UI-2c) follow the same rules. Hidden, not disabled, when the user may not write the record or create one (B7); disabled
 * while nothing changed or a request runs.
 */
export function saveAvailability(ctx: Pick<RecordContext, 'record' | 'canCreate'>, state: Pick<RecordForm, 'readOnly' | 'dirty' | 'busy'>): { shown: boolean; disabled: boolean } {
  return { shown: !state.readOnly && (!!ctx.record || ctx.canCreate), disabled: !state.dirty || state.busy };
}
