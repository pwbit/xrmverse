import { useEffect, useState } from 'react';

export type WebResourceCheck = { status: 'checking' } | { status: 'ready' } | { status: 'missing' } | { status: 'error'; detail: string };

/**
 * Asks the server for a web resource before it is framed. The server answers 404 for a resource that does not exist and for
 * one the caller may not read, so both are `missing`; any other failure is an `error` that is not reported as missing.
 * An `undefined` address (not a web resource, for example an external IFRAME) is `ready` without a request.
 */
export function useWebResourceCheck(src: string | undefined): WebResourceCheck {
  const [state, setState] = useState<{ src: string; check: WebResourceCheck } | undefined>();
  useEffect(() => {
    if (src === undefined) return;
    const abort = new AbortController();
    fetch(src, { credentials: 'same-origin', signal: abort.signal }).then(
      (response) =>
        setState({
          src,
          check: response.ok ? { status: 'ready' } : response.status === 404 ? { status: 'missing' } : { status: 'error', detail: `${response.status} ${response.statusText}`.trim() },
        }),
      (e: unknown) => {
        if (!abort.signal.aborted) setState({ src, check: { status: 'error', detail: e instanceof Error ? e.message : String(e) } });
      },
    );
    return () => abort.abort();
  }, [src]);
  if (src === undefined) return { status: 'ready' };
  return state?.src === src ? state.check : { status: 'checking' };
}

/** The address when it is a web resource of this server (`/WebResources/...`), the only kind that is checked. */
export const checkedAddress = (src: string): string | undefined => (src.startsWith('/WebResources/') ? src : undefined);
