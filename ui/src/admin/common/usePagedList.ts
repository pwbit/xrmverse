import { useCallback, useEffect, useRef, useState } from 'react';
import { useAuth } from '../../shell/auth/AuthProvider';
import { useNotify } from '../../shell/notify/Notifications';
import type { RequestExtras } from '../../shell/api/client';

export interface PagedList<T> {
  items: T[];
  setItems: React.Dispatch<React.SetStateAction<T[]>>;
  nextLink?: string;
  loading: boolean;
  error?: string;
  loadMore: () => void;
  reload: () => void;
}

/**
 * A server-paged list (`@odata.nextLink`, never the whole table). A new `path` (or `reload`) starts over; a response that
 * arrives after a newer request was made is dropped. `path` undefined loads nothing.
 */
export function usePagedList<T>(path: string | undefined, pageSize = 50, init?: RequestExtras): PagedList<T> {
  const { api } = useAuth();
  const notify = useNotify();
  const [items, setItems] = useState<T[]>([]);
  const [nextLink, setNextLink] = useState<string>();
  const [loading, setLoading] = useState(path !== undefined);
  const [error, setError] = useState<string>();
  const [version, setVersion] = useState(0);
  const generation = useRef(0);
  const initRef = useRef(init);
  initRef.current = init;
  const notifyRef = useRef(notify);
  notifyRef.current = notify;

  const fetchPage = useCallback(
    async (pathOrLink: string, append: boolean) => {
      if (!api) return;
      const mine = ++generation.current;
      setLoading(true);
      setError(undefined);
      try {
        const page = await api.page<T>(pathOrLink, pageSize, initRef.current);
        if (mine !== generation.current) return;
        setItems((prev) => (append ? [...prev, ...page.value] : page.value));
        setNextLink(page.nextLink);
      } catch (e) {
        if (mine !== generation.current) return;
        setError(e instanceof Error ? e.message : String(e));
        notifyRef.current.error(e);
      } finally {
        if (mine === generation.current) setLoading(false);
      }
    },
    [api, pageSize],
  );

  useEffect(() => {
    if (path === undefined) {
      generation.current++;
      setItems([]);
      setNextLink(undefined);
      setLoading(false);
      return;
    }
    void fetchPage(path, false);
  }, [path, version, fetchPage]);

  const loadMore = useCallback(() => {
    if (nextLink) void fetchPage(nextLink, true);
  }, [nextLink, fetchPage]);
  const reload = useCallback(() => setVersion((v) => v + 1), []);

  return { items, setItems, nextLink, loading, error, loadMore, reload };
}
