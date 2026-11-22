import { Link } from '@fluentui/react-components';
import { t } from '../../shell/i18n/t';
import { PageHeader } from '../../shell/layout/PageHeader';

const LICENSE_URL = 'https://www.apache.org/licenses/LICENSE-2.0';
const SECURITY_URL = 'https://github.com/pwbit/XRMverse/blob/main/SECURITY.md';

export function AboutPage() {
  return (
    <section>
      <PageHeader title={t('about.title')} />
      <p>{t('about.notice')}</p>
      <p>{t('about.license')}</p>
      <p>
        <Link href={LICENSE_URL} target="_blank" rel="noopener noreferrer">
          {t('about.licenseLink')}
        </Link>
      </p>
      <p>{t('about.thirdParty')}</p>
      <p>
        <Link href={SECURITY_URL} target="_blank" rel="noopener noreferrer">
          {t('about.security')}
        </Link>
      </p>
    </section>
  );
}
