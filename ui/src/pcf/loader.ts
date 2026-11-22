import { scriptElementLoader } from '../client-api/scriptHost';
import type { XrmApi } from '../shell/api/client';
import { literal } from '../shell/api/odata';
import { isPermanentFailure, PcfLoadError } from './errors';
import { parseManifest, type PcfManifest } from './manifest';
import { installPlatformLibraries } from './platformLibraries';

/**
 * Loads a PCF control into the shell window (ruling R6): reads its manifest, publishes the platform libraries, injects the
 * stylesheets and the scripts, and takes the constructor the bundle hands to `ComponentFramework.registerControl`. Each
 * control loads once per session (a page reload starts a new one). A failure rejects with a {@link PcfLoadError} whose
 * message is the reason the form shows (ruling R7); a permanent one is kept for the session, a transient one (a request
 * or a script that failed, a registration that timed out) is forgotten so the next form open tries again. Loads of the same
 * `namespace.constructor` run one after the other, so each takes the constructor its own bundle registers.
 */

/** The members the host calls on a control instance (Learn, `StandardControl` and `ReactControl`). */
export interface PcfControl {
  init(context: unknown, notifyOutputChanged?: () => void, state?: Record<string, unknown>, container?: HTMLDivElement): void;
  /** A standard control draws into its container; a virtual control returns a React element. */
  updateView(context: unknown): unknown;
  getOutputs?(): Record<string, unknown>;
  destroy(): void;
}

export type PcfConstructor = new () => PcfControl;

export interface LoadedControl {
  manifest: PcfManifest;
  ctor: PcfConstructor;
}

/** The columns of a `customcontrol` row the loader reads. */
export interface CustomControlRow {
  name: string;
  manifest: string;
  version?: string | null;
}

export interface LoaderDeps {
  /** The `customcontrol` row of `name`, or undefined when there is none. */
  fetchManifest: (name: string) => Promise<CustomControlRow | undefined>;
  /** Where the control files are served; default `/Controls`. */
  baseUrl?: string;
  /** Loads one script into the shell window; default a `<script src>` element. */
  loadScript?: (url: string) => Promise<void>;
  /** How long scripts and registration may take; default 10 s. */
  timeoutMs?: number;
}

const DEFAULT_BASE = '/Controls';
const REGISTER_TIMEOUT_MS = 10_000;

/** `/Controls/{name}/{path}`: the name and each path segment encoded. */
export function controlFileUrl(name: string, path: string, baseUrl: string = DEFAULT_BASE): string {
  return `${baseUrl}/${encodeURIComponent(name)}/${path.split('/').map(encodeURIComponent).join('/')}`;
}

/** Reads the manifest through the Web API: `customcontrols?$filter=name eq '{name}'&$select=name,manifest,version`. */
export function manifestFetcher(api: Pick<XrmApi, 'get'>): LoaderDeps['fetchManifest'] {
  return async (name) => {
    const filter = encodeURIComponent(`name eq ${literal(name)}`);
    const found = await api.get<{ value: CustomControlRow[] }>(`customcontrols?$filter=${filter}&$select=name,manifest,version`);
    return found.value[0];
  };
}

const defaultScriptLoader = (url: string): Promise<void> => scriptElementLoader(window, url, url);
let scriptLoader: (url: string) => Promise<void> = defaultScriptLoader;

/** Replaces the default script loader (jsdom does not run `<script src>`); undefined restores it. */
export function setScriptLoader(next: ((url: string) => Promise<void>) | undefined): void {
  scriptLoader = next ?? defaultScriptLoader;
}

const cache = new Map<string, Promise<LoadedControl>>();
/** Constructors by `namespace.constructor`, lower case. */
const registry = new Map<string, PcfConstructor>();
const waiters = new Map<string, Set<(ctor: PcfConstructor) => void>>();
/** The last load queued per `namespace.constructor`, lower case. */
const chains = new Map<string, Promise<void>>();

const keyOf = (name: string): string => name.trim().toLowerCase();

function registerControl(name: unknown, ctor: unknown): void {
  if (typeof name !== 'string' || typeof ctor !== 'function') return;
  const key = keyOf(name);
  registry.set(key, ctor as PcfConstructor);
  const pending = waiters.get(key);
  waiters.delete(key);
  pending?.forEach((resolve) => resolve(ctor as PcfConstructor));
}

/** The constructor registered under `namespace.constructor` in this session, if any. */
export function registeredControl(name: string): PcfConstructor | undefined {
  return registry.get(keyOf(name));
}

/** Makes `window.ComponentFramework.registerControl` record constructors; other members of an existing object stay. */
function installRegistration(win: Window): void {
  const target = win as unknown as { ComponentFramework?: Record<string, unknown> };
  const existing = target.ComponentFramework;
  if (existing && existing.registerControl === registerControl) return;
  target.ComponentFramework = { ...(existing ?? {}), registerControl };
}

