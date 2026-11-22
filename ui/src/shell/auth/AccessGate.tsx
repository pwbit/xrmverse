import { Button, Card, makeStyles, MessageBar, MessageBarBody, MessageBarTitle, Spinner, Subtitle1, Text, Title1 } from '@fluentui/react-components';
import type { ReactNode } from 'react';
import { t } from '../i18n/t';
import { BrandBar } from '../layout/BrandBar';
import { useAuth } from './AuthProvider';
import { DevSignIn } from './DevSignIn';
import { AccessContext, useLoadAccess } from './roles';

/**
 * Renders its children only for a signed-in System Administrator or System Customizer. No data request is issued
 * by the gate's children before the level is known: they are not mounted until then. With `anyUser` (the
 * model-driven apps), any signed-in user gets the children, which decide what the user may see.
 */
const useStyles = makeStyles({
  page: { minHeight: '100vh', display: 'flex', flexDirection: 'column' },
  card: { width: 'min(480px, calc(100% - 32px))', margin: '48px auto', padding: '24px', boxSizing: 'border-box', rowGap: '16px' },
  title: { margin: 0 },
});

export function AccessGate({ children, anyUser = false }: { children: ReactNode; anyUser?: boolean }) {
  const auth = useAuth();
  if (auth.status === 'signingIn') return <Spinner label={t('auth.signIn.working')} />;
  if (auth.status === 'error') {
    return (
      <MessageBar intent="error">
        <MessageBarBody>{auth.error}</MessageBarBody>
      </MessageBar>
    );
  }
  if (auth.status === 'signedOut') return <SignInPage auth={auth} />;
  if (anyUser) return <>{children}</>;
  return <SignedInGate>{children}</SignedInGate>;
}

function SignedInGate({ children }: { children: ReactNode }) {
  const access = useLoadAccess();
  if (access.level === 'loading') return <Spinner label={t('access.loading')} />;
  if (access.level === 'error') {
    return (
      <MessageBar intent="error">
        <MessageBarBody>{t('access.error', { detail: access.error?.message ?? '' })}</MessageBarBody>
      </MessageBar>
    );
  }
  if (access.level === 'denied') return <AccessDenied detail={access.error?.message} />;
  return <AccessContext.Provider value={access}>{children}</AccessContext.Provider>;
}

function AccessDenied({ detail }: { detail?: string }) {
  const { signOut } = useAuth();
  return (
    <section>
      <MessageBar intent="warning">
        <MessageBarBody>
          <MessageBarTitle>{t('access.denied.title')}</MessageBarTitle>
          {t('access.denied.body')}
        </MessageBarBody>
      </MessageBar>
      {detail && <Text block>{detail}</Text>}
      <Button onClick={signOut}>{t('auth.signOut')}</Button>
    </section>
  );
}

/** The signed-out page: the brand bar, then a card with the product name, the title and the sign-in action. */
function SignInPage({ auth }: { auth: ReturnType<typeof useAuth> }) {
  const styles = useStyles();
  return (
    <div className={styles.page}>
      <BrandBar />
      <main>
        <Card className={styles.card}>
          <Title1 as="h1" block className={styles.title}>
            {t('app.name')}
          </Title1>
          <Subtitle1 as="h2" block className={styles.title}>
            {t('auth.signIn.title')}
          </Subtitle1>
          {auth.expired && (
            <MessageBar intent="warning">
              <MessageBarBody>{t('auth.sessionExpired')}</MessageBarBody>
            </MessageBar>
          )}
          {auth.config?.authMode === 'Entra' || auth.config?.authMode === 'Local' ? (
            <Button appearance="primary" onClick={() => auth.signIn()}>
              {t(auth.config.authMode === 'Local' ? 'auth.signIn.local' : 'auth.signIn.entra')}
            </Button>
          ) : (
            <DevSignIn onSubmit={auth.signInDev} />
          )}
        </Card>
      </main>
    </div>
  );
}
