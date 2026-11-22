import { makeStyles, Text, tokens } from '@fluentui/react-components';
import type { ReactNode } from 'react';
import { t } from '../i18n/t';
import { brandFontStack } from '../theme';

const useStyles = makeStyles({
  bar: {
    display: 'flex',
    alignItems: 'center',
    flexWrap: 'wrap',
    columnGap: tokens.spacingHorizontalM,
    rowGap: tokens.spacingVerticalXS,
    padding: `0 ${tokens.spacingHorizontalL}`,
    minHeight: '48px',
    backgroundColor: tokens.colorBrandBackgroundPressed,
    color: tokens.colorNeutralForegroundOnBrand,
  },
  name: { color: tokens.colorNeutralForegroundOnBrand, fontFamily: brandFontStack, fontWeight: tokens.fontWeightSemibold, fontSize: tokens.fontSizeBase400 },
  logo: { flexShrink: 0, color: tokens.colorNeutralForegroundOnBrand },
});

/** Shared styles for content placed on the brand bar: text and transparent buttons that stay readable on it. */
export const useOnBrandStyles = makeStyles({
  text: { color: tokens.colorNeutralForegroundOnBrand },
  button: {
    color: tokens.colorNeutralForegroundOnBrand,
    ':hover': { color: tokens.colorNeutralForegroundOnBrand, backgroundColor: tokens.colorBrandBackgroundHover },
    ':hover:active': { color: tokens.colorNeutralForegroundOnBrand, backgroundColor: tokens.colorBrandBackground },
  },
  divider: { width: '1px', height: '24px', flexShrink: 0, backgroundColor: tokens.colorNeutralStrokeOnBrand2 },
});

/** The xrmverse mark: our own rounded square with an "x" stroke (no third-party logo), in brand tokens. */
export function XrmverseLogo({ size = 24 }: { size?: number }) {
  const styles = useStyles();
  return (
    <svg className={styles.logo} width={size} height={size} viewBox="0 0 24 24" role="img" aria-label={t('apps.header.logo')}>
      <rect width="24" height="24" rx="6" fill={tokens.colorBrandBackground} />
      <path d="M8 8l8 8M16 8l-8 8" stroke="currentColor" strokeWidth="2.5" strokeLinecap="round" />
    </svg>
  );
}

/**
 * The xrmverse brand bar (a `banner` landmark) shared by the web admin, the sign-in page and the model-driven apps:
 * `start` (the narrow-window Menu button), the logo and name, then `children`.
 */
export function BrandBar({ start, children }: { start?: ReactNode; children?: ReactNode }) {
  const styles = useStyles();
  return (
    <header className={styles.bar}>
      {start}
      <XrmverseLogo />
      <Text className={styles.name}>{t('app.name')}</Text>
      {children}
    </header>
  );
}
