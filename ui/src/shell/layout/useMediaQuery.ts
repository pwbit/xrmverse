import { useEffect, useState } from 'react';

/** Whether `query` matches now, following changes; false where `matchMedia` is missing (tests, old browsers). */
export function useMediaQuery(query: string): boolean {
  const get = () => typeof window !== 'undefined' && typeof window.matchMedia === 'function' && window.matchMedia(query).matches;
  const [matches, setMatches] = useState(get);
  useEffect(() => {
    if (typeof window.matchMedia !== 'function') return;
    const list = window.matchMedia(query);
    const onChange = () => setMatches(list.matches);
    onChange();
    list.addEventListener('change', onChange);
    return () => list.removeEventListener('change', onChange);
  }, [query]);
  return matches;
}

/** The narrow-window breakpoint shared by the web admin and the model-driven apps. */
export const NARROW = '(max-width: 600px)';
