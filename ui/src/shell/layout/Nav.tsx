import { makeStyles, NavDrawer, NavDrawerBody, NavItem } from '@fluentui/react-components';
import type { MouseEvent } from 'react';
import { matchPath, useLocation, useNavigate } from 'react-router-dom';
import { navigableRoutes } from '../../admin/routes';
import { useAccess } from '../auth/roles';
import { t } from '../i18n/t';
import { NARROW, useMediaQuery } from './useMediaQuery';

// Keeps its width beside a wide page (a table's columns): a flex item would otherwise shrink to nothing.
const useStyles = makeStyles({ nav: { flexShrink: 0 } });

/**
 * Sidebar of the sections the current user may open (Environment is System Administrator only), as a Fluent `NavDrawer`:
 * inline on wide windows, an overlay on narrow ones shown while `open` (the header's Menu button). Choosing a section
 * calls `onNavigate`, which closes the overlay.
 */
export function Nav({ id, open = false, onNavigate }: { id?: string; open?: boolean; onNavigate?: () => void } = {}) {
  const styles = useStyles();
  const { level } = useAccess();
  const { pathname } = useLocation();
  const navigate = useNavigate();
  const narrow = useMediaQuery(NARROW);
  const items = navigableRoutes.filter((r) => !r.adminOnly || level === 'admin');
  const selected = items.find((r) => matchPath({ path: r.path, end: false }, pathname))?.path;
  const go = (path: string) => (e: MouseEvent) => {
    // Plain clicks stay in the single-page app; modified clicks (new tab) keep the browser's default.
    if (e.button !== 0 || e.metaKey || e.ctrlKey || e.shiftKey || e.altKey) return;
    e.preventDefault();
    navigate(path);
    onNavigate?.();
  };
  return (
    <NavDrawer
      id={id}
      aria-label={t('nav.label')}
      className={styles.nav}
      type={narrow ? 'overlay' : 'inline'}
      open={narrow ? open : true}
      onOpenChange={(_, data) => !data.open && onNavigate?.()}
      // Always a string: Fluent treats an undefined first value as uncontrolled and would then ignore later ones.
      selectedValue={selected ?? ''}
    >
      <NavDrawerBody>
        {items.map((r) => (
          <NavItem key={r.path} as="a" href={r.path} value={r.path} onClick={go(r.path)}>
            {t(r.label)}
          </NavItem>
        ))}
      </NavDrawerBody>
    </NavDrawer>
  );
}
