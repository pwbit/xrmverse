import { createElement } from 'react';
import type { FormValues } from '../../admin/tables/formValues';
import type { Party } from '../../admin/tables/partyLists';
import type { Command } from '../../renderers/classic/form/CommandBar';
import type { XrmApi } from '../../shell/api/client';
import { t } from '../../shell/i18n/t';
import { SendIcon } from '../../shell/icons';
import type { RecordContext } from '../record/loadRecord';

/** Email `statecode` Open (Draft and Failed); Completed is Sent or Pending Send (plan 4.1b, global constraints). */
export const EMAIL_OPEN = 0;
export const EMAIL_COMPLETED = 1;
/** Email `statuscode` values. */
export const EMAIL_STATUS = { draft: 1, sent: 3, pendingSend: 6, failed: 8 } as const;

/** The key of the form notification a send writes (its warning or the server's refusal). */
export const SEND_NOTIFICATION = 'xrmverse.email.send';

const RECIPIENTS = ['to', 'cc', 'bcc'] as const;

/**
 * The form's **Send** (plan 4.1b, task 9), first in the command bar: on an existing email of the `email` table that is open
 * (`statecode` 0, as the form last read it) and that the user may write. A new email has no Send until it is saved; a sent or
 * sending one has none either.
 */
export function emailSendCommands({ ctx, record, busy, onSend }: { ctx: RecordContext; record: Record<string, unknown> | undefined; busy: boolean; onSend: () => void }): Command[] {
  if (ctx.entity.logicalName !== 'email' || !ctx.record || !record || !ctx.rights.includes('Write')) return [];
  if (record.statecode !== EMAIL_OPEN) return [];
  return [{ id: 'email:send', icon: createElement(SendIcon), label: t('email.cmd.send'), disabled: busy, onClick: onSend }];
}

/** The addresses among To, Cc and Bcc that name no record (unresolved recipients). */
export function unresolvedRecipients(values: FormValues): string[] {
  return RECIPIENTS.flatMap((column) => {
    const parties = values[column];
    return Array.isArray(parties) ? (parties as Party[]).filter((p) => !p.id && p.address).map((p) => p.address!) : [];
  });
}

/** `organization.allowunresolvedpartiesonemailsend` (off by default, as in Dataverse). */
async function allowsUnresolved(api: XrmApi): Promise<boolean> {
  const result = await api.get<{ value: { allowunresolvedpartiesonemailsend?: boolean | null }[] }>('organizations?$select=allowunresolvedpartiesonemailsend');
  return result.value[0]?.allowunresolvedpartiesonemailsend === true;
}

export interface SendDeps {
  api: XrmApi;
  /** The email's id. */
  id: string;
  /** The form's save (its OnSave, the required check); false when it did not save. */
  save: () => Promise<boolean>;
  /** The form's values once saved. */
  values: () => FormValues;
  /** Writes the send's form notification (an error). */
  notify: (text: string) => void;
  /** Clears it. */
  clear: () => void;
  /** Reads the email again (Pending Send makes the form read-only). */
  reload: () => void;
  /** True while a save of the form runs (an auto-save): Send waits for it, since a second save would not run. */
  saving?: () => boolean;
  /** How long Send waits for that save (30 s by default). */
  savingTimeoutMs?: number;
}

/** Waits until no save runs; false when one still runs after `timeoutMs`. */
async function saveEnded(saving: () => boolean, timeoutMs: number): Promise<boolean> {
  const deadline = Date.now() + timeoutMs;
  while (saving()) {
    if (Date.now() >= deadline) return false;
    await new Promise((r) => setTimeout(r, 50));
  }
  return true;
}

/**
 * Send: the form is saved first, then `SendEmail` runs with `IssueSend` true; the email reads again as Pending Send. With
 * unresolved recipients the form warns and sends nothing when the organization refuses them (the server would answer 400). A
 * refusal of the server (the sender policy, a queue the user cannot read) shows its message in the form notification area.
 * True when the send was queued.
 */
export async function sendEmail(deps: SendDeps): Promise<boolean> {
  deps.clear();
  if (deps.saving && !(await saveEnded(deps.saving, deps.savingTimeoutMs ?? 30_000))) {
    deps.notify(t('email.send.stillSaving'));
    return false;
  }
  if (!(await deps.save())) return false;
  try {
    const unresolved = unresolvedRecipients(deps.values());
    if (unresolved.length > 0 && !(await allowsUnresolved(deps.api))) {
      deps.notify(t('email.send.unresolved', { addresses: unresolved.join(', ') }));
      return false;
    }
    await deps.api.post(`emails(${deps.id})/Microsoft.Dynamics.CRM.SendEmail`, { IssueSend: true });
  } catch (e) {
    deps.notify(t('email.send.failed', { detail: e instanceof Error ? e.message : String(e) }));
    return false;
  }
  deps.reload();
  return true;
}
