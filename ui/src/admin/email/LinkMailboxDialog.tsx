import {
  Button,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  Field,
  MessageBar,
  MessageBarBody,
  Select,
  Switch,
} from '@fluentui/react-components';
import { useState } from 'react';
import { t } from '../../shell/i18n/t';
import { receivesEmail, type Mailbox, type Profile } from './emailApi';

/**
 * Links a mailbox to an email server profile (plan 4.1b task 8, plan 4.1c task 10): a choice of the active profiles, the mailbox's
 * current one first selected, and a Receive email switch that starts from what the mailbox does today. Saving sends the profile,
 * Server-Side Synchronization and outgoing email on, and the incoming side as the switch says; the server's refusal is shown in the
 * dialog.
 */
export function LinkMailboxDialog({
  mailbox,
  mailboxName,
  profiles,
  busy,
  error,
  onClose,
  onSave,
}: {
  mailbox: Mailbox;
  mailboxName: string;
  profiles: Profile[];
  busy: boolean;
  error?: string;
  onClose: () => void;
  /** `incoming` is undefined while the Receive email switch is untouched: the mailbox's incoming columns are then left alone. */
  onSave: (profileId: string, incoming?: boolean) => void;
}) {
  const choices = profiles.filter((p) => p.active || p.id === mailbox.profileId);
  const [profileId, setProfileId] = useState(mailbox.profileId ?? choices[0]?.id ?? '');
  const [incoming, setIncoming] = useState<boolean>();
  const receives = incoming ?? receivesEmail(mailbox);
  return (
    <Dialog open onOpenChange={(_, d) => !d.open && onClose()}>
      <DialogSurface>
        <DialogBody>
          <DialogTitle>{t('email.link.title', { name: mailboxName })}</DialogTitle>
          <DialogContent>
            {error && (
              <MessageBar intent="error">
                <MessageBarBody>{error}</MessageBarBody>
              </MessageBar>
            )}
            {choices.length === 0 ? (
              <p>{t('email.link.noProfile')}</p>
            ) : (
              <Field label={t('email.link.profile')} hint={t('email.link.hint')}>
                <Select value={profileId} onChange={(_, d) => setProfileId(d.value)}>
                  {choices.map((p) => (
                    <option key={p.id} value={p.id}>
                      {p.name}
                    </option>
                  ))}
                </Select>
              </Field>
            )}
            <Field hint={t('email.link.incomingHint')}>
              <Switch label={t('email.link.incoming')} checked={receives} disabled={busy} onChange={(_, d) => setIncoming(d.checked)} />
            </Field>
          </DialogContent>
          <DialogActions>
            <Button appearance="secondary" onClick={onClose}>
              {t('common.cancel')}
            </Button>
            <Button appearance="primary" disabled={!profileId || busy} onClick={() => onSave(profileId, incoming)}>
              {t('email.link.save')}
            </Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}
