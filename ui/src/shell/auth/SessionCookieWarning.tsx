import { MessageBar, MessageBarBody, MessageBarTitle } from '@fluentui/react-components';
import { t } from '../i18n/t';
import { useAuth } from './AuthProvider';

/**
 * The shell's warning while the web session cookie could not be set: form libraries and web resources are loaded with that
 * cookie, so without it they fail. Shows the server's reason; disappears once a post succeeds.
 */
export function SessionCookieWarning() {
  const { sessionCookieProblem: problem } = useAuth();
  if (!problem) return null;
  return (
    <MessageBar intent="warning" layout="multiline">
      <MessageBarBody>
        <MessageBarTitle>{t('auth.sessionCookie.title')}</MessageBarTitle>
        {t(problem.tooLong ? 'auth.sessionCookie.tooLong' : 'auth.sessionCookie.failed', { message: problem.message })}
      </MessageBarBody>
    </MessageBar>
  );
}
