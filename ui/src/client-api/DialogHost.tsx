import {
  Body1,
  Button,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  Field,
  makeStyles,
  mergeClasses,
  MessageBar,
  MessageBarBody,
  Select,
  Spinner,
  Text,
  tokens,
} from '@fluentui/react-components';
import { useCallback, useEffect, useMemo, useRef, useState, useSyncExternalStore, type CSSProperties, type ReactNode, type RefObject } from 'react';
import { EntityPicker, type PickedEntity } from '../admin/common/EntityPicker';
import { entityPath } from '../admin/tables/tableApi';
import { useLoad } from '../admin/tables/useLoad';
import { t } from '../shell/i18n/t';
import { isWebResourceName, webResourceSrc, WebResourceNameError } from './scriptHost';
import { useWebResourceCheck } from './useWebResourceCheck';
import { bracedUpper } from './values';

/** A dialog size: a number of pixels, or Learn's SizeValue `{ value, unit }` in `px` or `%`. */
export type DialogLength = number | { value: number; unit?: 'px' | '%' };

export interface DialogSize {
  width?: DialogLength;
  height?: DialogLength;
  /** 1 centre (default), 2 on the far side. */
  position?: number;
}

export interface AlertStrings {
  text: string;
  title?: string;
  confirmButtonLabel?: string;
}

export interface ConfirmStrings {
  text: string;
  title?: string;
  subtitle?: string;
  confirmButtonLabel?: string;
  cancelButtonLabel?: string;
}

export interface ErrorOptions {
  message?: string;
  details?: string;
  errorCode?: number;
}

/** A system view of `lookupObjects`: the `<filter>` elements of its FetchXML narrow the search of its table. */
export interface LookupView {
  id: string;
  name: string;
  entity: string;
  filters: string[];
}

export interface LookupOptions {
  entityTypes: string[];
  defaultEntityType?: string;
  allowMultiSelect?: boolean;
  filters?: { filterXml: string; entityLogicalName: string }[];
  /** The views to choose from (`viewIds`); a table with views listed is searched through one of them only. */
  views?: LookupView[];
  /** The view a table opens on (`defaultViewId`). */
  defaultViewId?: string;
  /** The search term the lookup opens with. */
  searchText?: string;
}

export interface LookupResult {
  id: string;
  name: string;
  entityType: string;
}

export interface WebResourceDialog extends DialogSize {
  name: string;
  data?: string;
  title?: string;
}

/** A record form in a dialog (#133): `url` is the record page (`main.aspx`) the dialog opens on. */
export interface RecordDialog extends DialogSize {
  url: string;
  title?: string;
  /**
   * The history state the dialog's form opens with, as a page navigation would pass it (a subgrid's New hands over the
   * relationship to associate). Set by the shell only, never by `Xrm.Navigation.navigateTo`.
   */
  state?: unknown;
}

/** The record a record dialog last showed saved: the one it opened, or the one its create form created. */
export interface SavedRecord {
  entityType: string;
  id: string;
  name: string;
}

/** Renders the form of a record dialog; `done` closes the dialog with the record saved last (if any). */
export type RecordDialogRenderer = (options: RecordDialog, done: (saved: SavedRecord | undefined) => void) => ReactNode;

/** The shell's dialogs for the Client API: each promise resolves when its dialog closes. */
export interface DialogHost {
  alert(strings: AlertStrings, size?: DialogSize): Promise<void>;
  confirm(strings: ConfirmStrings, size?: DialogSize): Promise<boolean>;
  error(options: ErrorOptions): Promise<void>;
  /** Shows the progress overlay; calls nest by count, one {@link closeProgress} per show. */
  showProgress(message: string): void;
  closeProgress(): void;
  lookup(options: LookupOptions): Promise<LookupResult[]>;
  /** An HTML web resource in a dialog; the page's `window.close()` closes it. */
  webResource(options: WebResourceDialog): Promise<void>;
  /** A record form in a dialog; resolves when it closes, with the record created or opened when it was saved. */
  record(options: RecordDialog): Promise<SavedRecord | undefined>;
}

type Entry =
  | { id: number; kind: 'alert'; strings: AlertStrings; size?: DialogSize; done: () => void }
  | { id: number; kind: 'confirm'; strings: ConfirmStrings; size?: DialogSize; done: (confirmed: boolean) => void }
  | { id: number; kind: 'error'; options: ErrorOptions; done: () => void }
  | { id: number; kind: 'lookup'; options: LookupOptions; done: (picked: LookupResult[]) => void }
  | { id: number; kind: 'webResource'; options: WebResourceDialog; done: () => void }
  | { id: number; kind: 'record'; options: RecordDialog; done: (saved: SavedRecord | undefined) => void };

