import { MessageBar, MessageBarBody, MessageBarTitle, Spinner } from '@fluentui/react-components';
import { useEffect, useState, type ReactNode } from 'react';
import { XrmError } from '../shell/api/errors';
import { useAuth } from '../shell/auth/AuthProvider';
import { t } from '../shell/i18n/t';
import { listWorkflows } from './api/studioApi';

type Gate = { state: 'loading' } | { state: 'open' } | { state: 'noPrivilege'; detail: string } | { state: 'failed'; detail: string };

/**
 * The studio's own gate (no web admin role): its children are mounted once `GET /authoring/workflows` succeeds. A 403 names the
 * missing privilege (`prvReadWorkflow`); any other failure (a 5xx, a network error) shows the server message as it is.
 */
export function StudioGate({ children }: { children: ReactNode }) {
  const { api } = useAuth();
  const [gate, setGate] = useState<Gate>({ state: 'loading' });
  useEffect(() => {
    if (!api) return;
    let live = true;
    listWorkflows(api).then(
      () => live && setGate({ state: 'open' }),
      (e: unknown) => {
        if (!live) return;
        const detail = e instanceof XrmError ? e.serverMessage : e instanceof Error ? e.message : String(e);
        setGate(e instanceof XrmError && e.status === 403 ? { state: 'noPrivilege', detail } : { state: 'failed', detail });
      },
    );
    return () => {
      live = false;
    };
  }, [api]);

  if (gate.state === 'loading') return <Spinner label={t('studio.gate.loading')} />;
  if (gate.state === 'open') return <>{children}</>;
  if (gate.state === 'noPrivilege') {
    return (
      <MessageBar intent="warning">
        <MessageBarBody>
          <MessageBarTitle>{t('studio.gate.noPrivilege')}</MessageBarTitle>
          {gate.detail}
        </MessageBarBody>
      </MessageBar>
    );
  }
  return (
    <MessageBar intent="error">
      <MessageBarBody>{gate.detail}</MessageBarBody>
    </MessageBar>
  );
}
