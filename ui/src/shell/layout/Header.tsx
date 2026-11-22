import { Button, Hamburger, makeStyles, mergeClasses, Text, tokens } from '@fluentui/react-components';
import { useEffect, useState } from 'react';
import { Link } from 'react-router-dom';
import { useCanReadWorkflows } from '../../studio/useCanReadWorkflows';
import { useAuth } from '../auth/AuthProvider';
import { t } from '../i18n/t';
import { useNotify } from '../notify/Notifications';
import { BrandBar, useOnBrandStyles } from './BrandBar';

const useStyles = makeStyles({
  environment: { flexGrow: 1, flexBasis: '0px', minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis' },
  menu: { minWidth: 'auto', display: 'none', '@media (max-width: 600px)': { display: 'inline-flex' } },
  wide: { '@media (max-width: 600px)': { display: 'none' } },
  studio: {
    textDecorationLine: 'none',
    padding: `${tokens.spacingVerticalXS} ${tokens.spacingHorizontalS}`,
    borderRadius: tokens.borderRadiusMedium,
    ':hover': { textDecorationLine: 'underline' },
  },
});

/** The narrow-window Menu button's link to the navigation it shows and hides. */
export interface HeaderMenu {
  navId: string;
  open: boolean;
  toggle(): void;
}

/**
 * The web admin header: the brand bar, the environment's name, a link to the workflow studio (for a user who may read
 * processes), the user and Sign out. On narrow windows the user's name is
 * hidden and a Menu button shows or hides the navigation.
 */
export function Header({ menu }: { menu?: HeaderMenu } = {}) {
  const styles = useStyles();
  const onBrand = useOnBrandStyles();
  const { api, user, signOut } = useAuth();
  const notify = useNotify();
  const [environment, setEnvironment] = useState<string>();
  const studio = useCanReadWorkflows();

  useEffect(() => {
    if (!api) return;
    let cancelled = false;
    const controller = new AbortController();
    api.get<{ value: { name: string }[] }>('organizations?$select=name', { signal: controller.signal }).then(
      (result) => !cancelled && setEnvironment(result.value[0]?.name),
      (e: unknown) => !cancelled && notify.error(e),
    );
    return () => {
      cancelled = true;
      controller.abort();
    };
  }, [api, notify]);

  const environmentName = environment ?? t('header.environmentUnknown');
  return (
    <BrandBar
      start={
        menu && (
          <Hamburger
            appearance="transparent"
            className={mergeClasses(onBrand.button, styles.menu)}
            aria-label={t('nav.menu')}
            aria-expanded={menu.open}
            aria-controls={menu.navId}
            onClick={menu.toggle}
          />
        )
      }
    >
      <span className={onBrand.divider} aria-hidden="true" />
      <Text className={mergeClasses(onBrand.text, styles.environment)} wrap={false} title={`${t('header.environment')}: ${environmentName}`}>
        {environmentName}
      </Text>
      {studio && (
        <Link to="/studio/workflows" className={mergeClasses(onBrand.button, styles.studio)}>
          {t('studio.title')}
        </Link>
      )}
      <Text className={mergeClasses(onBrand.text, styles.wide)}>{user?.name}</Text>
      <Button appearance="transparent" className={onBrand.button} onClick={signOut}>
        {t('auth.signOut')}
      </Button>
    </BrandBar>
  );
}
