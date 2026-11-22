import { Caption1, Card, makeStyles, Subtitle2, tokens } from '@fluentui/react-components';
import type { ReactElement } from 'react';
import { Link } from 'react-router-dom';

const useStyles = makeStyles({
  card: {
    position: 'relative',
    flexGrow: 1,
    minWidth: 0,
    padding: 0,
    overflow: 'hidden',
    rowGap: 0,
    ':focus-within': { outline: `${tokens.strokeWidthThick} solid ${tokens.colorStrokeFocus2}` },
  },
  // The image of an app: a band in the theme's brand colours (the gradient is set inline from the brand tokens).
  banner: {
    height: '84px',
    display: 'grid',
    placeItems: 'center',
    color: tokens.colorNeutralForegroundOnBrand,
  },
  text: {
    display: 'grid',
    rowGap: tokens.spacingVerticalXS,
    minWidth: 0,
    padding: `${tokens.spacingVerticalM} ${tokens.spacingHorizontalL} ${tokens.spacingVerticalL}`,
    textAlign: 'center',
  },
  // The name stays on one line and ends with an ellipsis when too long; the full name is its tooltip.
  link: {
    color: tokens.colorBrandForegroundLink,
    textDecorationLine: 'none',
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
    ':hover': { textDecorationLine: 'underline' },
    // The whole card opens the app.
    '::after': { content: '""', position: 'absolute', inset: 0 },
  },
  description: {
    color: tokens.colorNeutralForeground2,
    display: '-webkit-box',
    WebkitLineClamp: 2,
    WebkitBoxOrient: 'vertical',
    overflow: 'hidden',
  },
});

/** The xrmverse mark on a translucent tile, in the on-brand colour; decorative (the name is the link text). */
function AppMark() {
  const c = tokens.colorNeutralForegroundOnBrand;
  return (
    <svg data-testid="app-tile-mark" width="48" height="48" viewBox="0 0 24 24" aria-hidden="true" focusable="false">
      <rect width="24" height="24" rx="6" fill={c} fillOpacity="0.18" stroke={c} strokeOpacity="0.5" />
      <path d="M8 8l8 8M16 8l-8 8" stroke={c} strokeWidth="2.5" strokeLinecap="round" />
    </svg>
  );
}

/** A banner tile with centred text; the whole card is the link. `icon` replaces the xrmverse mark in the banner. */
export function AppTile({ name, description, to, icon }: { name: string; description?: string; to: string; icon?: ReactElement }) {
  const styles = useStyles();
  return (
    <Card className={styles.card} focusMode="no-tab">
      <div
        className={styles.banner}
        data-testid="app-tile-banner"
        style={{ backgroundImage: `linear-gradient(135deg, ${tokens.colorBrandBackgroundPressed}, ${tokens.colorBrandBackground})` }}
      >
        {icon ?? <AppMark />}
      </div>
      <div className={styles.text} data-testid="app-tile-text">
        <Link className={styles.link} to={to} title={name}>
          <Subtitle2 align="center">{name}</Subtitle2>
        </Link>
        {description && (
          <Caption1 block align="center" className={styles.description} title={description}>
            {description}
          </Caption1>
        )}
      </div>
    </Card>
  );
}
