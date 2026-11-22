import { Button, makeStyles, MessageBar, MessageBarActions, MessageBarBody, SearchBox, Spinner, Text, tokens } from '@fluentui/react-components';
import { useCallback, useContext, useEffect, useMemo, useRef, useState, type DragEvent, type KeyboardEvent } from 'react';
import { cachedRead } from '../controlEnv';
import { useLocation, useNavigate } from 'react-router-dom';
import { saveFile } from '../../../../admin/tables/fileApi';
import { ConfirmDialog } from '../../../../admin/common/ConfirmDialog';
import { DisplayZoneContext } from '../../../../apps/displayZone';
import { mainAspxUrl } from '../../../../apps/mainAspx';
import { activityActionsAllowed, assignActivity, creatableActivityTypes, newActivityUrl, setActivityState } from '../../../../apps/record/timeline/activityActions';
import { createNote, deleteNote, downloadNote, maxUploadBytes, tooLargeMessage, updateNote } from '../../../../apps/record/timeline/noteActions';
import { createPost, deletePost } from '../../../../apps/record/timeline/postActions';
import { holds } from '../../../../apps/record/privileges';
import { activityOnlyFilterSet, postOnlyFilterSet, type TimelineFilters } from '../../../../apps/record/timeline/filters';
import type { TimelineItem } from '../../../../apps/record/timeline/timelineQuery';
import type { TimelineControl as TimelineModel } from '../../../../model/form';
import { createFormatting } from '../../../../pcf/formatting';
import { t, uiLocale } from '../../../../shell/i18n/t';
import { useAuth } from '../../../../shell/auth/AuthProvider';
import { DismissIcon } from '../../../../shell/icons';
import { AssignDialog } from '../AssignDialog';
import type { ControlEnv } from '../controlEnv';
import { NoteEditDialog, NoteEntry } from './NoteEntry';
import { MentionText, mentionPlainText, PostEntry, PostThread } from './PostEntry';
import { TimelineChips } from './TimelineChips';
import { TimelineCreateMenu } from './TimelineCreateMenu';
import { TimelineFilterPane } from './TimelineFilterPane';
import { TimelineHeader } from './TimelineHeader';
import { TimelineItemRow, type TimelineItemActions } from './TimelineItemRow';
import { activityStatusOf, dateGroupOf, isExpanded, sameDay, useCompleteStates, useTimeline, type DateGroup } from './useTimeline';

const SEARCH_DELAY_MS = 300;
const ANNOUNCE_DELAY_MS = 100;

const useStyles = makeStyles({
  timeline: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalS, minWidth: 0 },
  list: { margin: 0, padding: 0 },
  groupTitle: {
    display: 'block',
    marginBlockStart: tokens.spacingVerticalS,
    paddingBlockEnd: tokens.spacingVerticalXXS,
    color: tokens.colorNeutralForeground3,
    textTransform: 'uppercase',
    borderBottom: `${tokens.strokeWidthThin} solid ${tokens.colorNeutralStroke2}`,
  },
  more: { alignSelf: 'center' },
  // Read by screen readers only.
  announcer: { position: 'absolute', width: '1px', height: '1px', overflow: 'hidden', clipPath: 'inset(50%)', whiteSpace: 'nowrap' },
});

const GROUP_LABELS: Record<DateGroup, Parameters<typeof t>[0]> = {
  due: 'record.timeline.group.due',
  today: 'record.timeline.group.today',
  yesterday: 'record.timeline.group.yesterday',
  thisWeek: 'record.timeline.group.thisWeek',
  thisMonth: 'record.timeline.group.thisMonth',
  older: 'record.timeline.group.older',
};

const errorText = (e: unknown) => (e instanceof Error ? e.message : String(e));

/** Any filter of the pane is set (search and sort are not pane filters). */
const filtersApplied = (f: TimelineFilters) => activityOnlyFilterSet(f) || postOnlyFilterSet(f) || f.recordTypes.length > 0 || f.modified.length > 0 || f.mine;

export interface TimelineControlProps {
  control: TimelineModel;
  env: ControlEnv;
  /** The cell's label, shown in the header; absent when the cell hides it. */
  label?: string;
  /** Bumped to read page 1 again (the Client API's `refresh()`). */
  reload?: number;
  /** Bumped by a script's `setFocus()`: the focus moves to the first item, else to the search box. */
  focus?: number;
  /** A script's `setDisabled(true)`: no create menu, note entry, file drop or quick actions; the list can still be read and opened. */
  disabled?: boolean;
}

