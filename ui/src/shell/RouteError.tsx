import { Body1, Button, Title2 } from '@fluentui/react-components';
import { useRouteError } from 'react-router-dom';
import { t } from './i18n/t';

/** What a render error inside the app shows, instead of the router's default screen. */
export function RouteError() {
  const error = useRouteError();
  const detail = error instanceof Error ? error.message : typeof error === 'string' ? error : t('app.error.unknown');
  return (
    <section role="alert">
      <Title2 as="h1">{t('app.error.title')}</Title2>
      <Body1 block>{t('app.error.body', { detail })}</Body1>
      <Button appearance="primary" onClick={() => window.location.reload()}>
        {t('app.error.reload')}
      </Button>
    </section>
  );
}