/** Forgets every loaded control and registration (tests). */
export function resetPcfLoader(): void {
  cache.clear();
  registry.clear();
  waiters.clear();
}

const reasonOf = (e: unknown): string => (e instanceof Error && e.message ? e.message : String(e));

/**
 * The control `name` (the `customcontrol` row name), loaded once per session. A permanent failure is kept too; after a
 * transient one (see {@link PcfLoadError.transient}) the next call loads again.
 */
export function loadControl(name: string, deps: LoaderDeps): Promise<LoadedControl> {
  const key = keyOf(name);
  let loading = cache.get(key);
  if (!loading) {
    const started = load(name, deps);
    loading = started;
    cache.set(key, started);
    started.catch((e: unknown) => {
      if (!isPermanentFailure(e) && cache.get(key) === started) cache.delete(key);
    });
  }
  return loading;
}

/** Runs `work` after every earlier load of the same `namespace.constructor` settled, so one registration answers one load. */
async function serialized<T>(key: string, work: () => Promise<T>): Promise<T> {
  const previous = chains.get(key) ?? Promise.resolve();
  let release: () => void = () => {};
  const mine = new Promise<void>((resolve) => (release = resolve));
  const tail = previous.then(() => mine);
  chains.set(key, tail);
  try {
    await previous;
    return await work();
  } finally {
    release();
    if (chains.get(key) === tail) chains.delete(key);
  }
}

async function load(name: string, deps: LoaderDeps): Promise<LoadedControl> {
  let row: CustomControlRow | undefined;
  try {
    row = await deps.fetchManifest(name);
  } catch (e) {
    throw new PcfLoadError('manifest', `its manifest could not be read (${reasonOf(e)})`, { transient: true });
  }
  if (!row || typeof row.manifest !== 'string') throw new PcfLoadError('notFound', 'no custom control with this name is installed');
  const manifest = parseManifest(row.manifest);
  if (manifest.code.length === 0) throw new PcfLoadError('manifest', 'its manifest names no code file');
  installPlatformLibraries(manifest.platformLibraries, window);

  const key = keyOf(`${manifest.namespace}.${manifest.constructor}`);
  // Files are served under the row's own name (its case may differ from the form's).
  const served = row.name || name;
  return serialized(key, () => install(manifest, served, key, deps));
}

/** Injects the stylesheets and scripts of `manifest` and waits for its registration. */
async function install(manifest: PcfManifest, served: string, key: string, deps: LoaderDeps): Promise<LoadedControl> {
  const baseUrl = deps.baseUrl ?? DEFAULT_BASE;
  const loadScript = deps.loadScript ?? scriptLoader;
  // A constructor registered under this key before (another control, an earlier bundle) is never used for this load.
  registry.delete(key);
  installRegistration(window);
  const links: HTMLLinkElement[] = [];
  for (const css of manifest.css) {
    const link = document.createElement('link');
    link.rel = 'stylesheet';
    link.setAttribute('href', controlFileUrl(served, css.path, baseUrl));
    link.dataset.pcf = served;
    document.head.appendChild(link);
    links.push(link);
  }
  const scriptUrls = manifest.code.map((code) => controlFileUrl(served, code.path, baseUrl));
  // A failed load leaves nothing behind, so the retry on the next form open injects every file again.
  const uninstall = () => {
    links.forEach((l) => l.remove());
    document.head.querySelectorAll('script[src]').forEach((s) => {
      if (scriptUrls.includes(s.getAttribute('src') ?? '')) s.remove();
    });
  };

  let resolveRegistered: (ctor: PcfConstructor) => void = () => {};
  const registered = new Promise<PcfConstructor>((resolve) => (resolveRegistered = resolve));
  const waiting = waiters.get(key) ?? new Set();
  waiting.add(resolveRegistered);
  waiters.set(key, waiting);

  const limit = deps.timeoutMs ?? REGISTER_TIMEOUT_MS;
  let timer: ReturnType<typeof setTimeout> | undefined;
  const timeout = new Promise<never>((_, reject) => {
    const why = `it did not register ${manifest.namespace}.${manifest.constructor} within ${limit / 1000} s`;
    timer = setTimeout(() => reject(new PcfLoadError('timeout', why)), limit);
  });
  const scripts = (async () => {
    for (const [i, code] of manifest.code.entries()) {
      try {
        await loadScript(scriptUrls[i]);
      } catch (e) {
        throw new PcfLoadError('script', `${code.path} could not be loaded (${reasonOf(e)})`);
      }
    }
  })();
  try {
    const ctor = await Promise.race([Promise.all([scripts, registered]).then(([, c]) => c), timeout]);
    return { manifest, ctor };
  } catch (e) {
    uninstall();
    throw e;
  } finally {
    clearTimeout(timer);
    waiters.get(key)?.delete(resolveRegistered);
  }
}
