import { Button, makeStyles, mergeClasses, Text, tokens } from '@fluentui/react-components';
import { Link, Outlet } from 'react-router-dom';
import { UserMenu } from '../apps/UserMenu';
import { useAuth } from '../shell/auth/AuthProvider';
import { t } from '../shell/i18n/t';
import { BrandBar, useOnBrandStyles } from '../shell/layout/BrandBar';

const useStyles = makeStyles({
  root: { display: 'flex', flexDirection: 'column', minHeight: '100vh', backgroundColor: tokens.colorNeutralBackground1 },
  title: { flexGrow: 1, minWidth: 0 },
  apps: {
    textDecorationLine: 'none',
    padding: `${tokens.spacingVerticalXS} ${tokens.spacingHorizontalS}`,
    borderRadius: tokens.borderRadiusMedium,
    ':hover': { textDecorationLine: 'underline' },
  },
  main: {
    flexGrow: 1,
    minWidth: 0,
    padding: tokens.spacingHorizontalXXL,
    boxSizing: 'border-box',
    '& .fui-Table': { boxShadow: tokens.shadow2, borderRadius: tokens.borderRadiusMedium, backgroundColor: tokens.colorNeutralBackground1 },
  },
});

/**
 * The workflow studio's frame, an app of its own (not the web admin's `Shell` and not its role gate): the brand bar with the
 * studio's name, a link back to the apps, the user menu and Sign out, then the current studio page.
 */
export function StudioShell() {
  const styles = useStyles();
  const onBrand = useOnBrandStyles();
  const { signOut } = useAuth();
  return (
    <div className={styles.root}>
      <BrandBar>
        <span className={onBrand.divider} aria-hidden="true" />
        <Text className={mergeClasses(onBrand.text, styles.title)}>{t('studio.title')}</Text>
        <Link to="/apps" className={mergeClasses(onBrand.button, styles.apps)}>
          {t('studio.header.apps')}
        </Link>
        <UserMenu buttonClassName={onBrand.button} />
        <Button appearance="transparent" className={onBrand.button} onClick={signOut}>
          {t('auth.signOut')}
        </Button>
      </BrandBar>
      <main className={styles.main}>
        <Outlet />
      </main>
    </div>
  );
}
