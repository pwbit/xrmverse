import { MessageBar, MessageBarBody, makeStyles, tokens } from '@fluentui/react-components';
import { t } from '../../../shell/i18n/t';

export interface FormNotification {
  key: string;
  intent: 'info' | 'warning' | 'error';
  text: string;
}

const useStyles = makeStyles({
  list: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalXS, marginBlock: tokens.spacingVerticalS },
});

/** The form's notifications (scripts, read-only, the form bar notice, save errors), one bar each. */
export function FormNotifications({ items }: { items: FormNotification[] }) {
  const styles = useStyles();
  if (items.length === 0) return null;
  return (
    <div className={styles.list} role="region" aria-label={t('record.notifications')}>
      {items.map((n) => (
        <MessageBar key={n.key} intent={n.intent} data-intent={n.intent}>
          <MessageBarBody>{n.text}</MessageBarBody>
        </MessageBar>
      ))}
    </div>
  );
}
