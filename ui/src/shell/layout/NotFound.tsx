import { Body1 } from '@fluentui/react-components';
import { t } from '../i18n/t';
import { PageHeader } from './PageHeader';

export function NotFound() {
  return (
    <section>
      <PageHeader title={t('route.notFound.title')} />
      <Body1 block>{t('route.notFound.body')}</Body1>
    </section>
  );
}
