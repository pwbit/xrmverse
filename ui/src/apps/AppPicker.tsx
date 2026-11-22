import {
  Body1,
  makeStyles,
  MessageBar,
  MessageBarBody,
  Spinner,
  Title2,
  tokens,
} from '@fluentui/react-components';
import { useEffect, useState } from 'react';
import type { AppModel } from '../model/app';
import { useAuth } from '../shell/auth/AuthProvider';
import { t } from '../shell/i18n/t';
import { WorkflowStudioIcon } from '../shell/icons';
import { useCanReadWorkflows } from '../studio/useCanReadWorkflows';
import { AppHeader } from './AppHeader';
import { AppTile } from './AppTile';
import { loadApps } from './loadApps';
import { mainAspxUrl } from './mainAspx';

const useStyles = makeStyles({
  // Same page surface as the web admin and studio shells: fills the viewport in every theme.
  root: { minHeight: '100vh', boxSizing: 'border-box', backgroundColor: tokens.colorNeutralBackground1 },
  main: { padding: tokens.spacingHorizontalXXL, maxWidth: '960px' },
  list: {
    listStyleType: 'none',
    margin: `${tokens.spacingVerticalL} 0 0`,
    padding: 0,
    display: 'grid',
    gap: tokens.spacingHorizontalM,
    gridTemplateColumns: 'repeat(auto-fill, minmax(260px, 1fr))',
  },
  item: { display: 'flex' },
});

/** `/apps`: the apps the user may open (see {@link loadApps}), then the workflow studio when the user may read processes. */
export function AppPicker() {
  const styles = useStyles();
  const { api } = useAuth();
  const [state, setState] = useState<{ apps?: AppModel[]; error?: string }>({});
  const studio = useCanReadWorkflows();
  useEffect(() => {
    if (!api) return;
    let cancelled = false;
    loadApps(api).then(
      (apps) => !cancelled && setState({ apps }),
      (e: unknown) => !cancelled && setState({ error: e instanceof Error ? e.message : String(e) }),
    );
    return () => {
      cancelled = true;
    };
  }, [api]);

  return (
    <div className={styles.root} data-testid="app-picker">
      <AppHeader />
      <main className={styles.main}>
        <Title2 as="h1">{t('apps.title')}</Title2>
        {state.error && (
          <MessageBar intent="error">
            <MessageBarBody>{t('apps.error', { detail: state.error })}</MessageBarBody>
          </MessageBar>
        )}
        {!state.apps && !state.error && <Spinner label={t('apps.loading')} />}
        {/* The empty state only when there is nothing at all: no apps and no tools tile (the studio answer is known and negative). */}
        {state.apps?.length === 0 && studio === false && <Body1 block>{t('apps.none')}</Body1>}
        {!!state.apps?.length && (
          <ul className={styles.list} aria-label={t('apps.title')}>
            {state.apps.map((app) => (
              <li key={app.id} className={styles.item}>
                <AppTile name={app.name} description={app.description} to={mainAspxUrl({ appId: app.id, pageType: '' })} />
              </li>
            ))}
          </ul>
        )}
        {studio && (
          <ul className={styles.list} aria-label={t('apps.tools')}>
            <li className={styles.item}>
              <AppTile name={t('studio.title')} description={t('studio.apps.description')} to="/studio/workflows" icon={<WorkflowStudioIcon fontSize={48} />} />
            </li>
          </ul>
        )}
      </main>
    </div>
  );
}
