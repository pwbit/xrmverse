import { makeStyles, MessageBar, MessageBarBody, Spinner } from '@fluentui/react-components';
import { isWebResourceName, webResourceSrc } from '../client-api/scriptHost';
import { useWebResourceCheck } from '../client-api/useWebResourceCheck';
import { t } from '../shell/i18n/t';

const useStyles = makeStyles({
  frame: { display: 'block', minHeight: '70vh', border: 'none' },
});

export interface WebResourcePageProps {
  /** `webresourceName` of the page URL: the full web resource name. */
  name: string;
  /** `data` of the page URL. */
  data?: string;
}

/**
 * `pagetype=webresource`: the HTML web resource full size inside the app shell, with `data` in its query string. The server
 * answers 404 for a resource that does not exist and for one the caller may not read; both show the same message. A name that
 * would leave `/WebResources/` is refused without a request.
 */
export function WebResourcePage({ name, data }: WebResourcePageProps) {
  const styles = useStyles();
  const valid = isWebResourceName(name);
  const src = valid ? webResourceSrc(name, data !== undefined ? { data } : {}) : undefined;
  const check = useWebResourceCheck(src);
  if (!valid) {
    return (
      <MessageBar intent="error">
        <MessageBarBody>{t('webresource.invalidName', { name })}</MessageBarBody>
      </MessageBar>
    );
  }
  if (check.status === 'checking') return <Spinner label={t('common.loading')} />;
  if (check.status === 'missing' || check.status === 'error') {
    return (
      <MessageBar intent={check.status === 'missing' ? 'warning' : 'error'}>
        <MessageBarBody>{check.status === 'missing' ? t('webresource.notFound', { name }) : t('apps.error', { detail: check.detail })}</MessageBarBody>
      </MessageBar>
    );
  }
  return <iframe className={styles.frame} style={{ width: '100%', height: '100%' }} src={src} title={t('xrm.webResource.frame', { name })} />;
}
