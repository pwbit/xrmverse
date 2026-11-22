import { useCallback, useState } from 'react';

const KEY = 'xrmverse.nav.open';

function remembered(): boolean | undefined {
  try {
    const v = window.localStorage.getItem(KEY);
    return v === '1' ? true : v === '0' ? false : undefined;
  } catch {
    return undefined; // blocked storage: nothing remembered
  }
}

function remember(open: boolean) {
  try {
    window.localStorage.setItem(KEY, open ? '1' : '0');
  } catch {
    // blocked storage: the choice just is not remembered
  }
}

/**
 * Whether the sitemap navigation is open, and a toggle that remembers the choice in this browser. It starts closed
 * when `narrowQuery` matches (the navigation would cover the page), else as last remembered, else open.
 */
export function useNavOpen(narrowQuery: string): [open: boolean, toggle: () => void] {
  const [open, setOpen] = useState(() => {
    if (typeof window.matchMedia === 'function' && window.matchMedia(narrowQuery).matches) return false;
    return remembered() ?? true;
  });
  const toggle = useCallback(() => {
    setOpen((o) => {
      remember(!o);
      return !o;
    });
  }, []);
  return [open, toggle];
}
