import { Badge, Button, makeStyles, MessageBar, MessageBarBody, mergeClasses, Spinner, Text, tokens, Tooltip } from '@fluentui/react-components';
import { ChevronLeftRegular, PanelRightRegular, ChevronRightRegular, DismissRegular } from '@fluentui/react-icons';
import { useEffect, useLayoutEffect, useState, useSyncExternalStore } from 'react';
import { isWebResourceName, webResourceSrc, webResourceUrl } from '../client-api/scriptHost';
import { useWebResourceCheck } from '../client-api/useWebResourceCheck';
import type { PaneSnapshotItem, SidePaneStore } from '../client-api/xrmverse/sidePanes';
import { t } from '../shell/i18n/t';
import { NARROW, useMediaQuery } from '../shell/layout/useMediaQuery';

const STRIP_WIDTH = 40;
const MIN_WIDTH = 300;
/** Space that stays for the main area when a pane is drawn beside it. */
const MAIN_RESERVE = 320;

/** The drawn width of a pane: `max(300, min(width, viewport - 320))`. */
export const drawnWidth = (width: number, viewport: number): number => Math.max(MIN_WIDTH, Math.min(width, viewport - MAIN_RESERVE));

const useStyles = makeStyles({
  // Sticky at the top of the viewport while the page scrolls; its height is set by `useFitViewport`. Also the containing block
  // of the narrow overlay.
  root: {
    display: 'flex',
    flexShrink: 0,
    position: 'sticky',
    top: 0,
    alignSelf: 'flex-start',
    backgroundColor: tokens.colorNeutralBackground1,
  },
  strip: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
    rowGap: tokens.spacingVerticalXS,
    width: `${STRIP_WIDTH}px`,
    flexShrink: 0,
    paddingTop: tokens.spacingVerticalS,
    borderLeft: `${tokens.strokeWidthThin} solid ${tokens.colorNeutralStroke2}`,
    backgroundColor: tokens.colorNeutralBackground2,
  },
  tab: { position: 'relative', minWidth: 0 },
  tabSelected: { backgroundColor: tokens.colorNeutralBackground1Selected },
  tabImage: { width: '20px', height: '20px' },
  badge: { position: 'absolute', top: '2px', right: '2px' },
  content: {
    display: 'flex',
    flexDirection: 'column',
    minWidth: 0,
    minHeight: 0,
    overflow: 'hidden',
    borderLeft: `${tokens.strokeWidthThin} solid ${tokens.colorNeutralStroke2}`,
    backgroundColor: tokens.colorNeutralBackground1,
    color: tokens.colorNeutralForeground1,
  },
  overlay: { position: 'absolute', top: 0, bottom: 0, zIndex: 1, boxShadow: tokens.shadow16 },
  gone: { display: 'none' },
  header: {
    display: 'flex',
    alignItems: 'center',
    columnGap: tokens.spacingHorizontalS,
    padding: `${tokens.spacingVerticalXS} ${tokens.spacingHorizontalS}`,
    borderBottom: `${tokens.strokeWidthThin} solid ${tokens.colorNeutralStroke2}`,
  },
  title: { flexGrow: 1, fontWeight: tokens.fontWeightSemibold },
  // Fills the pane's height (not the page's): it grows from a zero basis inside the pane's column.
  frame: { display: 'block', flexGrow: 1, flexBasis: 0, minHeight: 0, border: 'none', width: '100%' },
});

/**
 * The web resource check of a pane's content, which settles the pending `navigate`: it resolves when the check is ready and
 * rejects with the failure, which the pane also shows. The current `contentKey` guards against a superseded navigate.
 */
function usePaneCheck(store: SidePaneStore, pane: PaneSnapshotItem) {
  const content = pane.content;
  const name = content?.webresourceName;
  const valid = name !== undefined && isWebResourceName(name);
  const src = valid ? webResourceSrc(name, content?.data !== undefined ? { data: content.data } : {}) : undefined;
  const check = useWebResourceCheck(src);
  const { paneId, contentKey } = pane;
  const failure =
    name === undefined
      ? undefined
      : !valid
        ? t('webresource.invalidName', { name })
        : check.status === 'missing'
          ? t('webresource.notFound', { name })
          : check.status === 'error'
            ? t('apps.error', { detail: check.detail })
            : undefined;
  const ready = valid && check.status === 'ready';
  useEffect(() => {
    if (failure !== undefined) store.setPaneError(paneId, contentKey, failure);
    else if (ready) store.markPaneLoaded(paneId, contentKey);
  }, [store, paneId, contentKey, failure, ready]);
  return { name, src, failure, ready, contentKey };
}

/** A pane whose content is not mounted (not shown, or hidden): only the check runs, so its `navigate` still settles. */
function PaneCheck({ store, pane }: { store: SidePaneStore; pane: PaneSnapshotItem }) {
  usePaneCheck(store, pane);
  return null;
}

function PaneFrame({ store, pane }: { store: SidePaneStore; pane: PaneSnapshotItem }) {
  const styles = useStyles();
  const { name, src, failure, ready, contentKey } = usePaneCheck(store, pane);
  if (name === undefined) return null;
  if (failure !== undefined) {
    return (
      <MessageBar intent="warning">
        <MessageBarBody>{failure}</MessageBarBody>
      </MessageBar>
    );
  }
  if (!ready) return <Spinner label={t('common.loading')} />;
  // Same origin, like a web resource page: `parent.Xrm` and `parent.XrmVerse` are reachable.
  return <iframe key={contentKey} className={styles.frame} src={src} title={t('xrm.webResource.frame', { name })} />;
}

