/**
 * The script host: a hidden same-origin `about:blank` IFrame that holds one form's libraries. Its own window keeps the
 * libraries' globals off the shell, and removing the IFrame ends their timers and listeners (the form was left).
 */

/** Loads one library into `win`; rejects when it cannot be fetched or does not evaluate. */
export type LibraryLoader = (win: Window, url: string, name: string) => Promise<void>;

export interface ScriptHost {
  window: Window;
  /** Names of the libraries that did not load. */
  failed: string[];
  /** The function a dotted name such as `OP365.Account.onLoad` points to, bound to its owner; undefined when it is not one. */
  resolve(functionName: string): Function | undefined;
  dispose(): void;
}

/** `/WebResources/{name}`: slashes stay, every other character is encoded per path segment. */
export const webResourceUrl = (name: string): string => `/WebResources/${name.split('/').map(encodeURIComponent).join('/')}`;

/** A web resource name that is not one (it would leave `/WebResources/`, or is a scheme, an absolute path, empty). */
export class WebResourceNameError extends Error {
  constructor(readonly resourceName: unknown) {
    super(`${String(resourceName)} is not a web resource name.`);
  }
}

const decoded = (s: string): string => {
  try {
    return decodeURIComponent(s);
  } catch {
    return s;
  }
};

/** True for a name that stays under `/WebResources/`: no scheme, no `..` segment (also encoded), no leading `/`, no backslash. */
export function isWebResourceName(name: unknown): name is string {
  if (typeof name !== 'string' || name.trim() === '') return false;
  const path = name.split('?')[0]!;
  return [path, decoded(path), decoded(decoded(path))].every((p) => !/^\s*[a-z][a-z0-9+.-]*:/i.test(p) && !p.includes('..') && !p.startsWith('/') && !p.includes('\\') && !p.includes('#'));
}

/**
 * The address of a web resource with query parameters: a query string in `name` is kept and `query` is appended after it
 * (`data`, the record parameters of a form control).
 */
export function webResourceSrc(name: string, query: Record<string, string> = {}): string {
  if (!isWebResourceName(name)) throw new WebResourceNameError(name);
  const at = name.indexOf('?');
  const own = at < 0 ? '' : name.slice(at + 1);
  const added = Object.entries(query).map(([k, v]) => `${encodeURIComponent(k)}=${encodeURIComponent(v)}`);
  const q = [own, ...added].filter(Boolean).join('&');
  return `${webResourceUrl(at < 0 ? name : name.slice(0, at))}${q ? `?${q}` : ''}`;
}

/** Appends a `<script src>`. A script that throws while it evaluates (a syntax error) reports through the window's `error` event. */
export const scriptElementLoader: LibraryLoader = (win, url) =>
  new Promise<void>((resolve, reject) => {
    const doc = win.document;
    const absolute = new URL(url, doc.baseURI === 'about:blank' ? win.parent.location.href : doc.baseURI).href;
    let broken = false;
    const onError = (e: Event) => {
      if ((e as ErrorEvent).filename === absolute) broken = true;
    };
    win.addEventListener('error', onError);
    const script = doc.createElement('script');
    script.onload = () => {
      win.removeEventListener('error', onError);
      if (broken) reject(new Error('script error'));
      else resolve();
    };
    script.onerror = () => {
      win.removeEventListener('error', onError);
      reject(new Error('not loaded'));
    };
    script.src = url;
    doc.head.appendChild(script);
  });

let loader: LibraryLoader = scriptElementLoader;

/** Replaces the library loader (jsdom does not run `<script src>`); undefined restores the default. */
export function setLibraryLoader(next: LibraryLoader | undefined): void {
  loader = next ?? scriptElementLoader;
}

/**
 * Creates the host, calls `install(window)` (the Client API goes there) and loads `libraries` one after the other, each after
 * the previous one finished. A library that fails is listed in `failed` and the next one still loads.
 */
export async function createScriptHost(
  libraries: string[],
  install: (win: Window) => void,
  load: LibraryLoader = loader,
  signal?: AbortSignal,
): Promise<ScriptHost> {
  const frame = document.createElement('iframe');
  frame.src = 'about:blank';
  frame.style.display = 'none';
  frame.setAttribute('aria-hidden', 'true');
  frame.tabIndex = -1;
  document.body.appendChild(frame);
  const win = frame.contentWindow;
  if (!win) {
    frame.remove();
    throw new Error('The script host window could not be created.');
  }
  const failed: string[] = [];
  let disposed = false;
  const dispose = () => {
    if (disposed) return;
    disposed = true;
    // Browsers stop an IFrame's timers on removal; jsdom needs close() for that.
    win.close();
    frame.remove();
  };
  // An abort (the form was left while the libraries were loading) removes the window at once; no further library loads.
  signal?.addEventListener('abort', dispose, { once: true });
  try {
    if (signal?.aborted) throw new DOMException('The script host was aborted.', 'AbortError');
    install(win);
    for (const name of libraries) {
      try {
        await load(win, webResourceUrl(name), name);
      } catch {
        failed.push(name);
      }
      if (disposed) throw new DOMException('The script host was aborted.', 'AbortError');
    }
  } catch (e) {
    dispose();
    throw e;
  }
  return {
    window: win,
    failed,
    resolve(functionName) {
      try {
        let owner: unknown = win;
        let value: unknown = win;
        for (const part of functionName.trim().split('.')) {
          owner = value;
          if (value === null || value === undefined) return undefined;
          value = (value as Record<string, unknown>)[part];
        }
        return typeof value === 'function' ? (value as Function).bind(owner) : undefined;
      } catch {
        return undefined;
      }
    },
    dispose,
  };
}
