import { Button, Field, Input, MessageBar, MessageBarBody, Text } from '@fluentui/react-components';
import { useState, type FormEvent } from 'react';
import { t } from '../i18n/t';
import { loadDevCredentials, type DevCredentials } from './devToken';

/** The seed administrator of `docker/.env.example`; a convenience default for the object id field. */
const SEED_ADMIN_OBJECT_ID = '00000000-0000-0000-0000-000000000001';

export function DevSignIn({ onSubmit }: { onSubmit: (credentials: DevCredentials) => void }) {
  const [initial] = useState(loadDevCredentials);
  const [signingKey, setSigningKey] = useState(initial?.signingKey ?? '');
  const [objectId, setObjectId] = useState(initial?.objectId ?? SEED_ADMIN_OBJECT_ID);
  const [name, setName] = useState(initial?.name ?? t('auth.dev.defaultName'));
  const [invalid, setInvalid] = useState(false);

  function submit(e: FormEvent) {
    e.preventDefault();
    if (!signingKey.trim() || !objectId.trim()) return setInvalid(true);
    setInvalid(false);
    onSubmit({ signingKey: signingKey.trim(), objectId: objectId.trim(), name: name.trim() || t('auth.dev.defaultName') });
  }

  return (
    <form onSubmit={submit} style={{ display: 'grid', gap: 12 }}>
      <Text>{t('auth.dev.intro')}</Text>
      <Field label={t('auth.dev.signingKey')}>
        <Input type="password" value={signingKey} onChange={(_, d) => setSigningKey(d.value)} autoComplete="off" />
      </Field>
      <Field label={t('auth.dev.objectId')}>
        <Input value={objectId} onChange={(_, d) => setObjectId(d.value)} />
      </Field>
      <Field label={t('auth.dev.name')}>
        <Input value={name} onChange={(_, d) => setName(d.value)} />
      </Field>
      {invalid && (
        <MessageBar intent="error">
          <MessageBarBody>{t('auth.dev.invalid')}</MessageBarBody>
        </MessageBar>
      )}
      <Button appearance="primary" type="submit">
        {t('auth.dev.submit')}
      </Button>
    </form>
  );
}