interface Snapshot {
  dialogs: Entry[];
  progress: { count: number; message: string };
}

/** The dialog host with the store {@link DialogHostView} renders. */
export interface DialogHostStore extends DialogHost {
  subscribe(listener: () => void): () => void;
  snapshot(): Snapshot;
  /** Hides the progress overlay whatever its count: the form whose scripts showed it was left. */
  clearProgress(): void;
}

type Distribute<T> = T extends unknown ? Omit<T, 'id' | 'done'> : never;

export function createDialogHost(): DialogHostStore {
  let state: Snapshot = { dialogs: [], progress: { count: 0, message: '' } };
  const listeners = new Set<() => void>();
  let next = 0;
  const set = (s: Snapshot) => {
    state = s;
    listeners.forEach((l) => l());
  };
  function open<R>(entry: Distribute<Entry>): Promise<R> {
    return new Promise<R>((resolve) => {
      const id = ++next;
      const done = (value: R) => {
        set({ ...state, dialogs: state.dialogs.filter((d) => d.id !== id) });
        resolve(value);
      };
      set({ ...state, dialogs: [...state.dialogs, { ...entry, id, done } as Entry] });
    });
  }
  return {
    subscribe(listener) {
      listeners.add(listener);
      return () => listeners.delete(listener);
    },
    snapshot: () => state,
    alert: (strings, size) => open<void>({ kind: 'alert', strings, size }),
    confirm: (strings, size) => open<boolean>({ kind: 'confirm', strings, size }),
    error: (options) => open<void>({ kind: 'error', options }),
    lookup: (options) => open<LookupResult[]>({ kind: 'lookup', options }),
    record: (options) => open<SavedRecord | undefined>({ kind: 'record', options }),
    webResource: (options) => (isWebResourceName(options.name) ? open<void>({ kind: 'webResource', options }) : Promise.reject(new WebResourceNameError(options.name))),
    showProgress: (message) => set({ ...state, progress: { count: state.progress.count + 1, message } }),
    closeProgress: () => {
      if (state.progress.count > 0) set({ ...state, progress: { ...state.progress, count: state.progress.count - 1 } });
    },
    clearProgress: () => {
      if (state.progress.count > 0) set({ ...state, progress: { count: 0, message: '' } });
    },
  };
}

const useStyles = makeStyles({
  overlay: {
    position: 'fixed',
    inset: 0,
    zIndex: 1000000,
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    backgroundColor: tokens.colorBackgroundOverlay,
  },
  progress: {
    padding: '24px 32px',
    borderRadius: tokens.borderRadiusLarge,
    backgroundColor: tokens.colorNeutralBackground1,
    boxShadow: tokens.shadow16,
  },
  frameSurface: { display: 'flex', flexDirection: 'column', maxWidth: '100vw', maxHeight: '100vh' },
  frameBody: { display: 'flex', flexDirection: 'column', flexGrow: 1, minHeight: 0 },
  frame: { flexGrow: 1, width: '100%', minHeight: '200px', border: 'none' },
  side: { marginRight: 0, marginLeft: 'auto', height: '100vh' },
  details: { whiteSpace: 'pre-wrap', fontFamily: tokens.fontFamilyMonospace },
});

const lengthOf = (l: DialogLength | undefined): string | undefined => {
  if (typeof l === 'number' && Number.isFinite(l)) return `${l}px`;
  if (l && typeof l === 'object' && typeof l.value === 'number' && Number.isFinite(l.value)) return `${l.value}${l.unit === '%' ? '%' : 'px'}`;
  return undefined;
};

function sizeStyle(size: DialogSize | undefined): CSSProperties {
  const style: CSSProperties = {};
  const width = lengthOf(size?.width);
  const height = lengthOf(size?.height);
  if (width) {
    style.width = width;
    style.maxWidth = '100vw';
  }
  if (height) style.height = height;
  return style;
}

