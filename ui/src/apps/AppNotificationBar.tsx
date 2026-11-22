import { Button, MessageBar, MessageBarActions, MessageBarBody } from '@fluentui/react-components';
import { DismissRegular } from '@fluentui/react-icons';
import { useState, useSyncExternalStore } from 'react';
import type { AppNotification, AppNotificationStore } from '../client-api/xrmverse/appNotifications';
import { t } from '../shell/i18n/t';

const MAX_VISIBLE = 3;
const INTENT = { 1: 'success', 2: 'error', 3: 'warning', 4: 'info' } as const;

async function runAction(n: AppNotification): Promise<void> {
  try {
    await n.action?.eventHandler();
  } catch (e) {
    console.error('App notification action failed', e);
  }
}

/** The app's notifications under the header, newest first; at most three show, the rest behind "+N more". */
export function AppNotificationBar({ store }: { store: AppNotificationStore }) {
  const list = useSyncExternalStore(store.subscribe, store.list);
  const [expanded, setExpanded] = useState(false);
  // A burst after the list went back to three or fewer folds again.
  if (expanded && list.length <= MAX_VISIBLE) setExpanded(false);
  if (list.length === 0) return null;
  const shown = expanded ? list : list.slice(0, MAX_VISIBLE);
  const hidden = list.length - shown.length;
  return (
    <div role="region" aria-label={t('apps.notifications.label')}>
      {shown.map((n) => (
        <MessageBar key={n.id} intent={INTENT[n.level]} layout="multiline">
          <MessageBarBody>{n.message}</MessageBarBody>
          {(n.action || n.showCloseButton) && (
            <MessageBarActions
              containerAction={
                n.showCloseButton ? (
                  <Button
                    appearance="transparent"
                    size="small"
                    aria-label={t('apps.notifications.close')}
                    icon={<DismissRegular />}
                    onClick={() => void store.clear(n.id)}
                  />
                ) : undefined
              }
            >
              {n.action && <Button onClick={() => void runAction(n)}>{n.action.actionLabel}</Button>}
            </MessageBarActions>
          )}
        </MessageBar>
      ))}
      {hidden > 0 && (
        <Button appearance="transparent" size="small" onClick={() => setExpanded(true)}>
          {t('apps.notifications.more', { count: hidden })}
        </Button>
      )}
    </div>
  );
}
