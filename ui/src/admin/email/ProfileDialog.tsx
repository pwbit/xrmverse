import {
  Button,
  Checkbox,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  Field,
  Input,
  MessageBar,
  MessageBarBody,
  Select,
} from '@fluentui/react-components';
import { useState } from 'react';
import { t } from '../../shell/i18n/t';
import { kindOf, secretSet, type Profile, type ProfileKind, type ProfileValues } from './emailApi';

/**
 * Creates or edits an email server profile: SMTP (server, port, TLS, and the user name and password in the profile or each
 * mailbox's) or Microsoft Graph (tenant, application id, client secret). The secret is write-only: the field starts empty, says
 * whether one is set, and is sent only when something is typed. The server validates the profile; its refusal is shown as it is
 * inside the dialog, which stays open with what was typed.
 */
export function ProfileDialog({
  profile,
  busy,
  error,
  onClose,
  onSave,
}: {
  profile?: Profile;
  busy: boolean;
  error?: string;
  onClose: () => void;
  onSave: (values: ProfileValues) => void;
}) {
  const [kind, setKind] = useState<ProfileKind>((profile && kindOf(profile)) ?? 'smtp');
  const [name, setName] = useState(profile?.name ?? '');
  const [server, setServer] = useState(profile?.server ?? '');
  const [port, setPort] = useState(profile?.port == null ? '' : String(profile.port));
  const [ssl, setSsl] = useState(profile?.ssl ?? false);
  const [userName, setUserName] = useState(profile?.userName ?? '');
  const [inProfile, setInProfile] = useState(profile ? profile.credentials !== 0 : true);
  const [tenant, setTenant] = useState(profile?.tenant ?? '');
  const [clientId, setClientId] = useState(profile?.clientId ?? '');
  const [secret, setSecret] = useState('');
  const [portError, setPortError] = useState<string>();

  // The stored secret is the one of the stored kind; a profile that changes kind has none yet.
  const stored = !!profile && kindOf(profile) === kind && secretSet(profile);
  const secretHint = stored ? t('email.profile.secretSet') : t('email.profile.secretNotSet');
  const save = () => {
    // An SMTP port is empty (the default) or a whole number; anything else is refused here rather than sent as no port.
    if (kind === 'smtp' && port.trim() !== '' && !/^\d+$/.test(port.trim())) {
      setPortError(t('email.profile.portInvalid', { value: port }));
      return;
    }
    setPortError(undefined);
    onSave({ name, kind, server, port, ssl, userName, inProfile, tenant, clientId, secret });
  };

  return (
    <Dialog open onOpenChange={(_, d) => !d.open && onClose()}>
      <DialogSurface>
        <DialogBody>
          <DialogTitle>{profile ? t('email.profile.editTitle', { name: profile.name }) : t('email.profile.newTitle')}</DialogTitle>
          <DialogContent>
            {error && (
              <MessageBar intent="error">
                <MessageBarBody>{error}</MessageBarBody>
              </MessageBar>
            )}
            <Field label={t('email.profile.name')} required>
              <Input value={name} onChange={(_, d) => setName(d.value)} />
            </Field>
            <Field label={t('email.profile.type')}>
              <Select value={kind} onChange={(_, d) => setKind(d.value as ProfileKind)}>
                <option value="smtp">{t('email.kind.smtp')}</option>
                <option value="graph">{t('email.kind.graph')}</option>
              </Select>
            </Field>
            {kind === 'smtp' ? (
              <>
                <Field label={t('email.profile.server')} required>
                  <Input value={server} onChange={(_, d) => setServer(d.value)} />
                </Field>
                <Field
                  label={t('email.profile.port')}
                  hint={t('email.profile.portHint')}
                  validationState={portError ? 'error' : 'none'}
                  validationMessage={portError}
                >
                  <Input value={port} inputMode="numeric" onChange={(_, d) => setPort(d.value)} />
                </Field>
                <Checkbox label={t('email.profile.ssl')} checked={ssl} onChange={(_, d) => setSsl(d.checked === true)} />
                <Field label={t('email.profile.credentials')}>
                  <Select value={inProfile ? 'profile' : 'mailbox'} onChange={(_, d) => setInProfile(d.value === 'profile')}>
                    <option value="profile">{t('email.credentials.profile')}</option>
                    <option value="mailbox">{t('email.credentials.mailbox')}</option>
                  </Select>
                </Field>
                {inProfile && (
                  <>
                    <Field label={t('email.profile.userName')}>
                      <Input value={userName} onChange={(_, d) => setUserName(d.value)} />
                    </Field>
                    <Field label={t('email.profile.password')} hint={secretHint}>
                      <Input type="password" autoComplete="new-password" value={secret} onChange={(_, d) => setSecret(d.value)} />
                    </Field>
                  </>
                )}
              </>
            ) : (
              <>
                <Field label={t('email.profile.tenant')} required>
                  <Input value={tenant} onChange={(_, d) => setTenant(d.value)} />
                </Field>
                <Field label={t('email.profile.clientId')} required>
                  <Input value={clientId} onChange={(_, d) => setClientId(d.value)} />
                </Field>
                <Field label={t('email.profile.clientSecret')} hint={secretHint}>
                  <Input type="password" autoComplete="new-password" value={secret} onChange={(_, d) => setSecret(d.value)} />
                </Field>
              </>
            )}
          </DialogContent>
          <DialogActions>
            <Button appearance="secondary" onClick={onClose}>
              {t('common.cancel')}
            </Button>
            <Button appearance="primary" disabled={!name.trim() || busy} onClick={save}>
              {t('email.profile.save')}
            </Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}
