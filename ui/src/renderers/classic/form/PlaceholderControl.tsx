import { MessageBar, MessageBarBody } from '@fluentui/react-components';
import { t } from '../../../shell/i18n/t';

/** A control that is not rendered in this version, shown as an explicit message in its cell (Global Constraints, "Non-field controls"). */
export function PlaceholderControl() {
  return (
    <MessageBar intent="info">
      <MessageBarBody>{t('record.control.unavailable')}</MessageBarBody>
    </MessageBar>
  );
}
