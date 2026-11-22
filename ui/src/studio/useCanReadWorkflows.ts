import { useEffect, useState } from 'react';
import type { XrmApi } from '../shell/api/client';
import { useOptionalAuth } from '../shell/auth/AuthProvider';
import { XrmError } from '../shell/api/errors';
import { listWorkflows } from './api/studioApi';

/**
 * Whether the signed-in user may read the processes (ruling W7): the success of `GET /authoring/workflows`, asked once per
 * session. Only a definite answer is kept: success (true) or a 401/403 (false). Any other failure (network, 5xx) answers
 * false but is forgotten, so a later visit asks again. The answer is kept by the session's {@link XrmApi} (a new one is made at
 * every sign-in), so a sign-out forgets it.
 */
const answers = new WeakMap<XrmApi, Promise<boolean>>();

export function canReadWorkflows(api: XrmApi): Promise<boolean> {
  let answer = answers.get(api);
  if (!answer) {
    const asked: Promise<boolean> = listWorkflows(api).then(
      () => true,
      (e: unknown) => {
        if (!(e instanceof XrmError && (e.status === 401 || e.status === 403)) && answers.get(api) === asked) answers.delete(api);
        return false;
      },
    );
    answer = asked;
    answers.set(api, answer);
  }
  return answer;
}

/** `undefined` while the answer is not known yet (or nobody is signed in); it drives the studio's tile and header link. */
export function useCanReadWorkflows(): boolean | undefined {
  const api = useOptionalAuth()?.api;
  const [state, setState] = useState<{ api?: XrmApi; can?: boolean }>({});
  useEffect(() => {
    if (!api) return;
    let live = true;
    void canReadWorkflows(api).then((can) => live && setState({ api, can }));
    return () => {
      live = false;
    };
  }, [api]);
  return api && state.api === api ? state.can : undefined;
}
