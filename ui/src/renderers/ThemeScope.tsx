import { FluentProvider, type BrandVariants, type Theme } from '@fluentui/react-components';
import { createContext, useContext, useEffect, useMemo, useState, type ReactNode } from 'react';
import { useMediaQuery } from '../shell/layout/useMediaQuery';
import { xrmverseBrand, xrmverseDarkTheme, xrmverseTheme } from '../shell/theme';
import { resolveColorScheme } from './resolve';

/** The Fluent theme in force where a component renders, with its scheme and brand ramp (for PCF's `fluentDesignLanguage`). */
export interface ActiveTheme {
  theme: Theme;
  dark: boolean;
  /** The brand ramp the theme was built on. */
  brand?: BrandVariants;
}

const LIGHT: ActiveTheme = { theme: xrmverseTheme, dark: false, brand: xrmverseBrand };
const DARK: ActiveTheme = { theme: xrmverseDarkTheme, dark: true, brand: xrmverseBrand };

const ActiveThemeContext = createContext<ActiveTheme | undefined>(undefined);

/**
 * A nested `FluentProvider` with `theme`: everything below it (portals included, Fluent mounts them inside the provider)
 * renders with that theme, and {@link useActiveTheme} returns it. `dark` defaults to the enclosing scope's. `className` goes on
 * the provider and, through Fluent, on the mount node of every portal below it.
 */
export function ThemeScope({ theme, dark, brand, className, children }: { theme: Theme; dark?: boolean; brand?: BrandVariants; className?: string; children: ReactNode }) {
  const parent = useContext(ActiveThemeContext);
  const isDark = dark ?? parent?.dark ?? false;
  const value = useMemo<ActiveTheme>(() => ({ theme, dark: isDark, brand }), [theme, isDark, brand]);
  return (
    <FluentProvider theme={theme} className={className}>
      <ActiveThemeContext.Provider value={value}>{children}</ActiveThemeContext.Provider>
    </FluentProvider>
  );
}

/** The theme of the nearest {@link ThemeScope}; outside any, the xrmverse theme for the operating system's scheme. */
export function useActiveTheme(): ActiveTheme {
  const scoped = useContext(ActiveThemeContext);
  const osDark = useMediaQuery('(prefers-color-scheme: dark)');
  return scoped ?? (osDark ? DARK : LIGHT);
}

type Scheme = 0 | 1 | 2;
const SetRootSchemeContext = createContext<(scheme: Scheme) => void>(() => {});

/**
 * The page's root theme: the xrmverse theme in the signed-in user's colour scheme (`usersettings.xrmverse_colorscheme`,
 * reported from below by {@link useUserColorScheme}), else the operating system's. The user settings are loaded deep in the
 * apps route, so the scheme flows up through a setter; outside the apps (web admin, sign-in) it is the operating system's.
 */
export function RootTheme({ children }: { children: ReactNode }) {
  const [scheme, setScheme] = useState<Scheme>(0);
  const osDark = useMediaQuery('(prefers-color-scheme: dark)');
  const active = resolveColorScheme(scheme, undefined, osDark) === 'dark' ? DARK : LIGHT;
  return (
    <SetRootSchemeContext.Provider value={setScheme}>
      <ThemeScope theme={active.theme} dark={active.dark} brand={active.brand}>
        {children}
      </ThemeScope>
    </SetRootSchemeContext.Provider>
  );
}

/** Applies the user's colour scheme to the {@link RootTheme} while the caller is mounted; System (0) again when it unmounts. */
export function useUserColorScheme(scheme: Scheme): void {
  const set = useContext(SetRootSchemeContext);
  useEffect(() => {
    set(scheme);
    return () => set(0);
  }, [set, scheme]);
}
