import { t } from '../i18n/t';

/** The anonymous `GET /ui/config.json` the server publishes (public values only). */
export interface UiConfig {
  authMode: string;
  audience: string;
  authority?: string | null;
  clientId?: string | null;
  scope?: string | null;
  apiVersion: string;
}

export async function loadUiConfig(fetchImpl: typeof fetch = (i, init) => globalThis.fetch(i, init)): Promise<UiConfig> {
  let response: Response;
  try {
    response = await fetchImpl('/ui/config.json', { headers: { Accept: 'application/json' } });
  } catch (e) {
    throw new Error(t('auth.config.loadFailed', { detail: e instanceof Error ? e.message : String(e) }));
  }
  if (!response.ok) throw new Error(t('auth.config.loadFailed', { detail: `HTTP ${response.status}` }));
  try {
    return (await response.json()) as UiConfig;
  } catch {
    throw new Error(t('auth.config.loadFailed', { detail: t('api.error.invalidBody') }));
  }
}

/** Entra mode needs the SPA registration (`Ui:Entra:ClientId`) and the API scope (`Ui:Entra:Scope`); null when complete. */
export function entraConfigError(config: UiConfig): string | null {
  return config.clientId && config.scope && config.authority ? null : t('auth.config.entraMissing');
}

