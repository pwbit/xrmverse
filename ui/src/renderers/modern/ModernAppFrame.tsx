import { Button, makeStyles, Text, tokens, useId } from '@fluentui/react-components';
import { useEffect } from 'react';
import { XrmverseLogo } from '../../apps/AppHeader';
import { UserMenu } from '../../apps/UserMenu';
import { useNavOpen } from '../../apps/useNavOpen';
import { webResourceUrl } from '../../client-api/scriptHost';
import { useAuth } from '../../shell/auth/AuthProvider';
import { SessionCookieWarning } from '../../shell/auth/SessionCookieWarning';
import { t } from '../../shell/i18n/t';
import { NavMenuIcon } from '../../shell/icons';
import { SiteMapNav } from '../classic/SiteMapNav';
import type { AppFrameProps } from '../contract';
import { NARROW_QUERY } from './ModernViewGrid';

const useStyles = makeStyles({
  root: {
    display: 'flex',
    flexDirection: 'column',
    // The frame fills the window: the header stays, the rail and the main area scroll on their own (a list scrolls its grid).
    height: ['100vh', '100dvh'],
    backgroundColor: tokens.colorNeutralBackground2,
    // The filled icons come from the modern theme scope's class (filledIcons.ts), which also reaches the portals.
  },
  header: {
    display: 'flex',
    alignItems: 'center',
    columnGap: tokens.spacingHorizontalM,
    padding: `0 ${tokens.spacingHorizontalM}`,
    minHeight: '48px',
    backgroundColor: tokens.colorNeutralBackground1,
    color: tokens.colorNeutralForeground1,
    borderBottom: `${tokens.strokeWidthThin} solid ${tokens.colorNeutralStroke2}`,
  },
  logo: { height: '24px', width: 'auto', flexShrink: 0 },
  name: { flexGrow: 1, fontWeight: tokens.fontWeightSemibold, fontSize: tokens.fontSizeBase400, color: tokens.colorNeutralForeground1 },
  body: { display: 'flex', flexGrow: 1, minHeight: 0 },
  rail: { display: 'flex', flexShrink: 0, overflowY: 'auto' },
  main: { flexGrow: 1, padding: `${tokens.spacingVerticalL} ${tokens.spacingHorizontalXXL}`, minWidth: 0, overflow: 'auto', backgroundColor: tokens.colorNeutralBackground1 },
});

/** `document.title` is `title` while the frame is shown; the previous title comes back when it leaves. */
function useDocumentTitle(title: string) {
  useEffect(() => {
    const previous = document.title;
    document.title = title;
    return () => {
      document.title = previous;
    };
  }, [title]);
}

/** The page's `<link rel="icon">` points to the web resource `favicon` while the frame is shown; restored when it leaves. */
function useFavicon(favicon: string | undefined) {
  useEffect(() => {
    if (!favicon) return;
    const existing = document.head.querySelector<HTMLLinkElement>('link[rel~="icon"]');
    const link = existing ?? document.head.appendChild(document.createElement('link'));
    const previous = existing?.getAttribute('href') ?? null;
    if (!existing) link.rel = 'icon';
    link.setAttribute('href', webResourceUrl(favicon));
    return () => {
      if (!existing) link.remove();
      else if (previous === null) link.removeAttribute('href');
      else link.setAttribute('href', previous);
    };
  }, [favicon]);
}

/**
 * The modern app frame (ruling A3): a neutral header with the white-label logo and name (else the xrmverse logo and the
 * app's name), and the sitemap navigation as a rail that a header button collapses and expands.
 */
export function ModernAppFrame({ appName, siteMap, lcid, appId, current, children, overlays, whiteLabel, appBar, sidePanes }: AppFrameProps) {
  const styles = useStyles();
  const { signOut } = useAuth();
  // The rail starts closed on a narrow screen (it would cover the page), else as last remembered in this browser.
  const [open, toggle] = useNavOpen(NARROW_QUERY);
  const railId = useId('modern-rail');
  const name = whiteLabel?.name ?? appName;
  useDocumentTitle(name);
  useFavicon(whiteLabel?.favicon);
  return (
    <div className={styles.root}>
      <header className={styles.header}>
        <Button
          appearance="subtle"
          icon={<NavMenuIcon />}
          aria-label={t('apps.nav.toggle')}
          aria-expanded={open}
          aria-controls={railId}
          onClick={toggle}
        />
        {whiteLabel?.logo ? <img className={styles.logo} src={webResourceUrl(whiteLabel.logo)} alt={name} /> : <XrmverseLogo />}
        <Text className={styles.name}>{name}</Text>
        <UserMenu appearance="subtle" />
        <Button appearance="subtle" onClick={signOut}>
          {t('auth.signOut')}
        </Button>
      </header>
      <SessionCookieWarning />
      {appBar}
      <div className={styles.body}>
        <div id={railId} className={styles.rail}>
          {open && <SiteMapNav siteMap={siteMap} lcid={lcid} appId={appId} current={current} label={name} />}
        </div>
        <main className={styles.main}>{children}</main>
        {sidePanes}
      </div>
      {overlays}
    </div>
  );
}
