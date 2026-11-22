import { makeStyles, tokens } from '@fluentui/react-components';
import { useId, useState } from 'react';
import { Outlet } from 'react-router-dom';
import { SessionCookieWarning } from '../auth/SessionCookieWarning';
import { AdminTheme, AdminThemeNotice } from './AdminTheme';
import { Header } from './Header';
import { Nav } from './Nav';

const useStyles = makeStyles({
  root: { display: 'flex', flexDirection: 'column', minHeight: '100vh', backgroundColor: tokens.colorNeutralBackground1 },
  body: { display: 'flex', flexGrow: 1, '@media (max-width: 600px)': { flexDirection: 'column' } },
  main: {
    flexGrow: 1,
    minWidth: 0,
    padding: '24px',
    boxSizing: 'border-box',
    // Every list of the web admin is a surface outlined by shadow2, like a section of a model-driven form.
    '& .fui-Table': { boxShadow: tokens.shadow2, borderRadius: tokens.borderRadiusMedium, backgroundColor: tokens.colorNeutralBackground1 },
  },
});

/** The web admin layout, in the environment's presentation theme: the header, the section navigation and the current page. */
export function Shell() {
  const styles = useStyles();
  const navId = useId();
  // Narrow windows (600 px and less) collapse the navigation behind the header's Menu button.
  const [menuOpen, setMenuOpen] = useState(false);
  return (
    <AdminTheme>
      <div className={styles.root}>
        <Header menu={{ navId, open: menuOpen, toggle: () => setMenuOpen((o) => !o) }} />
        <SessionCookieWarning />
        <AdminThemeNotice />
        <div className={styles.body}>
          <Nav id={navId} open={menuOpen} onNavigate={() => setMenuOpen(false)} />
          <main className={styles.main}>
            <Outlet />
          </main>
        </div>
      </div>
    </AdminTheme>
  );
}
