import { useCallback, useEffect, useRef, useState } from 'react';
import type { XrmApi } from '../../shell/api/client';
import { useAuth } from '../../shell/auth/AuthProvider';

export interface Loaded<T> {
  data?: T;
  loading: boolean;
  /** The server's message with its Dataverse code, when the load failed. */
  error?: string;
  /** `silent`: read again without `loading` turning on, so what is shown stays as it is until the new data arrives. */
  reload: (silent?: boolean) => void;
}

/** {@link useLoad}'s state, with the key the data or error was read for. */
function useTaggedLoad<T>(key: string | undefined, load: (api: XrmApi) => Promise<T>): Loaded<T> & { loadedFor?: string } {
  const { api } = useAuth();
  const [data, setData] = useState<T>();
  const [loading, setLoading] = useState(key !== undefined);
  const [error, setError] = useState<string>();
  const [loadedFor, setLoadedFor] = useState<string>();
  const [version, setVersion] = useState(0);
  const loadRef = useRef(load);
  loadRef.current = load;
  const loadedKey = useRef(key);
  const silentRef = useRef(false);

  useEffect(() => {
    if (key === undefined || !api) {
      setLoading(false);
      return;
    }
    let current = true;
    if (loadedKey.current !== key) {
      loadedKey.current = key;
      setData(undefined);
    }
    if (!silentRef.current) setLoading(true);
    silentRef.current = false;
    setError(undefined);
    loadRef
      .current(api)
      .then((result) => {
        if (!current) return;
        setData(result);
        setLoadedFor(key);
      })
      .catch((e) => {
        if (!current) return;
        setError(e instanceof Error ? e.message : String(e));
        setLoadedFor(key);
      })
      .finally(() => current && setLoading(false));
    return () => {
      current = false;
    };
  }, [api, key, version]);

  const reload = useCallback((silent?: boolean) => {
    silentRef.current = silent === true;
    setVersion((v) => v + 1);
  }, []);
  return { data, loading, error, reload, loadedFor };
}

/**
 * Runs `load` when `key` changes (and on `reload`); `key` undefined loads nothing. A result that arrives after the key
 * changed is dropped.
 */
export function useLoad<T>(key: string | undefined, load: (api: XrmApi) => Promise<T>): Loaded<T> {
  const { data, loading, error, reload } = useTaggedLoad(key, load);
  return { data, loading, error, reload };
}

/**
 * {@link useLoad} for a list read for a target (H9): each result is tagged with the key it was loaded for, and until the
 * result of `key` itself arrives nothing is returned, not even for the render in which the key changed. The data and
 * error of another key are never shown.
 */
export function useLoadFor<T>(key: string | undefined, load: (api: XrmApi) => Promise<T>): Loaded<T> {
  const { data, loading, error, reload, loadedFor } = useTaggedLoad(key, load);
  if (key === undefined || loadedFor !== key) return { data: undefined, loading: key !== undefined, error: undefined, reload };
  return { data, loading, error, reload };
}
