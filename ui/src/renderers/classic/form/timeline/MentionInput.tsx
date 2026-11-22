import { makeStyles, mergeClasses, Popover, PopoverSurface, Text, Textarea, tokens } from '@fluentui/react-components';
import { useCallback, useEffect, useId, useLayoutEffect, useRef, useState } from 'react';
import { formatMention } from '../../../../apps/record/timeline/mentions';
import type { XrmApi } from '../../../../shell/api/client';
import { t } from '../../../../shell/i18n/t';

const useStyles = makeStyles({
  root: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalXXS },
  counter: { alignSelf: 'flex-end', color: tokens.colorNeutralForeground3 },
  over: { color: tokens.colorPaletteRedForeground1 },
  surface: { padding: tokens.spacingVerticalXS, minWidth: '240px', maxHeight: '260px', overflowY: 'auto' },
  list: { listStyleType: 'none', margin: 0, padding: 0 },
  option: { display: 'flex', flexDirection: 'column', padding: `${tokens.spacingVerticalXS} ${tokens.spacingHorizontalS}`, cursor: 'pointer', borderRadius: tokens.borderRadiusMedium },
  active: { backgroundColor: tokens.colorNeutralBackground1Selected },
});

interface Candidate {
  id: string;
  display: string;
  email?: string;
}
interface UserRow {
  systemuserid: string;
  fullname?: string | null;
  internalemailaddress?: string | null;
}

export interface MentionInputProps {
  /** The stored text, mention markup included. */
  value: string;
  onChange: (value: string) => void;
  /** 2000 for a post, 1000 for a comment; the counter shows `value.length / maxLength`. */
  maxLength: number;
  label: string;
  /** Web API client that searches the users. */
  api: XrmApi;
  /** The signed-in user: their recent picks are kept under `xrmverse.mentions.recent.{userId}`. */
  userId: string;
  /** Ctrl+Enter (Cmd+Enter): the input does not swallow it. */
  onSubmit?: () => void;
}

const MAX_RECENT = 5;
const recentKey = (userId: string) => `xrmverse.mentions.recent.${userId}`;
const bare = (id: string) => id.replace(/^\{|\}$/g, '').toLowerCase();

function readRecent(userId: string): Candidate[] {
  try {
    const parsed: unknown = JSON.parse(localStorage.getItem(recentKey(userId)) ?? '[]');
    if (!Array.isArray(parsed)) return [];
    return parsed
      .filter((r): r is Candidate => typeof r?.id === 'string' && typeof r?.display === 'string')
      .slice(0, MAX_RECENT)
      .map((r) => ({ id: r.id, display: r.display }));
  } catch {
    return [];
  }
}

function rememberRecent(userId: string, pick: Candidate): void {
  try {
    const next = [{ id: pick.id, display: pick.display }, ...readRecent(userId).filter((r) => bare(r.id) !== bare(pick.id))].slice(0, MAX_RECENT);
    localStorage.setItem(recentKey(userId), JSON.stringify(next));
  } catch {
    // Storage blocked or full: the recent list is a convenience.
  }
}

/** The `@query` that ends at the caret: an `@` at the start or after white space, then characters that are not white space or brackets. */
export function activeMention(text: string, caret: number): { start: number; query: string } | undefined {
  const m = /(?:^|\s)@([^\s@[\]]*)$/.exec(text.slice(0, caret));
  return m ? { start: caret - m[1].length - 1, query: m[1] } : undefined;
}

