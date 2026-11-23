import { Link } from '@fluentui/react-components';
import { useEffect, useState } from 'react';
import { useAuth } from '../../shell/auth/AuthProvider';
import { t } from '../../shell/i18n/t';
import { PageHeader } from '../../shell/layout/PageHeader';

const LICENSE_URL = 'https://www.apache.org/licenses/LICENSE-2.0';
const SECURITY_URL = 'https://github.com/pwbit/XRMverse/blob/main/SECURITY.md';

/** The part of `GET /admin/health` this page reads: `sdkVersion` is null when `sdkMode` is `none`. */
interface SdkHealth {
  sdkMode?: string;
  sdkVersion?: string | null;
}

export function AboutPage() {
  const { api } = useAuth();
  const [sdk, setSdk] = useState<SdkHealth>();

  useEffect(() => {
    if (!api) return;
    let live = true;
    api
      .admin<SdkHealth>('GET', 'health', undefined, { acceptStatus: [503] })
      .then((h) => live && setSdk(h))
      .catch(() => {
        // The line is informational: without a health report the page simply omits it.
      });
    return () => {
      live = false;
    };
  }, [api]);

  const sdkLine = !sdk
    ? undefined
    : sdk.sdkMode === 'none'
      ? t('about.sdkNone')
      : sdk.sdkVersion
        ? t('about.sdk', { version: sdk.sdkVersion })
        : undefined;

  return (
    <section>
      <PageHeader title={t('about.title')} />
      <p>{t('about.notice')}</p>
      {sdkLine && <p>{sdkLine}</p>}
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
