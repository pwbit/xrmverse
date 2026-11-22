import { Badge, Button, Card, makeStyles, mergeClasses, Text, tokens, Tooltip } from '@fluentui/react-components';
import type { ReactNode } from 'react';
import type { TimelineItem } from '../../../../apps/record/timeline/timelineQuery';
import type { PcfFormatting } from '../../../../pcf/formatting';
import { t } from '../../../../shell/i18n/t';
import { ActivityIcon, AssignIcon, AttachmentIcon, CollapseItemIcon, CompleteIcon, DeleteIcon, DownloadIcon, EditIcon, ExpandItemIcon, OpenIcon, RECORD_TYPE_ICONS } from '../../../../shell/icons';
import type { TimelineAppearance } from '../controlEnv';
import { activityStatusOf, overdueDays, sameDay } from './useTimeline';

const useStyles = makeStyles({
  row: {
    display: 'grid',
    gridTemplateColumns: 'auto auto minmax(0, 1fr) auto',
    columnGap: tokens.spacingHorizontalS,
    alignItems: 'start',
    paddingBlock: tokens.spacingVerticalS,
    borderBottom: `${tokens.strokeWidthThin} solid ${tokens.colorNeutralStroke3}`,
    listStyle: 'none',
    ':focus-visible': { outline: `${tokens.strokeWidthThick} solid ${tokens.colorStrokeFocus2}`, outlineOffset: `calc(-1 * ${tokens.strokeWidthThick})` },
  },
  // The overlay's restyle: the item is a card (the grid moves to the card) and, compact, takes less room.
  // The li is the focus target (roving tab stop): it keeps the keyboard focus ring in cards mode too.
  cardItem: {
    listStyle: 'none',
    marginBlockEnd: tokens.spacingVerticalS,
    borderRadius: tokens.borderRadiusMedium,
    ':focus-visible': { outline: `${tokens.strokeWidthThick} solid ${tokens.colorStrokeFocus2}`, outlineOffset: tokens.strokeWidthThin },
  },
  card: { display: 'grid', borderBottom: 'none', paddingBlock: tokens.spacingVerticalS, paddingInline: tokens.spacingHorizontalM },
  compact: { paddingBlock: tokens.spacingVerticalXXS },
  compactCard: { paddingBlock: tokens.spacingVerticalXS, paddingInline: tokens.spacingHorizontalS },
  icon: { fontSize: '20px', marginBlockStart: '6px', color: tokens.colorBrandForeground1 },
  main: { minWidth: 0, display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalXXS },
  title: { overflow: 'hidden', textOverflow: 'ellipsis' },
  // A post's text is the whole post: it wraps and keeps its line breaks.
  postText: { whiteSpace: 'pre-wrap', overflowWrap: 'anywhere' },
  meta: { color: tokens.colorNeutralForeground3 },
  side: { display: 'flex', flexDirection: 'column', alignItems: 'flex-end', justifyContent: 'space-between', gap: tokens.spacingVerticalXS, alignSelf: 'stretch' },
  overdue: { color: tokens.colorPaletteRedForeground1 },
  notice: { color: tokens.colorNeutralForeground3, fontStyle: 'italic' },
  body: { whiteSpace: 'pre-wrap', overflowWrap: 'anywhere' },
  file: { display: 'flex', alignItems: 'center', gap: tokens.spacingHorizontalXS },
  actions: { display: 'flex' },
});

const STATUS_COLOR = { active: 'informative', overdue: 'danger', closed: 'subtle' } as const;