const odata = (s: string) => s.replace(/'/g, "''");

/**
 * The user search of the @ list (`systemusers`): the full name or the email address starting with the text, or a later word
 * of the full name (the last name) starting with it. xrmverse's `systemuser` has no `firstname` or `lastname` column, and a
 * filter on either is refused. Disabled users are left out.
 */
async function searchUsers(api: XrmApi, query: string): Promise<Candidate[]> {
  const q = odata(encodeURIComponent(query));
  const filter = query
    ? `&$filter=(startswith(fullname,'${q}') or contains(fullname,'%20${q}') or startswith(internalemailaddress,'${q}')) and isdisabled eq false`
    : '';
  const result = await api.get<{ value: UserRow[] }>(`systemusers?$select=systemuserid,fullname,internalemailaddress${filter}&$top=10`);
  return result.value.map((u) => ({ id: u.systemuserid, display: u.fullname || u.internalemailaddress || u.systemuserid, email: u.internalemailaddress ?? undefined }));
}

/** A textarea where typing `@` opens a list of users; picking one writes `@[8,{guid},"Full Name"]` (spec §5, Mentions). */
export function MentionInput({ value, onChange, maxLength, label, api, userId, onSubmit }: MentionInputProps) {
  const styles = useStyles();
  const listId = useId();
  const field = useRef<HTMLTextAreaElement>(null);
  const [active, setActive] = useState<{ start: number; query: string } | undefined>();
  const [dismissed, setDismissed] = useState(false);
  const [found, setFound] = useState<Candidate[]>([]);
  const [index, setIndex] = useState(0);
  const caretAfter = useRef<number | undefined>(undefined);

  const query = active?.query;
  useEffect(() => {
    if (query === undefined) {
      setFound([]);
      return;
    }
    let current = true;
    searchUsers(api, query).then(
      (rows) => current && setFound(rows),
      () => current && setFound([]),
    );
    return () => {
      current = false;
    };
  }, [api, query]);

  // Recent picks first (those that match the text), then the search result without them.
  const needle = (query ?? '').toLowerCase();
  const recent = readRecent(userId).filter((r) => r.display.toLowerCase().includes(needle));
  const seen = new Set(recent.map((r) => bare(r.id)));
  const options = [...recent, ...found.filter((u) => !seen.has(bare(u.id)))];
  const open = active !== undefined && !dismissed && options.length > 0;
  const optionId = (i: number) => `${listId}-${i}`;

  useLayoutEffect(() => {
    if (caretAfter.current !== undefined && field.current) {
      field.current.setSelectionRange(caretAfter.current, caretAfter.current);
      caretAfter.current = undefined;
    }
  }, [value]);

  const track = useCallback((text: string, caret: number) => {
    setActive(activeMention(text, caret));
    setIndex(0);
    setDismissed(false);
  }, []);

  const pick = (c: Candidate) => {
    if (!active) return;
    const caret = field.current?.selectionStart ?? value.length;
    const insert = `${formatMention(c.id, c.display)} `;
    const next = value.slice(0, active.start) + insert + value.slice(caret);
    if (next.length > maxLength) return;
    rememberRecent(userId, { id: bare(c.id), display: c.display });
    caretAfter.current = active.start + insert.length;
    setActive(undefined);
    onChange(next);
    field.current?.focus();
  };

  const onKeyDown = (e: React.KeyboardEvent<HTMLTextAreaElement>) => {
    if (e.key === 'Enter' && (e.ctrlKey || e.metaKey)) {
      onSubmit?.();
      return;
    }
    if (!open) return;
    if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
      e.preventDefault();
      setIndex((i) => (i + (e.key === 'ArrowDown' ? 1 : options.length - 1)) % options.length);
    } else if (e.key === 'Enter' || e.key === 'Tab') {
      e.preventDefault();
      pick(options[Math.min(index, options.length - 1)]);
    } else if (e.key === 'Escape') {
      e.preventDefault();
      e.stopPropagation();
      setDismissed(true);
    }
  };

  return (
    <div className={styles.root}>
      <Textarea
        ref={field}
        resize="vertical"
        aria-label={label}
        aria-autocomplete="list"
        aria-controls={open ? listId : undefined}
        aria-activedescendant={open ? optionId(index) : undefined}
        value={value}
        onChange={(e, d) => {
          // Typing past the limit is refused; shortening is always allowed.
          if (d.value.length > maxLength && d.value.length > value.length) return;
          onChange(d.value);
          track(d.value, e.target.selectionStart ?? d.value.length);
        }}
        onKeyDown={onKeyDown}
        onClick={(e) => track(value, e.currentTarget.selectionStart ?? value.length)}
        onKeyUp={(e) => {
          if (e.key === 'ArrowLeft' || e.key === 'ArrowRight') track(value, e.currentTarget.selectionStart ?? value.length);
        }}
        onBlur={() => setDismissed(true)}
      />
      <Text size={200} className={mergeClasses(styles.counter, value.length > maxLength && styles.over)} aria-live="polite">
        {`${value.length} / ${maxLength}`}
      </Text>
      <Popover open={open} unstable_disableAutoFocus positioning={{ target: field.current?.parentElement ?? field.current, position: 'below', align: 'start' }} trapFocus={false}>
        <PopoverSurface className={styles.surface} tabIndex={-1}>
          <ul id={listId} role="listbox" aria-label={t('record.timeline.mention.users')} className={styles.list}>
            {options.map((o, i) => (
              <li
                key={o.id}
                id={optionId(i)}
                role="option"
                aria-selected={i === index}
                className={mergeClasses(styles.option, i === index && styles.active)}
                onMouseDown={(e) => {
                  e.preventDefault();
                  pick(o);
                }}
                onMouseEnter={() => setIndex(i)}
              >
                <Text weight="semibold">{o.display}</Text>
                {o.email && <Text size={200}>{o.email}</Text>}
              </li>
            ))}
          </ul>
        </PopoverSurface>
      </Popover>
    </div>
  );
}
