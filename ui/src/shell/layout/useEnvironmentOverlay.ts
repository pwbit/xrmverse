import { useCallback, useEffect, useState } from 'react';
import { PRESENTATION } from '../../renderers/lookSettings';
import { XrmError } from '../api/errors';
import { useAuth } from '../auth/AuthProvider';
import { useAccess } from '../auth/roles';

/** Every mounted {@link useEnvironmentOverlay}'s re-read. */
const listeners = new Set<() => void>();

/** Makes every mounted {@link useEnvironmentOverlay} read the setting again (after "Model-driven apps look" is saved). */
export function refreshEnvironmentLook(): void {
  for (const listener of [...listeners]) listener();
}

const same = (a: string, b: string) => a.toLowerCase() === b.toLowerCase();
const set = (value: string | null | undefined): string | undefined => (value?.trim() ? value.trim() : undefined);

/**
 * The environment's presentation overlay web resource (`xrmverse_Presentation`): the environment's `organizationsetting`
 * value, else the definition's default, trimmed; undefined when neither is set, when the organization has no such definition
 * (then `organizationsettings` is not read) or when a read fails. A 403 (no read access to the settings) is silent; any other
 * failure gives its text as `error`, for the notice of R5. The previous name stays while a re-read is under way. Only a
 * System Administrator reads the setting: other users of the web admin (System Customizer…) get 403 on it, so for them, and
 * while access is still being resolved, nothing is read and there is no name (the xrmverse theme).
 */
export function useEnvironmentOverlay(): { name?: string; error?: string; reload: () => void } {
  const { api } = useAuth();
  const admin = useAccess().level === 'admin';
  const [state, setState] = useState<{ name?: string; error?: string }>({});
  const [generation, setGeneration] = useState(0);
  const reload = useCallback(() => setGeneration((g) => g + 1), []);

  useEffect(() => {
    listeners.add(reload);
    return () => {
      listeners.delete(reload);
    };
  }, [reload]);

  useEffect(() => {
    if (!api || !admin) return;
    const controller = new AbortController();
    const { signal } = controller;
    (async () => {
      const definition = (
        await api.get<{ value: { settingdefinitionid: string; uniquename: string; defaultvalue?: string | null }[] }>(
          `settingdefinitions?$select=settingdefinitionid,uniquename,defaultvalue&$filter=uniquename eq '${PRESENTATION}'`,
          { signal },
        )
      ).value.find((d) => same(d.uniquename, PRESENTATION));
      if (!definition) return undefined;
      const rows = (
        await api.get<{ value: { _settingdefinitionid_value?: string; value: string | null }[] }>(
          `organizationsettings?$select=_settingdefinitionid_value,value&$filter=_settingdefinitionid_value eq ${definition.settingdefinitionid}`,
          { signal },
        )
      ).value;
      const row = rows.find((r) => same(r._settingdefinitionid_value ?? '', definition.settingdefinitionid));
      return set(row?.value) ?? set(definition.defaultvalue);
    })().then(
      (value) => !signal.aborted && setState({ name: value }),
      (e: unknown) => {
        if (signal.aborted) return;
        if (e instanceof XrmError && e.status === 403) setState({});
        else setState({ error: e instanceof XrmError ? e.serverMessage : e instanceof Error ? e.message : String(e) });
      },
    );
    return () => controller.abort();
  }, [api, admin, generation]);

  return admin ? { ...state, reload } : { reload };
}
