import { makeStyles, useId } from '@fluentui/react-components';
import { AppHeader } from '../../apps/AppHeader';
import { useNavOpen } from '../../apps/useNavOpen';
import { SessionCookieWarning } from '../../shell/auth/SessionCookieWarning';
import { NARROW } from '../../shell/layout/useMediaQuery';
import type { AppFrameProps } from '../contract';
import { SiteMapNav } from './SiteMapNav';

const useStyles = makeStyles({
  // The frame fills the window: the header stays, the navigation and the main area scroll on their own (a list scrolls its grid).
  root: { display: 'flex', flexDirection: 'column', height: ['100vh', '100dvh'] },
  body: { display: 'flex', flexGrow: 1, minHeight: 0 },
  nav: { display: 'flex', flexShrink: 0, overflowY: 'auto' },
  main: { flexGrow: 1, padding: '16px 24px', minWidth: 0, overflow: 'auto' },
});

/** The classic app frame: header with a navigation toggle, sitemap navigation and the main area. */
export function ClassicAppFrame({ appName, siteMap, lcid, appId, current, children, overlays, appBar, sidePanes }: AppFrameProps) {
  const styles = useStyles();
  const [open, toggle] = useNavOpen(NARROW);
  const navId = useId('classic-nav');
  return (
    <div className={styles.root}>
      <AppHeader appName={appName} navToggle={{ open, controls: navId, onToggle: toggle }} />
      <SessionCookieWarning />
      {appBar}
      <div className={styles.body}>
        <div id={navId} className={styles.nav}>
          {open && <SiteMapNav siteMap={siteMap} lcid={lcid} appId={appId} current={current} label={appName} />}
        </div>
        <main className={styles.main}>{children}</main>
        {sidePanes}
      </div>
      {overlays}
    </div>
  );
}
