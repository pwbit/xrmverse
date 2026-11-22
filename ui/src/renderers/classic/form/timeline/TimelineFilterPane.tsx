import { Button, Checkbox, makeStyles, tokens } from '@fluentui/react-components';
import type { ReactNode } from 'react';
import type { ActivityStatus, DueRange, ModifiedRange, PostsBy, RecordKind, TimelineFilters } from '../../../../apps/record/timeline/filters';
import type { TimelineCounts } from '../../../../apps/record/timeline/timelineQuery';
import { t } from '../../../../shell/i18n/t';
import type { ControlEnv } from '../controlEnv';
import { useStatusReasonLabels } from './useTimeline';

const useStyles = makeStyles({
  pane: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
    padding: tokens.spacingHorizontalS,
    border: `${tokens.strokeWidthThin} solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
  },
  category: { border: 'none', margin: 0, padding: 0, display: 'flex', flexDirection: 'column' },
  legend: { fontWeight: tokens.fontWeightSemibold, paddingInline: 0 },
  count: { color: tokens.colorNeutralForeground3, marginInlineStart: tokens.spacingHorizontalXS },
  clear: { alignSelf: 'flex-start' },
});

const toggle = <T,>(list: T[], value: T): T[] => (list.includes(value) ? list.filter((v) => v !== value) : [...list, value]);

const STATUSES: ActivityStatus[] = ['active', 'overdue', 'closed'];
const DUE: DueRange[] = ['next30d', 'next7d', 'next24h', 'last24h', 'last7d', 'last30d'];
const MODIFIED: ModifiedRange[] = ['last24h', 'last7d', 'last30d'];
const POSTS_BY: PostsBy[] = ['auto', 'users'];

export interface TimelineFilterPaneProps {
  env: ControlEnv;
  filters: TimelineFilters;
  counts?: TimelineCounts;
  hasActivities: boolean;
  hasNotes: boolean;
  /** The timeline shows posts: the Posts record type and the Posts by category. */
  hasPosts?: boolean;
  /** The activity tables shown, in order, and their display names. */
  shownTypes: string[];
  typeNames: Record<string, string>;
  onChange: (f: TimelineFilters) => void;
}

/**
 * The filter pane (spec §5.3): record type, activity type, status, status reason, due date, Posts by, modified date and
 * Owner = Me. The activity categories exist only on a table with activities, Posts by only with posts; the record type only
 * when there are two kinds to choose from. Options within a category are OR, categories AND.
 */
export function TimelineFilterPane({ env, filters, counts, hasActivities, hasNotes, hasPosts = false, shownTypes, typeNames, onChange }: TimelineFilterPaneProps) {
  const styles = useStyles();
  const reasons = hasActivities ? (counts?.byReason ?? []) : [];
  const reasonLabels = useStatusReasonLabels(
    env,
    reasons.map((r) => r.type),
  );
  const typeName = (type: string) => typeNames[type] ?? type;
  const set = (patch: Partial<TimelineFilters>) => onChange({ ...filters, ...patch });

  const option = (key: string, label: string, checked: boolean, onToggle: () => void, count?: number) => (
    <Checkbox
      key={key}
      checked={checked}
      onChange={onToggle}
      label={
        <>
          {label}
          {count !== undefined && (
            <>
              {' '}
              <span className={styles.count}>{count}</span>
            </>
          )}
        </>
      }
    />
  );
  const category = (legend: string, children: ReactNode) => (
    <fieldset className={styles.category}>
      <legend className={styles.legend}>{legend}</legend>
      {children}
    </fieldset>
  );
  const kind = (k: RecordKind, label: string, count?: number) =>
    option(k, label, filters.recordTypes.includes(k), () => set({ recordTypes: toggle(filters.recordTypes, k) }), count);

  return (
    <div role="group" aria-label={t('record.timeline.pane')} className={styles.pane}>
      {[hasActivities, hasNotes, hasPosts].filter(Boolean).length >= 2 &&
        category(t('record.timeline.pane.recordType'), [
          ...(hasNotes ? [kind('note', t('record.timeline.chip.notes'), counts?.notes)] : []),
          ...(hasActivities ? [kind('activity', t('record.timeline.chip.activities'), counts?.activities)] : []),
          ...(hasPosts ? [kind('post', t('record.timeline.chip.posts'), counts?.posts)] : []),
        ])}
      {hasActivities && (
        <>
          {category(
            t('record.timeline.pane.activityType'),
            shownTypes.map((type) =>
              option(type, typeName(type), filters.activityTypes.includes(type), () => set({ activityTypes: toggle(filters.activityTypes, type) }), counts?.byType[type] ?? (counts ? 0 : undefined)),
            ),
          )}
          {category(
            t('record.timeline.pane.status'),
            STATUSES.map((s) => option(s, t(`record.timeline.status.${s}`), filters.statuses.includes(s), () => set({ statuses: toggle(filters.statuses, s) }))),
          )}
          {reasons.length > 0 &&
            category(
              t('record.timeline.pane.reason'),
              reasons.map((r) => {
                const selected = filters.statusReasons.some((x) => x.type === r.type && x.value === r.value);
                const label = t('record.timeline.pane.reasonOption', { type: typeName(r.type), label: reasonLabels[`${r.type}|${r.value}`] ?? String(r.value) });
                return option(
                  `${r.type}|${r.value}`,
                  label,
                  selected,
                  () =>
                    set({
                      statusReasons: selected
                        ? filters.statusReasons.filter((x) => !(x.type === r.type && x.value === r.value))
                        : [...filters.statusReasons, { type: r.type, value: r.value }],
                    }),
                  r.count,
                );
              }),
            )}
          {category(
            t('record.timeline.pane.due'),
            DUE.map((d) => option(d, t(`record.timeline.range.${d}`), filters.due.includes(d), () => set({ due: toggle(filters.due, d) }))),
          )}
        </>
      )}
      {hasPosts &&
        category(
          t('record.timeline.pane.postsBy'),
          POSTS_BY.map((p) =>
            option(
              p,
              t(`record.timeline.postsBy.${p}`),
              filters.postsBy.includes(p),
              () => set({ postsBy: toggle(filters.postsBy, p) }),
              p === 'auto' ? counts?.postsByAuto : counts?.postsByUsers,
            ),
          ),
        )}
      {category(
        t('record.timeline.pane.modified'),
        MODIFIED.map((m) => option(m, t(`record.timeline.range.${m}`), filters.modified.includes(m), () => set({ modified: toggle(filters.modified, m) }))),
      )}
      {category(t('record.timeline.pane.owner'), option('me', t('record.timeline.pane.me'), filters.mine, () => set({ mine: !filters.mine })))}
      <Button
        className={styles.clear}
        size="small"
        onClick={() => onChange({ ...filters, recordTypes: [], activityTypes: [], statuses: [], statusReasons: [], due: [], modified: [], mine: false, postsBy: [] })}
      >
        {t('record.timeline.pane.clear')}
      </Button>
    </div>
  );
}
