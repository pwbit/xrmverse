import { Button, MessageBar, MessageBarActions, MessageBarBody } from '@fluentui/react-components';
import { createContext, useContext, useMemo, useRef, useState, type ReactNode } from 'react';
import type { PresentationModel } from '../../parsers/presentationJson';
import { buildTheme } from '../../renderers/modern/theme';
import { usePresentation } from '../../renderers/modern/usePresentation';
import { resolveColorScheme } from '../../renderers/resolve';
import { ThemeScope, useActiveTheme } from '../../renderers/ThemeScope';
import { DismissIcon } from '../icons';
import { t } from '../i18n/t';
import { xrmverseBrand } from '../theme';
import { useMediaQuery } from './useMediaQuery';
import { useEnvironmentOverlay } from './useEnvironmentOverlay';

/** Why the environment's look is not used (its setting or overlay could not be read), for {@link AdminThemeNotice}. */
const NoticeContext = createContext<string | undefined>(undefined);

/**
 * The web admin's theme: the theme the modern look builds from the environment's presentation overlay (`xrmverse_Presentation`):
 * its brand ramp, font, density and colour scheme; its white label does not apply, the admin stays xrmverse branded. Without an
 * overlay, while it loads, or when it cannot be read, the children render with the root theme (the xrmverse theme). The scope is
 * always there, so the pages below are not mounted again when the overlay arrives.
 */
export function AdminTheme({ children }: { children: ReactNode }) {
  const { name, error } = useEnvironmentOverlay();
  const presentation = usePresentation(name);
  // While the next overlay loads (after a save, say) the previous model's theme stays; it falls back when the next one fails.
  const previous = useRef<PresentationModel>();
  if (!name) previous.current = undefined;
  else if (!presentation.loading) previous.current = presentation.model;
  const model = presentation.loading ? previous.current : presentation.model;
  const notice = presentation.notice ?? (error !== undefined ? t('presentation.environmentUnreadable', { detail: error }).trim() : undefined);
  const root = useActiveTheme();
  const osDark = useMediaQuery('(prefers-color-scheme: dark)');
  // The admin has no per-user colour scheme: the overlay's scheme wins over the operating system's (R3).
  const dark = model ? resolveColorScheme(0, model.theme.colorScheme, osDark) === 'dark' : root.dark;
  const theme = useMemo(() => (model ? buildTheme(model, dark) : root.theme), [model, dark, root.theme]);
  const brand = model ? (model.theme.brand ?? xrmverseBrand) : root.brand;
  return (
    <ThemeScope theme={theme} dark={dark} brand={brand}>
      <NoticeContext.Provider value={notice}>{children}</NoticeContext.Provider>
    </ThemeScope>
  );
}

/** Why the environment's overlay is not used (the presentation notice), as a warning the user can dismiss; nothing otherwise. */
export function AdminThemeNotice() {
  const notice = useContext(NoticeContext);
  const [dismissed, setDismissed] = useState<string>();
  if (!notice || notice === dismissed) return null;
  return (
    <MessageBar intent="warning">
      <MessageBarBody>{notice}</MessageBarBody>
      <MessageBarActions
        containerAction={<Button appearance="transparent" icon={<DismissIcon />} aria-label={t('notify.dismiss')} onClick={() => setDismissed(notice)} />}
      />
    </MessageBar>
  );
}
