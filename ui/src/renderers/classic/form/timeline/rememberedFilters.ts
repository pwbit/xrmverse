import { EMPTY_FILTERS, type TimelineFilters } from '../../../../apps/record/timeline/filters';

/**
 * Remembered timeline filters (spec §5.8, an app option off by default): the chips, the pane filters and the sort order, per
 * user, form and control, in this browser's storage. The search is not remembered. Storage that cannot be read or written
 * (a private window, blocked site data, a full quota) means "not remembered": nothing here throws.
 */

const keyOf = (userId: string, formId: string, controlId: string) => `xrmverse.timeline.${userId}.${formId}.${controlId}`;

const strings = (v: unknown): string[] | undefined => (Array.isArray(v) && v.every((x) => typeof x === 'string') ? v : undefined);

/** Only the values a filter knows; anything else stored is left out. */
const known = <T extends string>(v: unknown, values: readonly T[]): T[] => (strings(v) ?? []).filter((x): x is T => (values as readonly string[]).includes(x));
const RECORD_TYPES = ['activity', 'note', 'post'] as const;
const POSTS_BY = ['auto', 'users'] as const;
const STATUSES = ['active', 'overdue', 'closed'] as const;
const DUE = ['next30d', 'next7d', 'next24h', 'last24h', 'last7d', 'last30d'] as const;
const MODIFIED = ['last24h', 'last7d', 'last30d'] as const;

/** The stored value as filters, each part checked; a part that is not what it should be keeps its default. */
function parse(raw: string): TimelineFilters | undefined {
  const v: unknown = JSON.parse(raw);
  if (!v || typeof v !== 'object' || Array.isArray(v)) return undefined;
  const o = v as Record<string, unknown>;
  const reasons = Array.isArray(o.statusReasons)
    ? o.statusReasons.filter((r): r is { type: string; value: number } => !!r && typeof r === 'object' && typeof r.type === 'string' && typeof r.value === 'number')
    : undefined;
  return {
    ...EMPTY_FILTERS,
    recordTypes: known(o.recordTypes, RECORD_TYPES),
    activityTypes: strings(o.activityTypes) ?? [],
    statuses: known(o.statuses, STATUSES),
    statusReasons: reasons?.map((r) => ({ type: r.type, value: r.value })) ?? [],
    due: known(o.due, DUE),
    modified: known(o.modified, MODIFIED),
    mine: o.mine === true,
    postsBy: known(o.postsBy, POSTS_BY),
    sort: o.sort === 'asc' ? 'asc' : 'desc',
  };
}

/** The filters remembered for the user's timeline `controlId` on form `formId`, or undefined when none are. */
export function readRemembered(userId: string, formId: string, controlId: string): TimelineFilters | undefined {
  try {
    const raw = localStorage.getItem(keyOf(userId, formId, controlId));
    return raw === null ? undefined : parse(raw);
  } catch {
    return undefined;
  }
}

/** Remembers `filters` (without the search) for the user's timeline `controlId` on form `formId`; a failure is ignored. */
export function writeRemembered(userId: string, formId: string, controlId: string, filters: TimelineFilters): void {
  try {
    const { search: _search, ...kept } = filters;
    localStorage.setItem(keyOf(userId, formId, controlId), JSON.stringify(kept));
  } catch {
    // Not remembered.
  }
}