/** A file size as people read it: bytes, KB or MB (1024-based, as the upload limit). */
export function fileSizeText(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${Math.round(bytes / 1024)} KB`;
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
}

/** The quick actions of an item (spec §5.4); an action left out is not shown. */
export interface TimelineItemActions {
  onOpen?: () => void;
  onComplete?: () => void;
  /** A note's Edit. */
  onEdit?: () => void;
  onAssign?: () => void;
  /** A note's Download attachment. */
  onDownload?: () => void;
  onDelete?: () => void;
}

const ACTIONS = [
  { key: 'onOpen', label: 'record.timeline.open', Icon: OpenIcon },
  { key: 'onComplete', label: 'record.timeline.complete', Icon: CompleteIcon },
  { key: 'onEdit', label: 'record.timeline.note.edit', Icon: EditIcon },
  { key: 'onAssign', label: 'record.cmd.assign', Icon: AssignIcon },
  { key: 'onDownload', label: 'record.timeline.note.download', Icon: DownloadIcon },
  { key: 'onDelete', label: 'record.cmd.delete', Icon: DeleteIcon },
] as const;

export interface TimelineItemRowProps {
  item: TimelineItem;
  /** The activity table's display name; a note says Note. */
  typeName: string;
  expanded: boolean;
  onToggle: () => void;
  now: Date;
  /** The user's time zone (IANA): the date shown is the item's day there, its time when it is today. */
  zone: string;
  formatting: PcfFormatting;
  actions?: TimelineItemActions;
  /** 0 for the item the list's one tab stop is on, -1 for the others (the arrows move between them). */
  tabIndex?: number;
  /** The item, or something in it, took the focus. */
  onFocus?: () => void;
  /** Enter on the item; Space expands or collapses it. */
  onOpen?: () => void;
  /** What the title shows instead of `item.title` (a post's text with its mentions as links). */
  titleContent?: ReactNode;
  /** The title as text, for the actions' name (a post's text with its mentions by their display string). */
  titleLabel?: string;
  /** Shown under the item's details (a post's replies). */
  footer?: ReactNode;
  /** The overlay's timeline restyle; absent, the classic look. */
  look?: TimelineAppearance;
}

/**
 * One timeline record: its type icon, the title in bold, the type and owner, the date at the bottom right (the time only when
 * it is today) and, for an activity, its status tag and how many days it is overdue. Expanded, it adds the body and a note's
 * attachment name and size. A post's title is its text and its owner its author; an auto or ActionHub post (`source` 1 or 3) shows "Auto post"
 * and a notice instead of its text, an XML whose format is not documented.
 */
export function TimelineItemRow({ item, typeName, expanded, onToggle, now, zone, formatting, actions = {}, tabIndex, onFocus, onOpen, titleContent, titleLabel, footer, look }: TimelineItemRowProps) {
  const styles = useStyles();
  const Icon = RECORD_TYPE_ICONS[item.table] ?? ActivityIcon;
  const modified = new Date(item.modifiedOn);
  const date = sameDay(item.modifiedOn, now, zone) ? formatting.formatTime(modified) : formatting.formatDateShort(modified);
  const status = item.kind === 'activity' ? activityStatusOf(item, now) : undefined;
  const days = status === 'overdue' ? overdueDays(item, now) : 0;
  const autoPost = item.kind === 'post' && (item.source === 1 || item.source === 3);
  const cards = look?.appearance === 'cards';
  const compact = look?.density === 'compact';
  const content = (
    <>
      <Button
        appearance="transparent"
        size="small"
        icon={expanded ? <CollapseItemIcon /> : <ExpandItemIcon />}
        aria-expanded={expanded}
        aria-label={expanded ? t('record.timeline.hideDetails') : t('record.timeline.showDetails')}
        onClick={onToggle}
      />
      <Icon className={styles.icon} aria-hidden />
      <div className={styles.main}>
        <Text weight="bold" className={mergeClasses(styles.title, item.kind === 'post' && !autoPost && styles.postText)} data-testid="timeline-item-title">
          {autoPost ? t('record.timeline.postsBy.auto') : (titleContent ?? item.title)}
        </Text>
        <Text size={200} className={styles.meta}>
          {[typeName, item.ownerName ?? item.authorName].filter(Boolean).join(' · ')}
        </Text>
        {autoPost && (
          <Text size={200} className={styles.notice}>
            {t('record.timeline.post.autoNotice')}
          </Text>
        )}
        {days >= 1 && (
          <Text size={200} className={styles.overdue}>
            {days === 1 ? t('record.timeline.overdueDay') : t('record.timeline.overdueDays', { count: days })}
          </Text>
        )}
        {expanded && item.body && (
          <Text size={300} className={styles.body}>
            {item.body}
          </Text>
        )}
        {expanded && item.fileName && (
          <Text size={200} className={styles.file}>
            <AttachmentIcon aria-hidden />
            {item.fileSize !== undefined ? `${item.fileName} (${fileSizeText(item.fileSize)})` : item.fileName}
          </Text>
        )}
        {footer}
      </div>
      <div className={styles.side}>
        {status && (
          <Badge appearance="tint" color={STATUS_COLOR[status]} size="small">
            {t(`record.timeline.status.${status}`)}
          </Badge>
        )}
        {ACTIONS.some((a) => actions[a.key]) && (
          <div className={styles.actions} role="toolbar" aria-label={t('record.timeline.itemActions', { title: autoPost ? t('record.timeline.postsBy.auto') : (titleLabel ?? item.title) })}>
            {ACTIONS.map(({ key, label, Icon }) => {
              const run = actions[key];
              return (
                run && (
                  <Tooltip key={key} content={t(label)} relationship="label">
                    <Button appearance="subtle" size="small" icon={<Icon />} onClick={run} />
                  </Tooltip>
                )
              );
            })}
          </div>
        )}
        <Text size={200} className={styles.meta}>
          {date}
        </Text>
      </div>
    </>
  );
  return (
    <li
      className={cards ? styles.cardItem : mergeClasses(styles.row, compact && styles.compact)}
      tabIndex={tabIndex}
      data-timeline-item=""
      onFocus={onFocus}
      onKeyDown={(e) => {
        // The keys of the item itself; on its buttons they keep their own meaning.
        if (e.target !== e.currentTarget || e.altKey || e.ctrlKey || e.metaKey) return;
        if (e.key === 'Enter') {
          e.preventDefault();
          (onOpen ?? onToggle)();
        } else if (e.key === ' ') {
          e.preventDefault();
          onToggle();
        }
      }}
    >
      {cards ? (
        <Card className={mergeClasses(styles.row, styles.card, compact && styles.compactCard)} data-appearance="cards" appearance="outline">
          {content}
        </Card>
      ) : (
        content
      )}
    </li>
  );
}
