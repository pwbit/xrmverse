import { useEffect, useRef } from 'react';
import { useBlocker, type Blocker } from 'react-router-dom';

export interface UnsavedGuard {
  /** `blocked` while a navigation waits for the user's answer: `proceed()` discards the changes, `reset()` keeps editing. */
  blocker: Blocker;
  /** Runs `navigate` without the guard (after a save, a delete or a discard the user confirmed). */
  navigateAnyway(navigate: () => void): void;
}

/**
 * The unsaved-changes guard of a form (Global Constraints, "Unsaved changes"): while `dirty`, every navigation inside the app
 * (sitemap, list, form selector, subgrid rows, the back button) is blocked until the user answers, and the browser's own
 * unload asks through `beforeunload`. The listener exists only while the form is dirty. Needs a data router.
 */
export function useUnsavedGuard(dirty: boolean): UnsavedGuard {
  const dirtyRef = useRef(dirty);
  dirtyRef.current = dirty;
  const allowed = useRef(false);
  const blocker = useBlocker(() => dirtyRef.current && !allowed.current);

  useEffect(() => {
    if (!dirty) return;
    const ask = (event: BeforeUnloadEvent) => {
      event.preventDefault();
      // Older browsers show their prompt only for a returned value.
      event.returnValue = '';
    };
    window.addEventListener('beforeunload', ask);
    return () => window.removeEventListener('beforeunload', ask);
  }, [dirty]);

  const navigateAnyway = (navigate: () => void) => {
    // The router asks the blocker synchronously inside `navigate`.
    allowed.current = true;
    try {
      navigate();
    } finally {
      allowed.current = false;
    }
  };

  return { blocker, navigateAnyway };
}
