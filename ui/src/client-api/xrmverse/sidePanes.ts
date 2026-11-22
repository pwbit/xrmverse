import { isWebResourceName } from '../scriptHost';

/** The strip state: 0 collapsed (only the tab strip shows), 1 expanded. */
export type SidePaneState = 0 | 1;

export interface SidePaneContent {
  webresourceName: string;
  data?: string;
}

export interface PaneSnapshotItem {
  paneId: string;
  title: string;
  imageSrc?: string;
  width: number;
  canClose: boolean;
  hideHeader: boolean;
  hidden: boolean;
  alwaysRender: boolean;
  /** `false` none, `true` a dot, a number 1-99 a count, `'99+'` above. */
  badge: boolean | number | '99+';
  content?: SidePaneContent;
  /** Increments on each `navigate`, so the shell remounts the IFrame. */
  contentKey: number;
  error?: string;
}

export interface PaneSnapshot {
  state: SidePaneState;
  selectedId?: string;
  /** Display order: `canClose: false` first, then the others, each group in creation order. */
  panes: PaneSnapshotItem[];
}

export interface SidePane {
  readonly paneId: string;
  title: string;
  imageSrc: string | undefined;
  width: number;
  canClose: boolean;
  hideHeader: boolean;
  hidden: boolean;
  alwaysRender: boolean;
  keepBadgeOnSelect: boolean;
  badge: boolean | number | null;
  navigate(pageInput: unknown): Promise<void>;
  select(): void;
  close(): void;
}

export interface SidePanesApi {
  state: SidePaneState;
  createPane(options?: unknown): Promise<SidePane>;
  getPane(paneId: string): SidePane | undefined;
  getAllPanes(): SidePane[];
  getSelectedPane(): SidePane | undefined;
}

export interface SidePaneStore {
  api: SidePanesApi;
  /** The current state; the object is replaced on each change, so it can be used as a snapshot. */
  snapshot(): PaneSnapshot;
  subscribe(fn: () => void): () => void;
  /**
   * For the shell: the web resource check failed for the content with `contentKey`; the pending `navigate` rejects with
   * `message` and the pane shows it. Ignored when `contentKey` is not the pane's current one (a superseded navigate).
   */
  setPaneError(paneId: string, contentKey: number, message: string): void;
  /**
   * For the shell: the web resource check for the content with `contentKey` is ready (the IFrame is created when the pane is
   * shown, at once for a shown pane); the pending `navigate` resolves. Ignored when `contentKey` is not the pane's current one.
   */
  markPaneLoaded(paneId: string, contentKey: number): void;
  /** Removes all panes; later `createPane` rejects. */
  dispose(): void;
}

interface PaneData {
  paneId: string;
  order: number;
  title: string;
  imageSrc?: string;
  width: number;
  canClose: boolean;
  hideHeader: boolean;
  hidden: boolean;
  alwaysRender: boolean;
  keepBadgeOnSelect: boolean;
  badge: boolean | number;
  content?: SidePaneContent;
  contentKey: number;
  error?: string;
  /** Settles the pending navigate, if any. */
  pending?: { resolve: () => void; reject: (e: Error) => void };
  open: boolean;
}

const DEFAULT_WIDTH = 340;

function badgeOf(value: unknown): boolean | number {
  if (value === true) return true;
  if (typeof value === 'number' && Number.isFinite(value) && value >= 1) return Math.floor(value);
  return false;
}

function requireBool(name: string, v: unknown): boolean {
  if (typeof v !== 'boolean') throw new TypeError(`${name} must be a boolean`);
  return v;
}

function requireWidth(v: unknown): number {
  if (typeof v !== 'number' || !Number.isFinite(v) || v <= 0) throw new TypeError('width must be a positive number');
  return v;
}

function requireImageSrc(v: unknown): string | undefined {
  if (v === undefined || v === null) return undefined;
  if (typeof v !== 'string' || !/^WebResources\/./.test(v) || !isWebResourceName(v.slice('WebResources/'.length))) {
    throw new TypeError('imageSrc must be a web resource path (WebResources/...)');
  }
  return v;
}

