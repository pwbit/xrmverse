import { MessageBar, MessageBarBody } from '@fluentui/react-components';
import { t } from '../../shell/i18n/t';

/** A list of the form designer that failed to load: the server's text, verbatim, in an error bar. Nothing without an error. */
export function LoadFailed({ error }: { error?: string }) {
  if (error === undefined) return null;
  return (
    <MessageBar intent="error">
      <MessageBarBody>{t('forms.controls.loadFailed', { detail: error })}</MessageBarBody>
    </MessageBar>
  );
}