function PaneBadge({ badge, className }: { badge: PaneSnapshotItem['badge']; className: string }) {
  if (badge === false) return null;
  if (badge === true) return <Badge className={className} size="extra-small" color="danger" aria-label={t('apps.sidePanes.badgeDot')} />;
  return (
    <Badge className={className} size="small" color="danger" aria-label={t('apps.sidePanes.badge', { count: badge })}>
      {badge}
    </Badge>
  );
}

/**
 * Sets the sticky area's height to the viewport below its top: below the header while the page is at the top, the whole
 * viewport once it sticks. Set on the element directly, so scrolling does not re-render the panes.
 */
function useFitViewport(el: HTMLElement | null) {
  useLayoutEffect(() => {
    if (!el) return;
    const fit = () => {
      const top = Math.max(0, el.getBoundingClientRect().top);
      el.style.height = `${Math.max(0, window.innerHeight - top)}px`;
    };
    fit();
    window.addEventListener('scroll', fit, { passive: true });
    window.addEventListener('resize', fit);
    // Content above the area (a notification bar) moves it without a scroll.
    const observer = typeof ResizeObserver === 'undefined' ? undefined : new ResizeObserver(fit);
    observer?.observe(document.body);
    return () => {
      window.removeEventListener('scroll', fit);
      window.removeEventListener('resize', fit);
      observer?.disconnect();
    };
  }, [el]);
}

/**
 * The side pane area on the right of the main area: a tab strip (one icon per visible pane) and the selected pane. Without
 * panes nothing is drawn. Under the narrow breakpoint an expanded pane overlays the main area instead of shrinking it.
 */
export function SidePaneArea({ store }: { store: SidePaneStore }) {
  const styles = useStyles();
  const snap = useSyncExternalStore(store.subscribe, store.snapshot);
  const narrow = useMediaQuery(NARROW);
  const [viewport, setViewport] = useState(() => window.innerWidth);
  useEffect(() => {
    const onResize = () => setViewport(window.innerWidth);
    window.addEventListener('resize', onResize);
    onResize();
    return () => window.removeEventListener('resize', onResize);
  }, []);
  const [root, setRoot] = useState<HTMLDivElement | null>(null);
  useFitViewport(root);
  const visible = snap.panes.filter((p) => !p.hidden);
  const expanded = snap.state === 1;
  const selected = visible.find((p) => p.paneId === snap.selectedId);
  const shownId = expanded ? selected?.paneId : undefined;
  // Content mounts only for the shown pane, or with `alwaysRender` (which survives another selection and a collapse); a
  // hidden pane mounts nothing.
  const mounted = (p: PaneSnapshotItem) => !p.hidden && (p.paneId === shownId || p.alwaysRender);
  // The panes with content that is not mounted still run the web resource check, so their `navigate` settles.
  const checks = snap.panes
    .filter((p) => p.content !== undefined && !mounted(p))
    .map((p) => <PaneCheck key={`${p.paneId}|${p.contentKey}`} store={store} pane={p} />);
  if (visible.length === 0) return <>{checks}</>;
  return (
    <div ref={setRoot} className={styles.root} role="region" aria-label={t('apps.sidePanes.label')}>
      {checks}
      {snap.panes.map((p) => {
        const shown = p.paneId === shownId;
        if (!mounted(p)) return null;
        const width = narrow ? viewport - STRIP_WIDTH : drawnWidth(p.width, viewport);
        return (
          <div
            key={p.paneId}
            data-pane-id={p.paneId}
            className={mergeClasses(styles.content, narrow && styles.overlay, !shown && styles.gone)}
            style={narrow ? { width: `${width}px`, right: `${STRIP_WIDTH}px` } : { width: `${width}px` }}
          >
            {!p.hideHeader && (
              <div className={styles.header}>
                <Text className={styles.title}>{p.title}</Text>
                {p.canClose && (
                  <Button
                    appearance="subtle"
                    size="small"
                    icon={<DismissRegular />}
                    aria-label={t('apps.sidePanes.close')}
                    onClick={() => store.api.getPane(p.paneId)?.close()}
                  />
                )}
              </div>
            )}
            <PaneFrame store={store} pane={p} />
          </div>
        );
      })}
      <div className={styles.strip}>
        <Button
          appearance="subtle"
          size="small"
          icon={expanded ? <ChevronRightRegular /> : <ChevronLeftRegular />}
          aria-label={expanded ? t('apps.sidePanes.collapse') : t('apps.sidePanes.expand')}
          aria-expanded={expanded}
          onClick={() => {
            store.api.state = expanded ? 0 : 1;
          }}
        />
        {visible.map((p) => (
          <Tooltip key={p.paneId} content={p.title} relationship="label" positioning="before">
            <Button
              appearance="subtle"
              size="small"
              className={mergeClasses(styles.tab, expanded && p.paneId === selected?.paneId && styles.tabSelected)}
              aria-pressed={expanded && p.paneId === selected?.paneId}
              icon={
                p.imageSrc ? <img className={styles.tabImage} src={webResourceUrl(p.imageSrc.slice('WebResources/'.length))} alt="" /> : <PanelRightRegular />
              }
              onClick={() => store.api.getPane(p.paneId)?.select()}
            >
              <PaneBadge badge={p.badge} className={styles.badge} />
            </Button>
          </Tooltip>
        ))}
      </div>
    </div>
  );
}
