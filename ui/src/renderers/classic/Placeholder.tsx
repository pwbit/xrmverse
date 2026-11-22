import { MessageBar, MessageBarBody } from '@fluentui/react-components';
import { t } from '../../shell/i18n/t';

const KNOWN = {
  custom: 'apps.placeholder.custom',
} as const;

/** The explicit "{what} opens in stage {stage}." message of a page type this version does not render. */
export function placeholderText(pageType: string): string {
  const key = Object.prototype.hasOwnProperty.call(KNOWN, pageType) ? KNOWN[pageType as keyof typeof KNOWN] : undefined;
  return key ? t(key) : t('apps.placeholder.other', { pageType });
}

export function Placeholder({ pageType }: { pageType: string }) {
  return (
    <MessageBar intent="info">
      <MessageBarBody>{placeholderText(pageType)}</MessageBarBody>
    </MessageBar>
  );
}