/**
 * The timeline of a form (UI-1g, spec §5): the activities regarding the record and its notes, newest first, with search,
 * chips, a filter pane, "Load more" and, when the app turns it on, date groups with the overdue activities first.
 */
export function TimelineControl({ control, env, label, reload = 0, focus = 0, disabled = false }: TimelineControlProps) {
  const styles = useStyles();
  const timeline = useTimeline(env, control, reload);
  const { availability, scope, filters, setFilters, counts, items, overdue, now, expanded, setExpanded } = timeline;
  const zone = useContext(DisplayZoneContext) ?? 'Etc/UTC';
  const formatting = useMemo(() => createFormatting(uiLocale(), { timeZone: zone }), [zone]);
  const [paneOpen, setPaneOpen] = useState(false);
  const [noticeShown, setNoticeShown] = useState(true);
  const [searchText, setSearchText] = useState('');
  const { api } = useAuth();
  const navigate = useNavigate();
  const location = useLocation();
  const here = location.pathname + location.search;
  const [pending, setPending] = useState<{ action: 'assign' | 'delete'; set: string; id: string; note?: true; post?: true }>();
  const [actionError, setActionError] = useState<string>();
  // The quick note entry (spec §5.6): open or not, the file attached to it, a refused file's message, the note being edited.
  const [noteOpen, setNoteOpen] = useState(false);
  const [noteFocus, setNoteFocus] = useState(0);
  const [noteFile, setNoteFile] = useState<File>();
  const [fileError, setFileError] = useState<string>();
  const [editing, setEditing] = useState<{ id: string; subject: string; text: string }>();
  // The post entry (spec §4.3): open or not, and a focus request from the + menu.
  const [postOpen, setPostOpen] = useState(false);
  const [postFocus, setPostFocus] = useState(0);
  // The keyboard (spec §6.8): the item the list's tab stop is on, and what the live region last said.
  const [activeKey, setActiveKey] = useState<string>();
  const [announcement, setAnnouncement] = useState('');
  // The region is emptied, then given the text a moment later: the same words said twice are then heard twice.
  const announceTimer = useRef<ReturnType<typeof setTimeout>>();
  const announce = useCallback((text: string) => {
    setAnnouncement('');
    clearTimeout(announceTimer.current);
    announceTimer.current = setTimeout(() => setAnnouncement(text), ANNOUNCE_DELAY_MS);
  }, []);
  useEffect(() => () => clearTimeout(announceTimer.current), []);
  const filtersChanged = t('record.timeline.announce.filtersChanged');
  const changeFilters: typeof setFilters = (next) => {
    setFilters(next);
    announce(filtersChanged);
  };

  // A script's setFocus(): the list's tab stop, else the search box.
  const sectionRef = useRef<HTMLElement>(null);
  useEffect(() => {
    if (!focus) return;
    const root = sectionRef.current;
    (root?.querySelector<HTMLElement>('[data-timeline-item][tabindex="0"]') ?? root?.querySelector<HTMLElement>('input[type="search"]'))?.focus();
  }, [focus]);

  // The quick actions (spec §5.4) by activity table, each shown only with its privilege.
  const { held, activityTables } = timeline;
  const tables = useMemo(() => new Map(activityTables.map((e) => [e.logicalName, e])), [activityTables]);
  const allowed = (table: string) => {
    const meta = tables.get(table);
    return held && meta?.entitySetName ? { set: meta.entitySetName, ...activityActionsAllowed(held, meta) } : undefined;
  };
  const completable = [...overdue, ...items].filter((i) => i.kind === 'activity' && activityStatusOf(i, now) !== 'closed' && allowed(i.table)?.complete);
  const completeStates = useCompleteStates(env, completable.map((i) => i.table));

  // What the form asks that xrmverse ignores is said, never silent (spec §6.6): a notice, and once the element names.
  const parameters = control.parameterNames.join(', ');
  const warned = useRef('');
  useEffect(() => {
    if (!parameters || warned.current === parameters) return;
    warned.current = parameters;
    console.warn(`Timeline '${control.id}': xrmverse does not read its parameters yet and uses the default settings: ${parameters}.`);
  }, [control.id, parameters]);

  // The search runs on the server once typing pauses.
  useEffect(() => {
    if (searchText === filters.search) return;
    const timer = setTimeout(() => {
      setFilters((f) => ({ ...f, search: searchText }));
      announce(filtersChanged);
    }, SEARCH_DELAY_MS);
    return () => clearTimeout(timer);
  }, [searchText, filters.search, setFilters, announce, filtersChanged]);

  const notice = control.parameterNames.length > 0 && noticeShown && (
    <MessageBar intent="info">
      <MessageBarBody>{t('record.timeline.parametersIgnored')}</MessageBarBody>
      <MessageBarActions containerAction={<Button appearance="transparent" size="small" icon={<DismissIcon />} aria-label={t('record.timeline.dismiss')} onClick={() => setNoticeShown(false)} />} />
    </MessageBar>
  );

  if (availability !== 'ready') {
    return (
      <section ref={sectionRef} className={styles.timeline} aria-label={label ?? t('record.timeline.title')}>
        {notice}
        <MessageBar intent="info">
          <MessageBarBody>{availability === 'newRecord' ? t('record.timeline.saveFirst') : t('record.timeline.noActivitiesOrNotes')}</MessageBarBody>
        </MessageBar>
      </section>
    );
  }

  // An action that changed a record reads the timeline again; a failure is said above the list.
  const run = async (action: () => Promise<void>, changed = true) => {
    setActionError(undefined);
    try {
      await action();
      if (changed) timeline.refresh();
    } catch (e) {
      setActionError(errorText(e));
    }
  };
  const openOf = (item: TimelineItem) =>
    item.kind === 'activity' ? () => navigate(mainAspxUrl({ appId: env.appId, pageType: 'entityrecord', etn: item.table, id: item.id }), { state: { returnTo: here } }) : undefined;
  const actionsOf = (item: TimelineItem): TimelineItemActions | undefined => {
    // A disabled timeline can still be read and opened; every other action is hidden.
    if (disabled) return item.kind === 'activity' ? { onOpen: openOf(item)! } : undefined;
    // A post: Delete on the user's own post only (Reply is under the expanded post).
    if (item.kind === 'post') return canDeletePost && isMine(item.authorId) ? { onDelete: () => setPending({ action: 'delete', set: 'posts', id: item.id, post: true }) } : undefined;
    if (item.kind === 'note') {
      if (!api || !held) return undefined;
      return {
        ...(held.has('prvWriteNote') ? { onEdit: () => setEditing({ id: item.id, subject: item.title, text: item.body ?? '' }) } : {}),
        ...(item.fileName && item.fileSize ? { onDownload: () => void run(async () => saveFile(await downloadNote(api, item.id)), false) } : {}),
        ...(held.has('prvDeleteNote') ? { onDelete: () => setPending({ action: 'delete', set: 'annotations', id: item.id, note: true }) } : {}),
      };
    }
    const open = openOf(item)!;
    const can = allowed(item.table);
    if (!can || !api) return { onOpen: open };
    const complete = completeStates[item.table];
    return {
      onOpen: open,
      ...(can.complete && complete && activityStatusOf(item, now) !== 'closed' ? { onComplete: () => void run(() => setActivityState(api, can.set, item.id, complete)) } : {}),
      ...(can.assign ? { onAssign: () => setPending({ action: 'assign', set: can.set, id: item.id }) } : {}),
      ...(can.delete ? { onDelete: () => setPending({ action: 'delete', set: can.set, id: item.id }) } : {}),
    };
  };

  const hasActivities = scope?.hasActivities ?? env.ctx.entity.hasActivities === true;
  const hasNotes = scope?.hasNotes ?? env.ctx.entity.hasNotes === true;
  const hasPosts = scope?.hasPosts ?? false;
  // Posts are written with prvCreatePost and deleted with prvDeletePost (comments use the same Post privileges); a disabled
  // timeline offers neither (spec §4.3). Delete is offered on the user's own posts and comments only.
  const userId = scope?.userId ?? '';
  const isMine = (authorId?: string) => !!userId && !!authorId && authorId.toLowerCase() === userId.toLowerCase();
  const canPost = !disabled && hasPosts && !!held && holds(held, 'Create', { schemaName: 'Post' }) && !!api && !!env.ctx.fieldContext.record;
  const canDeletePost = !disabled && hasPosts && !!held && holds(held, 'Delete', { schemaName: 'Post' }) && !!api;
  // A user mention opens the user's record, as an activity opens its form.
  const userUrl = (id: string) => mainAspxUrl({ appId: env.appId, pageType: 'entityrecord', etn: 'systemuser', id });
  const openUser = (id: string) => navigate(userUrl(id), { state: { returnTo: here } });
  const mentionText = (text: string) => <MentionText text={text} userUrl={userUrl} onOpenUser={openUser} />;
  const shortDate = (iso: string) => (sameDay(iso, now, zone) ? formatting.formatTime(new Date(iso)) : formatting.formatDateShort(new Date(iso)));
  const typeName = (item: TimelineItem) =>
    item.kind === 'note' ? t('record.timeline.note') : item.kind === 'post' ? t('record.timeline.post') : (timeline.typeNames[item.table] ?? item.table);
  const keyOf = (item: TimelineItem) => `${item.kind}:${item.id}`;
  const row = (item: TimelineItem) => (
    <TimelineItemRow
      key={keyOf(item)}
      item={item}
      tabIndex={keyOf(item) === tabStop ? 0 : -1}
      onFocus={() => setActiveKey(keyOf(item))}
      onOpen={openOf(item)}
      typeName={typeName(item)}
      expanded={isExpanded(expanded, item.id)}
      look={env.timelineAppearance}
      onToggle={() => {
        const toggled = new Set(expanded.toggled);
        if (!toggled.delete(item.id)) toggled.add(item.id);
        setExpanded({ ...expanded, toggled });
      }}
      now={now}
      zone={zone}
      formatting={formatting}
      actions={actionsOf(item)}
      {...(item.kind === 'post' && api
        ? {
            titleContent: mentionText(item.title),
            titleLabel: mentionPlainText(item.title),
            footer: (
              <PostThread
                api={api}
                post={item}
                expanded={isExpanded(expanded, item.id)}
                userId={userId}
                canReply={canPost}
                canDelete={canDeletePost}
                renderText={mentionText}
                formatDate={shortDate}
                onError={setActionError}
              />
            ),
          }
        : {})}
    />
  );

  // The + menu: a new activity opens its main form regarding this record (spec §6.4). After its first save the form comes
  // back here, as one opened by a subgrid's New does (RecordPage reads `returnTo` and `fromSubgrid`).
  const primaryName = env.ctx.entity.primaryNameAttribute;
  const named = primaryName ? (env.state.record ?? env.ctx.record)?.[primaryName] : undefined;
  const parent = { table: env.ctx.entity.logicalName, id: env.ctx.fieldContext.record?.id ?? '', name: typeof named === 'string' ? named : '' };
  const creatable = hasActivities && held && !disabled ? creatableActivityTypes(activityTables, held) : [];
  // A note needs notes on the table and `prvCreateNote`; without them there is no entry, no menu item and no drop.
  const noteRecord = env.ctx.fieldContext.record;
  const canNote = !disabled && hasNotes && !!held?.has('prvCreateNote') && !!api && !!noteRecord;
  const openNote = () => {
    setNoteOpen(true);
    setNoteFocus((n) => n + 1);
  };
  // A file is checked against `organization.maxuploadfilesize` before anything is sent (spec §5.6).
  const acceptFile = async (file: File) => {
    setFileError(undefined);
    // A limit that cannot be read leaves the check to the server, which refuses a larger body itself.
    const limit = api ? await cachedRead(env, 'maxuploadfilesize', () => maxUploadBytes(api)).catch(() => undefined) : undefined;
    const refused = limit === undefined ? undefined : tooLargeMessage(file, limit);
    if (refused) {
      setFileError(refused);
      return;
    }
    setNoteFile(file);
    setNoteOpen(true);
  };
  const dropHandlers = canNote
    ? {
        onDragOver: (e: DragEvent) => {
          if (e.dataTransfer.types.includes('Files')) e.preventDefault();
        },
        onDrop: (e: DragEvent) => {
          const file = e.dataTransfer.files[0];
          if (!file) return;
          e.preventDefault();
          void acceptFile(file);
        },
      }
    : {};
  const createMenu = (
    <TimelineCreateMenu
      onNote={canNote ? openNote : undefined}
      onPost={
        canPost
          ? () => {
              setPostOpen(true);
              setPostFocus((n) => n + 1);
            }
          : undefined
      }
      types={creatable}
      onCreate={(table) => navigate(newActivityUrl(env.appId, table, parent), { state: { returnTo: here, fromSubgrid: {} } })}
    />
  );

  // With groups on: Due first (its own query), then the dated groups in the order the items come.
  const groups: { id: DateGroup; items: TimelineItem[] }[] = [];
  if (timeline.groupByDate) {
    if (timeline.showDue && overdue.length) groups.push({ id: 'due', items: overdue });
    for (const item of items) {
      const id = dateGroupOf(item.modifiedOn, now, zone);
      const last = groups.at(-1);
      if (last && last.id === id) last.items.push(item);
      else groups.push({ id, items: [item] });
    }
  }

  // One tab stop for the items: the one last focused while it is shown, else the first.
  const shown = timeline.groupByDate ? groups.flatMap((g) => g.items) : items;
  const tabStop = shown.some((i) => keyOf(i) === activeKey) ? activeKey : shown[0] && keyOf(shown[0]);
  // Up and Down move between the items, N starts a note (Enter and Space are the item's own keys).
  const onListKeyDown = (e: KeyboardEvent<HTMLDivElement>) => {
    const target = e.target as HTMLElement;
    if (!target.hasAttribute('data-timeline-item') || e.altKey || e.ctrlKey || e.metaKey) return;
    if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
      e.preventDefault();
      const all = [...e.currentTarget.querySelectorAll<HTMLElement>('[data-timeline-item]')];
      all[all.indexOf(target) + (e.key === 'ArrowDown' ? 1 : -1)]?.focus();
    } else if ((e.key === 'n' || e.key === 'N') && canNote) {
      e.preventDefault();
      openNote();
    }
  };

  const list = (rows: TimelineItem[]) => (
    <ul
      className={styles.list}
      aria-label={t('record.timeline.items')}
      data-appearance={env.timelineAppearance?.appearance === 'cards' ? 'cards' : undefined}
      data-density={env.timelineAppearance?.density === 'compact' ? 'compact' : undefined}
    >
      {rows.map(row)}
    </ul>
  );

  return (
    <section ref={sectionRef} className={styles.timeline} aria-label={label ?? t('record.timeline.title')} data-testid="timeline" {...dropHandlers}>
      {notice}
      <TimelineHeader
        label={label}
        createMenu={createMenu}
        paneOpen={paneOpen}
        filtersApplied={filtersApplied(filters)}
        onTogglePane={() => setPaneOpen((o) => !o)}
        allExpanded={expanded.all}
        onToggleExpandAll={() => setExpanded({ all: !expanded.all, toggled: new Set() })}
        sort={filters.sort}
        onSort={(sort) => changeFilters((f) => ({ ...f, sort }))}
        onRefresh={timeline.refresh}
      />
      <SearchBox
        size="small"
        placeholder={t('record.timeline.search')}
        aria-label={t('record.timeline.search')}
        value={searchText}
        onChange={(_, d) => setSearchText(d.value)}
      />
      {canNote && noteRecord && (
        <NoteEntry
          open={noteOpen}
          focusRequest={noteFocus}
          onOpen={() => setNoteOpen(true)}
          onClose={() => {
            setNoteOpen(false);
            setNoteFile(undefined);
            setFileError(undefined);
          }}
          file={noteFile}
          onPickFile={(f) => void acceptFile(f)}
          onRemoveFile={() => setNoteFile(undefined)}
          onSave={async (note) => {
            setActionError(undefined);
            try {
              await createNote(api!, { entitySetName: noteRecord.set, id: noteRecord.id, table: env.ctx.entity.logicalName }, { ...note, ...(noteFile ? { file: noteFile } : {}) });
              setFileError(undefined);
              timeline.refresh();
              return true;
            } catch (e) {
              setActionError(errorText(e));
              return false;
            }
          }}
        />
      )}
      {canPost && postOpen && (
        <PostEntry
          api={api!}
          userId={userId}
          focusRequest={postFocus}
          onClose={() => setPostOpen(false)}
          onSave={async (text) => {
            setActionError(undefined);
            try {
              const record = env.ctx.fieldContext.record!;
              await createPost(api!, { table: env.ctx.entity.logicalName, id: record.id, entitySetName: record.set }, text);
              timeline.refresh();
              return true;
            } catch (e) {
              setActionError(errorText(e));
              return false;
            }
          }}
        />
      )}
      {fileError !== undefined && (
        <MessageBar intent="error">
          <MessageBarBody>{fileError}</MessageBarBody>
          <MessageBarActions containerAction={<Button appearance="transparent" size="small" icon={<DismissIcon />} aria-label={t('record.timeline.dismiss')} onClick={() => setFileError(undefined)} />} />
        </MessageBar>
      )}
      <TimelineChips filters={filters} counts={counts} hasActivities={hasActivities} hasNotes={hasNotes} hasPosts={hasPosts} onChange={changeFilters} />
      {paneOpen && (
        <TimelineFilterPane
          env={env}
          filters={filters}
          counts={counts}
          hasActivities={hasActivities}
          hasNotes={hasNotes}
          hasPosts={hasPosts}
          shownTypes={scope?.shownTypes ?? []}
          typeNames={timeline.typeNames}
          onChange={changeFilters}
        />
      )}
      {actionError !== undefined && (
        <MessageBar intent="error">
          <MessageBarBody>{t('record.timeline.actionFailed', { reason: actionError })}</MessageBarBody>
          <MessageBarActions
            containerAction={<Button appearance="transparent" size="small" icon={<DismissIcon />} aria-label={t('record.timeline.dismiss')} onClick={() => setActionError(undefined)} />}
          />
        </MessageBar>
      )}
      <div onKeyDown={onListKeyDown}>
        {timeline.error !== undefined ? (
          <MessageBar intent="warning">
            <MessageBarBody>{t('record.timeline.cannotShow', { reason: timeline.error })}</MessageBarBody>
          </MessageBar>
        ) : timeline.loading ? (
          <Spinner size="tiny" label={t('record.timeline.loading')} />
        ) : items.length === 0 && (!timeline.showDue || overdue.length === 0) ? (
          <Text>{t('record.timeline.empty')}</Text>
        ) : timeline.groupByDate ? (
          groups.map((g) => (
            <div key={g.id} data-testid="timeline-group" data-group={g.id}>
              <Text size={200} weight="semibold" className={styles.groupTitle}>
                {t(GROUP_LABELS[g.id])}
              </Text>
              {list(g.items)}
              {g.id === 'due' && timeline.overdueMore && (
                <Button appearance="transparent" size="small" onClick={() => changeFilters((f) => ({ ...f, statuses: ['overdue'] }))}>
                  {t('record.timeline.showAllOverdue')}
                </Button>
              )}
            </div>
          ))
        ) : (
          list(items)
        )}
      </div>
      {timeline.hasMore && !timeline.loading && timeline.error === undefined && (
        <Button
          className={styles.more}
          appearance="subtle"
          disabled={timeline.moreLoading}
          onClick={() =>
            void timeline.loadMore().then((n) => n > 0 && announce(n === 1 ? t('record.timeline.announce.oneMoreLoaded') : t('record.timeline.announce.moreLoaded', { count: n })))
          }
        >
          {t('record.timeline.loadMore')}
        </Button>
      )}
      <div role="status" aria-live="polite" className={styles.announcer}>
        {announcement}
      </div>
      <ConfirmDialog
        open={pending?.action === 'delete'}
        title={pending?.post ? t('record.timeline.post.deleteTitle') : t('record.delete.title')}
        body={pending?.post ? t('record.timeline.post.deleteBody') : t('record.delete.body')}
        confirmLabel={t('record.cmd.delete')}
        cancelLabel={t('common.cancel')}
        onCancel={() => setPending(undefined)}
        onConfirm={() => {
          const target = pending;
          setPending(undefined);
          if (target && api) void run(() => (target.note ? deleteNote(api, target.id) : target.post ? deletePost(api, target.id) : api.delete(`${target.set}(${target.id})`)));
        }}
      />
      {editing && api && (
        <NoteEditDialog
          note={editing}
          onClose={() => setEditing(undefined)}
          onSave={(note) => {
            const id = editing.id;
            setEditing(undefined);
            void run(() => updateNote(api, id, note));
          }}
        />
      )}
      {pending?.action === 'assign' && api && (
        <AssignDialog
          onAssign={async (owner) => {
            setActionError(undefined);
            await assignActivity(api, pending.set, pending.id, owner);
            timeline.refresh();
            return true;
          }}
          onClose={() => setPending(undefined)}
        />
      )}
    </section>
  );
}