/** The dialogs and the progress overlay of {@link createDialogHost}; mounted once by the app shell. */
export function DialogHostView({ host, renderRecord }: { host: DialogHostStore; /** The form of a record dialog (#133). */ renderRecord?: RecordDialogRenderer }) {
  const styles = useStyles();
  const snapshot = useSyncExternalStore(host.subscribe, host.snapshot);
  return (
    <>
      {snapshot.dialogs.map((d) =>
        d.kind === 'record' ? (
          <RecordFrame key={d.id} entry={d} render={renderRecord} />
        ) : (
          <HostedDialog key={d.id} entry={d} />
        ),
      )}
      {snapshot.progress.count > 0 && (
        <div className={styles.overlay}>
          <div className={styles.progress} role="alertdialog" aria-modal="true" aria-label={snapshot.progress.message} aria-busy="true">
            <Spinner label={snapshot.progress.message} />
          </div>
        </div>
      )}
    </>
  );
}

/** A record dialog: its form comes from the app shell; without a renderer it closes at once, nothing saved. */
function RecordFrame({ entry, render }: { entry: Extract<Entry, { kind: 'record' }>; render?: RecordDialogRenderer }) {
  useEffect(() => {
    if (!render) entry.done(undefined);
  }, [entry, render]);
  return render ? <>{render(entry.options, entry.done)}</> : null;
}

/** The surface style of a dialog of `size`: on the far side for position 2. */
export function dialogSurface(size: DialogSize | undefined): { side: boolean; style: CSSProperties } {
  return { side: size?.position === 2, style: sizeStyle(size) };
}

function HostedDialog({ entry }: { entry: Exclude<Entry, { kind: 'record' }> }) {
  switch (entry.kind) {
    case 'alert':
      return <AlertDialog entry={entry} />;
    case 'confirm':
      return <ConfirmDialog entry={entry} />;
    case 'error':
      return <ErrorDialog entry={entry} />;
    case 'lookup':
      return <LookupDialog entry={entry} />;
    case 'webResource':
      return <WebResourceFrameDialog entry={entry} />;
  }
}

