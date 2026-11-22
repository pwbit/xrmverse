import { Button, makeStyles, mergeClasses, Text } from '@fluentui/react-components';
import { useAuth } from '../shell/auth/AuthProvider';
import { t } from '../shell/i18n/t';
import { NavMenuIcon } from '../shell/icons';
import { BrandBar, useOnBrandStyles } from '../shell/layout/BrandBar';
import { UserMenu } from './UserMenu';

export { XrmverseLogo } from '../shell/layout/BrandBar';

const useStyles = makeStyles({ app: { flexGrow: 1 } });

export interface NavToggle {
  open: boolean;
  /** The id of the element the button shows and hides. */
  controls: string;
  onToggle: () => void;
}

/** The model-driven header: the brand bar, an optional navigation toggle, a divider and the app's name, then the user and Sign out. */
export function AppHeader({ appName, navToggle }: { appName?: string; navToggle?: NavToggle }) {
  const styles = useStyles();
  const onBrand = useOnBrandStyles();
  const { signOut } = useAuth();
  return (
    <BrandBar>
      {navToggle && (
        <Button
          appearance="transparent"
          className={onBrand.button}
          icon={<NavMenuIcon />}
          aria-label={t('apps.nav.toggle')}
          aria-expanded={navToggle.open}
          aria-controls={navToggle.controls}
          onClick={navToggle.onToggle}
        />
      )}
      {appName !== undefined && <span className={onBrand.divider} aria-hidden="true" />}
      <Text className={mergeClasses(onBrand.text, styles.app)}>{appName}</Text>
      <UserMenu buttonClassName={onBrand.button} />
      <Button appearance="transparent" className={onBrand.button} onClick={signOut}>
        {t('auth.signOut')}
      </Button>
    </BrandBar>
  );
}