function requireTitle(v: unknown): string {
  if (typeof v !== 'string') throw new TypeError('title must be a string');
  return v;
}

/** The app's side panes; one store per open app. */
export function createSidePanes(): SidePaneStore {
  const panes = new Map<string, PaneData>();
  const handles = new Map<string, SidePane>();
  const listeners = new Set<() => void>();
  let state: SidePaneState = 0;
  let selectedId: string | undefined;
  let disposed = false;
  let seq = 0;
  let order = 0;
  let cached: PaneSnapshot | undefined;

  const emit = () => {
    cached = undefined;
    listeners.forEach((fn) => fn());
  };

  const ordered = (): PaneData[] => {
    const all = [...panes.values()].sort((a, b) => a.order - b.order);
    return [...all.filter((p) => !p.canClose), ...all.filter((p) => p.canClose)];
  };

  const settle = (p: PaneData, error?: Error) => {
    const pending = p.pending;
    if (!pending) return;
    p.pending = undefined;
    if (error) pending.reject(error);
    else pending.resolve();
  };

  const select = (p: PaneData) => {
    selectedId = p.paneId;
    state = 1;
    if (!p.keepBadgeOnSelect) p.badge = false;
    emit();
  };

  const close = (p: PaneData) => {
    if (!p.open) return;
    p.open = false;
    panes.delete(p.paneId);
    handles.delete(p.paneId);
    settle(p);
    if (selectedId === p.paneId) selectedId = ordered().find((x) => !x.hidden)?.paneId;
    emit();
  };

  /** A setter that changes a field and notifies; a closed pane ignores writes. */
  const set = <K extends keyof PaneData>(p: PaneData, key: K, value: PaneData[K]) => {
    if (!p.open) return;
    p[key] = value;
    emit();
  };

  const handleOf = (p: PaneData): SidePane => ({
    get paneId() {
      return p.paneId;
    },
    get title() {
      return p.title;
    },
    set title(v) {
      set(p, 'title', requireTitle(v));
    },
    get imageSrc() {
      return p.imageSrc;
    },
    set imageSrc(v) {
      set(p, 'imageSrc', requireImageSrc(v));
    },
    get width() {
      return p.width;
    },
    set width(v) {
      set(p, 'width', requireWidth(v));
    },
    get canClose() {
      return p.canClose;
    },
    set canClose(v) {
      set(p, 'canClose', requireBool('canClose', v));
    },
    get hideHeader() {
      return p.hideHeader;
    },
    set hideHeader(v) {
      set(p, 'hideHeader', requireBool('hideHeader', v));
    },
    get hidden() {
      return p.hidden;
    },
    set hidden(v) {
      set(p, 'hidden', requireBool('hidden', v));
    },
    get alwaysRender() {
      return p.alwaysRender;
    },
    set alwaysRender(v) {
      set(p, 'alwaysRender', requireBool('alwaysRender', v));
    },
    get keepBadgeOnSelect() {
      return p.keepBadgeOnSelect;
    },
    set keepBadgeOnSelect(v) {
      set(p, 'keepBadgeOnSelect', requireBool('keepBadgeOnSelect', v));
    },
    get badge() {
      return p.badge;
    },
    set badge(v) {
      set(p, 'badge', badgeOf(v));
    },
    async navigate(pageInput: unknown): Promise<void> {
      if (!p.open) throw new Error('The pane was closed');
      if (typeof pageInput !== 'object' || pageInput === null) throw new TypeError('The page input must be an object');
      const input = pageInput as Record<string, unknown>;
      if (input.pageType !== 'webresource') {
        throw new Error(`${String(input.pageType)} is not available in a side pane in this version`);
      }
      const name = input.webresourceName;
      if (!isWebResourceName(name)) throw new Error(`${String(name)} is not a web resource name.`);
      const data = input.data === undefined || input.data === null ? undefined : String(input.data);
      // A later navigate on the same pane supersedes this one, which resolves.
      settle(p);
      const done = new Promise<void>((resolve, reject) => {
        p.pending = { resolve, reject };
      });
      p.content = data === undefined ? { webresourceName: name } : { webresourceName: name, data };
      p.contentKey += 1;
      p.error = undefined;
      emit();
      return done;
    },
    select() {
      if (p.open) select(p);
    },
    close() {
      close(p);
    },
  });

  const api: SidePanesApi = {
    get state() {
      return state;
    },
    set state(v: SidePaneState) {
      if (v !== 0 && v !== 1) throw new TypeError('state must be 0 (collapsed) or 1 (expanded)');
      if (disposed || v === state) return;
      state = v;
      emit();
    },
    async createPane(options?: unknown): Promise<SidePane> {
      if (disposed) throw new Error('The app was closed');
      if (options !== undefined && (typeof options !== 'object' || options === null)) throw new TypeError('The options must be an object');
      const o = (options ?? {}) as Record<string, unknown>;
      let paneId: string;
      if (o.paneId === undefined) {
        do {
          paneId = `pane-${++seq}`;
        } while (panes.has(paneId));
      } else {
        if (typeof o.paneId !== 'string' || o.paneId === '') throw new TypeError('paneId must be a non-empty string');
        paneId = o.paneId;
      }
      const opt = <T>(key: string, check: (v: unknown) => T, fallback: T): T => (o[key] === undefined ? fallback : check(o[key]));
      const p: PaneData = {
        paneId,
        order: 0,
        title: opt('title', requireTitle, ''),
        imageSrc: requireImageSrc(o.imageSrc),
        width: opt('width', requireWidth, DEFAULT_WIDTH),
        canClose: opt('canClose', (v) => requireBool('canClose', v), true),
        hideHeader: opt('hideHeader', (v) => requireBool('hideHeader', v), false),
        hidden: opt('hidden', (v) => requireBool('hidden', v), false),
        alwaysRender: opt('alwaysRender', (v) => requireBool('alwaysRender', v), false),
        keepBadgeOnSelect: opt('keepBadgeOnSelect', (v) => requireBool('keepBadgeOnSelect', v), false),
        badge: false,
        contentKey: 0,
        open: true,
      };
      const isSelected = opt('isSelected', (v) => requireBool('isSelected', v), true);
      if (panes.has(paneId)) throw new Error(`A pane with the id ${paneId} already exists`);
      p.order = ++order;
      panes.set(paneId, p);
      const handle = handleOf(p);
      handles.set(paneId, handle);
      if (isSelected) select(p);
      else emit();
      return handle;
    },
    getPane: (paneId) => handles.get(paneId),
    getAllPanes: () => ordered().map((p) => handles.get(p.paneId)!),
    getSelectedPane: () => (selectedId === undefined ? undefined : handles.get(selectedId)),
  };

  return {
    api,
    snapshot() {
      cached ??= {
        state,
        selectedId,
        panes: ordered().map((p) => ({
          paneId: p.paneId,
          title: p.title,
          imageSrc: p.imageSrc,
          width: p.width,
          canClose: p.canClose,
          hideHeader: p.hideHeader,
          hidden: p.hidden,
          alwaysRender: p.alwaysRender,
          badge: typeof p.badge === 'number' && p.badge > 99 ? '99+' : p.badge,
          content: p.content,
          contentKey: p.contentKey,
          error: p.error,
        })),
      };
      return cached;
    },
    subscribe(fn) {
      listeners.add(fn);
      return () => {
        listeners.delete(fn);
      };
    },
    setPaneError(paneId, contentKey, message) {
      const p = panes.get(paneId);
      if (!p || p.contentKey !== contentKey) return;
      p.error = message;
      settle(p, new Error(message));
      emit();
    },
    markPaneLoaded(paneId, contentKey) {
      const p = panes.get(paneId);
      if (!p || p.contentKey !== contentKey) return;
      settle(p);
    },
    dispose() {
      if (disposed) return;
      disposed = true;
      for (const p of panes.values()) {
        p.open = false;
        settle(p);
      }
      panes.clear();
      handles.clear();
      selectedId = undefined;
      state = 0;
      emit();
    },
  };
}