function AlertDialog({ entry }: { entry: Extract<Entry, { kind: 'alert' }> }) {
  const { strings } = entry;
  return (
    <Dialog open onOpenChange={(_, d) => !d.open && entry.done()}>
      <DialogSurface style={sizeStyle(entry.size)} aria-label={strings.title ? undefined : strings.text}>
        <DialogBody>
          {strings.title && <DialogTitle>{strings.title}</DialogTitle>}
          <DialogContent>{strings.text}</DialogContent>
          <DialogActions>
            <Button appearance="primary" onClick={() => entry.done()}>
              {strings.confirmButtonLabel || t('xrm.dialog.ok')}
            </Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}

function ConfirmDialog({ entry }: { entry: Extract<Entry, { kind: 'confirm' }> }) {
  const { strings } = entry;
  return (
    <Dialog open onOpenChange={(_, d) => !d.open && entry.done(false)}>
      <DialogSurface style={sizeStyle(entry.size)} aria-label={strings.title ? undefined : strings.text}>
        <DialogBody>
          {strings.title && <DialogTitle>{strings.title}</DialogTitle>}
          <DialogContent>
            {strings.subtitle && <Body1 block>{strings.subtitle}</Body1>}
            {strings.text}
          </DialogContent>
          <DialogActions>
            <Button appearance="primary" onClick={() => entry.done(true)}>
              {strings.confirmButtonLabel || t('xrm.dialog.ok')}
            </Button>
            <Button appearance="secondary" onClick={() => entry.done(false)}>
              {strings.cancelButtonLabel || t('xrm.dialog.cancel')}
            </Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}

function ErrorDialog({ entry }: { entry: Extract<Entry, { kind: 'error' }> }) {
  const styles = useStyles();
  const { message, details, errorCode } = entry.options;
  const code = typeof errorCode === 'number' && Number.isFinite(errorCode) ? `0x${(errorCode >>> 0).toString(16)}` : undefined;
  return (
    <Dialog open onOpenChange={(_, d) => !d.open && entry.done()}>
      <DialogSurface>
        <DialogBody>
          <DialogTitle>{t('xrm.dialog.errorTitle')}</DialogTitle>
          <DialogContent>
            {message && <Body1 block>{message}</Body1>}
            {code && <Body1 block>{t('xrm.dialog.errorCode', { code })}</Body1>}
            {details && (
              <Field label={t('xrm.dialog.details')}>
                <Text className={styles.details}>{details}</Text>
              </Field>
            )}
          </DialogContent>
          <DialogActions>
            <Button appearance="primary" onClick={() => entry.done()}>
              {t('xrm.dialog.ok')}
            </Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}

/**
 * Ruling C3: the page in a dialog closes it with `window.close()`. A browser ignores `close()` on a window a script did not
 * open, so the shell replaces `close` on the page's window. Each new document has a new window object, so this runs as soon as
 * the frame exists and again whenever the window's `close` is not ours (a poll for as long as the dialog is open), and after
 * every load. A script that calls `close()` in the first milliseconds of parsing can still run before the poll sees the document
 * (documented limit; the dialog's Close button and Esc always work). A page on another origin cannot be reached.
 */
function installClose(frame: HTMLIFrameElement, close: () => void): boolean {
  try {
    const win = frame.contentWindow;
    if (!win) return false;
    if (win.close !== close) Object.defineProperty(win, 'close', { value: close, configurable: true, writable: true });
    return win.close === close;
  } catch {
    // Another origin: the window is not ours to change.
    return false;
  }
}

/** Ticks of the poll (5 ms each) one document gets: about 2 s. */
const POLL_TICKS = 400;

/**
 * Installs the override while `active` (the frame is rendered). The poll stops once the override is on the current window or
 * after about 2 s, and starts again when that window goes away (`pagehide`: the frame navigates to a new document). The
 * returned function is the after-load install.
 */
function useCloseOverride(frame: RefObject<HTMLIFrameElement | null>, close: () => void, active: boolean): () => void {
  useEffect(() => {
    if (!active) return;
    let timer: ReturnType<typeof setInterval> | undefined;
    let left = 0;
    const stop = () => {
      if (timer !== undefined) clearInterval(timer);
      timer = undefined;
    };
    const start = () => {
      stop();
      left = POLL_TICKS;
      timer = setInterval(tick, 5);
    };
    const tick = () => {
      const el = frame.current;
      if (el && installClose(el, close)) {
        stop();
        el.contentWindow?.addEventListener('pagehide', start, { once: true });
      } else if (--left <= 0) stop();
    };
    start();
    return stop;
  }, [frame, close, active]);
  return () => {
    if (frame.current) installClose(frame.current, close);
  };
}

function WebResourceFrameDialog({ entry }: { entry: Extract<Entry, { kind: 'webResource' }> }) {
  const styles = useStyles();
  const { name, data, title } = entry.options;
  const src = webResourceSrc(name, data !== undefined && data !== '' ? { data } : {});
  const check = useWebResourceCheck(src);
  const side = entry.options.position === 2;
  const frame = useRef<HTMLIFrameElement>(null);
  const close = useCallback(() => entry.done(), [entry]);
  const onLoad = useCloseOverride(frame, close, check.status === 'ready');
  return (
    <Dialog open onOpenChange={(_, d) => !d.open && entry.done()}>
      <DialogSurface
        className={mergeClasses(styles.frameSurface, side && styles.side)}
        style={sizeStyle(entry.options)}
        aria-label={title ? undefined : t('xrm.webResource.frame', { name })}
      >
        <DialogBody className={styles.frameBody}>
          <DialogTitle
            action={
              <Button appearance="subtle" onClick={() => entry.done()}>
                {t('xrm.dialog.close')}
              </Button>
            }
          >
            {title ?? ''}
          </DialogTitle>
          <DialogContent className={styles.frameBody}>
            {check.status === 'checking' ? (
              <Spinner label={t('common.loading')} />
            ) : check.status === 'ready' ? (
              <iframe ref={frame} className={styles.frame} src={src} title={t('xrm.webResource.frame', { name })} onLoad={onLoad} />
            ) : (
              <MessageBar intent={check.status === 'missing' ? 'warning' : 'error'}>
                <MessageBarBody>{check.status === 'missing' ? t('webresource.notFound', { name }) : t('apps.error', { detail: check.detail })}</MessageBarBody>
              </MessageBar>
            )}
          </DialogContent>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}

interface TargetMeta {
  EntitySetName?: string;
  PrimaryIdAttribute?: string;
  PrimaryNameAttribute?: string;
}

/** The search of one table in the lookup dialog: UI-1b's lookup search, narrowed by the options' filters of that table. */
function LookupTarget({
  target,
  filters,
  searchText,
  selected,
  onPick,
}: {
  target: string;
  filters: string[];
  searchText?: string;
  selected?: PickedEntity;
  onPick: (p: PickedEntity) => void;
}) {
  const meta = useLoad(target, (api) => api.get<TargetMeta>(`${entityPath(target)}?$select=EntitySetName,PrimaryIdAttribute,PrimaryNameAttribute`));
  const fetchFilters = useCallback(() => filters, [filters]);
  if (meta.error) return <Text role="alert">{meta.error}</Text>;
  const m = meta.data;
  if (!m) return <Spinner size="tiny" label={t('common.loading')} />;
  if (!m.EntitySetName || !m.PrimaryNameAttribute) return <Text role="alert">{t('field.lookup.notSearchable', { table: target })}</Text>;
  return (
    <EntityPicker
      entitySet={m.EntitySetName}
      entityName={target}
      idAttribute={m.PrimaryIdAttribute ?? `${target}id`}
      nameAttribute={m.PrimaryNameAttribute}
      fetchFilters={filters.length ? fetchFilters : undefined}
      label={t('xrm.lookup.search', { table: target })}
      initialTerm={searchText}
      selected={selected}
      onSelect={onPick}
    />
  );
}

const NO_FILTERS: string[] = [];

function LookupDialog({ entry }: { entry: Extract<Entry, { kind: 'lookup' }> }) {
  const { options } = entry;
  const [target, setTarget] = useState(options.defaultEntityType && options.entityTypes.includes(options.defaultEntityType) ? options.defaultEntityType : options.entityTypes[0]!);
  const [picked, setPicked] = useState<LookupResult[]>([]);
  const [filters] = useState(() => {
    const byTable = new Map<string, string[]>();
    for (const f of options.filters ?? []) byTable.set(f.entityLogicalName, [...(byTable.get(f.entityLogicalName) ?? []), f.filterXml]);
    return byTable;
  });
  const viewsOf = (table: string) => (options.views ?? []).filter((v) => v.entity === table);
  const firstView = (table: string) => {
    const views = viewsOf(table);
    return (views.find((v) => v.id === options.defaultViewId) ?? views[0])?.id;
  };
  const [viewId, setViewId] = useState(() => firstView(target));
  const tableViews = viewsOf(target);
  const view = tableViews.find((v) => v.id === viewId);
  const key = `${target}|${view?.id ?? ''}`;
  // One array per table and view: the picker searches again only when they change.
  const searchFilters = useMemo(() => [...(view?.filters ?? []), ...(filters.get(target) ?? NO_FILTERS)], [view, filters, target]);
  const onPick = (p: PickedEntity) => {
    const result = { id: bracedUpper(p.id), name: p.name, entityType: target };
    if (!options.allowMultiSelect) return setPicked([result]);
    setPicked((all) => (all.some((x) => x.id === result.id) ? all.filter((x) => x.id !== result.id) : [...all, result]));
  };
  const current = picked.find((p) => p.entityType === target);
  return (
    <Dialog open onOpenChange={(_, d) => !d.open && entry.done([])}>
      <DialogSurface>
        <DialogBody>
          <DialogTitle>{t('xrm.lookup.title')}</DialogTitle>
          <DialogContent>
            {options.entityTypes.length > 1 && (
              <Field label={t('xrm.lookup.table')}>
                <Select
                  value={target}
                  onChange={(_, d) => {
                    setTarget(d.value);
                    setViewId(firstView(d.value));
                  }}
                >
                  {options.entityTypes.map((x) => (
                    <option key={x} value={x}>
                      {x}
                    </option>
                  ))}
                </Select>
              </Field>
            )}
            {tableViews.length > 0 && (
              <Field label={t('xrm.lookup.view')}>
                <Select value={viewId} onChange={(_, d) => setViewId(d.value)}>
                  {tableViews.map((v) => (
                    <option key={v.id} value={v.id}>
                      {v.name}
                    </option>
                  ))}
                </Select>
              </Field>
            )}
            <LookupTarget
              key={key}
              target={target}
              filters={searchFilters}
              searchText={options.searchText}
              selected={current && !options.allowMultiSelect ? { id: current.id.replace(/[{}]/g, '').toLowerCase(), name: current.name } : undefined}
              onPick={onPick}
            />
            {options.allowMultiSelect && picked.length > 0 && <Body1 block>{t('xrm.lookup.selected', { names: picked.map((p) => p.name).join(', ') })}</Body1>}
          </DialogContent>
          <DialogActions>
            <Button appearance="primary" disabled={picked.length === 0} onClick={() => entry.done(picked)}>
              {t('xrm.lookup.add')}
            </Button>
            <Button appearance="secondary" onClick={() => entry.done([])}>
              {t('xrm.dialog.cancel')}
            </Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}
