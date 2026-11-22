import { useEffect, useState } from 'react';
import { webResourceUrl } from '../../client-api/scriptHost';
import { parsePresentation, type PresentationModel } from '../../parsers/presentationJson';
import { t } from '../../shell/i18n/t';

interface Read {
  model?: PresentationModel;
  notice?: string;
}

const unreadable = (name: string, reason: string): Read => ({ notice: t('presentation.unreadable', { name, reason }) });

/** What the overlay text gives: the model, or the notice that says why the default look is used. Warnings go to the console. */
function interpret(name: string, text: string): Read {
  let raw: unknown;
  try {
    raw = JSON.parse(text);
  } catch {
    return unreadable(name, t('presentation.reason.notJson'));
  }
  if (typeof raw !== 'object' || raw === null || Array.isArray(raw)) return unreadable(name, t('presentation.reason.notObject'));
  const version = (raw as { version?: unknown }).version;
  if (version === undefined) return { notice: t('presentation.noVersion') };
  if (version !== 1) return { notice: t('presentation.version', { v: String(version) }) };
  const parsed = parsePresentation(text);
  for (const w of parsed.warnings) console.warn('[xrmverse presentation]', w);
  if (parsed.error || !parsed.model) return unreadable(name, parsed.error ?? t('presentation.reason.notObject'));
  return { model: parsed.model };
}

/**
 * Reads the presentation overlay web resource `name` (`/WebResources/{name}`, with the web session cookie like every web
 * resource) and parses it. Without a name nothing is read. A missing, unreadable or non-JSON overlay gives no model and the
 * notice of the Global Constraints; the caller then uses the default look.
 */
export function usePresentation(name?: string): { model?: PresentationModel; notice?: string; loading: boolean } {
  const [state, setState] = useState<Read & { name: string }>();
  useEffect(() => {
    if (!name) return;
    const abort = new AbortController();
    fetch(webResourceUrl(name), { credentials: 'same-origin', signal: abort.signal })
      .then(async (response): Promise<Read> => {
        if (!response.ok) return unreadable(name, t('presentation.reason.status', { status: response.status }));
        return interpret(name, await response.text());
      })
      .catch((e: unknown): Read => unreadable(name, t('presentation.reason.network', { detail: e instanceof Error ? e.message : String(e) })))
      .then((read) => {
        if (!abort.signal.aborted) setState({ ...read, name });
      });
    return () => abort.abort();
  }, [name]);
  if (!name) return { loading: false };
  if (state?.name !== name) return { loading: true };
  return { model: state.model, notice: state.notice, loading: false };
}
