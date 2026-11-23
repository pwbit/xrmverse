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
} from '@fluentui/react-components';
import { useState } from 'react';
import { t } from '../../shell/i18n/t';
import type { Mailbox, Profile } from './emailApi';

/**
 * Links a mailbox to an email server profile (plan 4.1b, task 8): a choice of the active profiles, the mailbox's current one first
 * selected. Saving sends the profile, Server-Side Synchronization and outgoing email on; the server's refusal is shown in the dialog.
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
  onSave: (profileId: string) => void;
}) {
  const choices = profiles.filter((p) => p.active || p.id === mailbox.profileId);
  const [profileId, setProfileId] = useState(mailbox.profileId ?? choices[0]?.id ?? '');
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
          </DialogContent>
          <DialogActions>
            <Button appearance="secondary" onClick={onClose}>
              {t('common.cancel')}
            </Button>
            <Button appearance="primary" disabled={!profileId || busy} onClick={() => onSave(profileId)}>
              {t('email.link.save')}
            </Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}
