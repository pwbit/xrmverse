import { useEffect, useMemo, useState } from 'react';
import type { XrmApi } from '../../shell/api/client';
import { useAuth } from '../../shell/auth/AuthProvider';
import { useNotify } from '../../shell/notify/Notifications';

const CHUNK = 50;

/** The primary names of the records with the given ids, read with one filtered query per 50 ids. */
export async function resolveNames(
  api: XrmApi,
  entitySet: string,
  idAttribute: string,
  nameAttribute: string,
  ids: readonly (string | null | undefined)[],
): Promise<Record<string, string>> {
  const unique = [...new Set(ids.filter((id): id is string => !!id))];
  const names: Record<string, string> = {};
  for (let i = 0; i < unique.length; i += CHUNK) {
    const filter = unique
      .slice(i, i + CHUNK)
      .map((id) => `${idAttribute} eq ${id}`)
      .join(' or ');
    const page = await api.page<Record<string, string | null>>(
      `${entitySet}?$select=${idAttribute},${nameAttribute}&$filter=${encodeURIComponent(filter)}`,
      CHUNK,
    );
    for (const row of page.value) names[String(row[idAttribute])] = String(row[nameAttribute] ?? '');
  }
  return names;
}

/** Names for the ids shown in a list (empty until resolved); a failure is reported and the ids stay unnamed. */
export function useNames(
  entitySet: string,
  idAttribute: string,
  nameAttribute: string,
  ids: readonly (string | null | undefined)[],
): Record<string, string> {
  const { api } = useAuth();
  const notify = useNotify();
  const [names, setNames] = useState<Record<string, string>>({});
  const key = useMemo(() => [...new Set(ids.filter(Boolean))].sort().join(','), [ids]);
  useEffect(() => {
    if (!api || !key) return;
    let current = true;
    resolveNames(api, entitySet, idAttribute, nameAttribute, key.split(','))
      .then((resolved) => current && setNames((prev) => ({ ...prev, ...resolved })))
      .catch((e) => current && notify.error(e));
    return () => {
      current = false;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [api, entitySet, idAttribute, nameAttribute, key]);
  return names;
}
