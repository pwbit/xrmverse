import { Button, Field, makeStyles, mergeClasses, Spinner, Text, tokens } from '@fluentui/react-components';
import { useContext, useEffect, useId, useRef, useState, type ClipboardEvent, type KeyboardEvent } from 'react';
import { useDebounced } from '../../../admin/common/useDebounced';
import type { AttributeMeta } from '../../../admin/tables/metadata';
import { addParties, isEmailAddress, splitAddresses, type Party } from '../../../admin/tables/partyLists';
import { entityPath } from '../../../admin/tables/tableApi';
import type { LookupOptions, LookupResult } from '../../../client-api/DialogHost';
import { XrmHostContext } from '../../../client-api/xrm';
import type { XrmApi } from '../../../shell/api/client';
import { literal } from '../../../shell/api/odata';
import { useAuth } from '../../../shell/auth/AuthProvider';
import { t } from '../../../shell/i18n/t';
import { CollapseItemIcon, DismissIcon, LookupSearchIcon, PARTY_ICONS, RECORD_TYPE_ICONS, SiteMapTableIcon } from '../../../shell/icons';

export type { Party } from '../../../admin/tables/partyLists';

/**
 * The sender policy of an email's `from` (plan 4.1b, the xrmverse setting `xrmverse_EmailSendFrom`): `userOrQueue` offers the
 * caller and the queues the caller can read, `queueOnly` the queues only.
 */
export interface SenderPolicy {
  mode: 'userOrQueue' | 'queueOnly';
  /** The signed-in user (`WhoAmI`). */
  userId: string;
}

export interface PartyListControlProps {
  attr: AttributeMeta;
  value: Party[];
  onChange: (next: Party[]) => void;
  disabled?: boolean;
  error?: string;
  /** An email's `from`: one party, chosen among the senders the policy allows. */
  sender?: SenderPolicy;
}

/** The party lists that hold one party (Dataverse: the sender and the organizer). */
const SINGLE = new Set(['from', 'organizer']);

/**
 * The email column the search reads for each party table, as Dataverse names it; a table not listed is searched by its primary
 * name only.
 */
const EMAIL_COLUMNS: Record<string, string> = {
  account: 'emailaddress1',
  contact: 'emailaddress1',
  systemuser: 'internalemailaddress',
  queue: 'emailaddress',
};

/** Results per table while typing. */
const PER_TABLE = 5;
const SEARCH_DELAY_MS = 300;

interface Target {
  table: string;
  set: string;
  idAttribute: string;
  nameAttribute: string;
  emailAttribute?: string;
  label: string;
}

interface Group {
  target: Target;
  parties: Party[];
}

interface RawTarget {
  EntitySetName?: string | null;
  PrimaryIdAttribute?: string | null;
  PrimaryNameAttribute?: string | null;
  DisplayCollectionName?: { UserLocalizedLabel?: { Label?: string } | null } | null;
}

/** The searchable party tables of the column; a table whose metadata cannot be read is left out. */
async function loadTargets(api: XrmApi, tables: string[]): Promise<Target[]> {
  const found = await Promise.all(
    tables.map(async (table): Promise<Target | undefined> => {
      try {
        const m = await api.get<RawTarget>(`${entityPath(table)}?$select=EntitySetName,PrimaryIdAttribute,PrimaryNameAttribute,DisplayCollectionName`);
        if (!m.EntitySetName || !m.PrimaryNameAttribute) return undefined;
        return {
          table,
          set: m.EntitySetName,
          idAttribute: m.PrimaryIdAttribute || `${table}id`,
          nameAttribute: m.PrimaryNameAttribute,
          emailAttribute: EMAIL_COLUMNS[table],
          label: m.DisplayCollectionName?.UserLocalizedLabel?.Label || table,
        };
      } catch {
        return undefined;
      }
    }),
  );
  return found.filter((x): x is Target => !!x);
}

const text = (v: unknown): string | undefined => (typeof v === 'string' && v ? v : undefined);

function partyOf(target: Target, row: Record<string, unknown>): Party {
  const address = target.emailAttribute ? text(row[target.emailAttribute]) : undefined;
  return { id: String(row[target.idAttribute]), logicalName: target.table, name: text(row[target.nameAttribute]), ...(address ? { address } : {}) };
}

async function query(api: XrmApi, target: Target, filter: string, top: number): Promise<Party[]> {
  const select = [target.idAttribute, target.nameAttribute, ...(target.emailAttribute ? [target.emailAttribute] : [])].join(',');
  const result = await api.get<{ value: Record<string, unknown>[] }>(`${target.set}?$select=${select}&$filter=${encodeURIComponent(filter)}&$top=${top}`);
  return result.value.map((row) => partyOf(target, row));
}

/** Typing: the primary name and the email column of each table contain the term, 5 per table. */
async function searchParties(api: XrmApi, targets: Target[], term: string): Promise<Group[]> {
  const lit = literal(term);
  const groups = await Promise.all(
    targets.map(async (target) => {
      const filter = [`contains(${target.nameAttribute},${lit})`, ...(target.emailAttribute ? [`contains(${target.emailAttribute},${lit})`] : [])].join(' or ');
      try {
        return { target, parties: await query(api, target, filter, PER_TABLE) };
      } catch {
        return { target, parties: [] };
      }
    }),
  );
  return groups.filter((g) => g.parties.length > 0);
}

/** The records whose email column is `address` (any case on SQL Server, as the server compares). */
async function recordsWithAddress(api: XrmApi, targets: Target[], address: string): Promise<{ target: Target; party: Party }[]> {
  const found = await Promise.all(
    targets
      .filter((target) => target.emailAttribute)
      .map(async (target) => {
        try {
          return (await query(api, target, `${target.emailAttribute} eq ${literal(address)}`, PER_TABLE)).map((party) => ({ target, party }));
        } catch {
          return [];
        }
      }),
  );
  return found.flat();
}

const useStyles = makeStyles({
  // The box of the lookup field (Fluent Input): the chips, the text and the search button on one line that wraps.
  box: {
    display: 'flex',
    flexWrap: 'wrap',
    alignItems: 'center',
    gap: tokens.spacingHorizontalXS,
    minHeight: '32px',
    boxSizing: 'border-box',
    paddingInline: tokens.spacingHorizontalS,
    paddingBlock: tokens.spacingVerticalXXS,
    border: `${tokens.strokeWidthThin} solid ${tokens.colorNeutralStroke1}`,
    borderBottomColor: tokens.colorNeutralStrokeAccessible,
    borderRadius: tokens.borderRadiusMedium,
    backgroundColor: tokens.colorNeutralBackground1,
    ':focus-within': { borderBottomColor: tokens.colorCompoundBrandStroke },
  },
  readOnly: { border: 'none', backgroundColor: 'transparent', paddingInline: 0 },
  list: { display: 'contents', listStyleType: 'none', margin: 0, padding: 0 },
  // The chip of the Share pane.
  chip: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalXS,
    paddingInlineStart: tokens.spacingHorizontalS,
    paddingInlineEnd: tokens.spacingHorizontalS,
    minHeight: '24px',
    borderRadius: tokens.borderRadiusCircular,
    backgroundColor: tokens.colorBrandBackground2,
    color: tokens.colorBrandForeground2,
  },
  removable: { paddingInlineEnd: 0 },
  unresolved: {
    backgroundColor: 'transparent',
    color: tokens.colorNeutralForeground1,
    border: `${tokens.strokeWidthThin} dashed ${tokens.colorNeutralStroke1}`,
  },
  note: { color: tokens.colorNeutralForeground3, fontSize: tokens.fontSizeBase200 },
  input: {
    flexGrow: 1,
    minWidth: '8rem',
    border: 'none',
    outlineStyle: 'none',
    backgroundColor: 'transparent',
    color: tokens.colorNeutralForeground1,
    fontFamily: 'inherit',
    fontSize: tokens.fontSizeBase300,
    paddingBlock: tokens.spacingVerticalXXS,
  },
  actions: { display: 'flex', alignItems: 'center', marginInlineStart: 'auto' },
  anchor: { position: 'relative' },
  popup: {
    position: 'absolute',
    zIndex: 10,
    insetInlineStart: 0,
    top: '100%',
    marginTop: tokens.spacingVerticalXXS,
    minWidth: 'min(28rem, 100%)',
    maxHeight: '20rem',
    overflowY: 'auto',
    padding: tokens.spacingVerticalXS,
    backgroundColor: tokens.colorNeutralBackground1,
    borderRadius: tokens.borderRadiusMedium,
    boxShadow: tokens.shadow16,
  },
  heading: { display: 'block', padding: `${tokens.spacingVerticalXS} ${tokens.spacingHorizontalS}`, color: tokens.colorNeutralForeground3, fontSize: tokens.fontSizeBase200 },
  option: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
    padding: `${tokens.spacingVerticalS} ${tokens.spacingHorizontalS}`,
    borderRadius: tokens.borderRadiusMedium,
    cursor: 'pointer',
    ':hover': { backgroundColor: tokens.colorNeutralBackground1Hover },
  },
  active: { backgroundColor: tokens.colorNeutralBackground1Hover },
  refused: { color: tokens.colorPaletteRedForeground1, cursor: 'not-allowed', ':hover': { backgroundColor: 'transparent' } },
});

function PartyIcon({ party }: { party: Party }) {
  const Icon = party.id ? (PARTY_ICONS[party.logicalName ?? ''] ?? SiteMapTableIcon) : RECORD_TYPE_ICONS.email!;
  return <Icon aria-hidden />;
}

const shown = (p: Party) => p.name ?? p.address ?? p.id ?? '';
const optionText = (p: Party) => (p.address ? `${shown(p)} · ${p.address}` : shown(p));

/** What the popup under the box offers. */
type Popup =
  | { kind: 'results'; groups: Group[] }
  | { kind: 'ambiguous'; address: string; found: { target: Target; party: Party }[] }
  | { kind: 'senders'; options: { party: Party; refused: boolean }[] | undefined };

/**
 * A party list column (`to`, `cc`, `from`, `requiredattendees`…) on a model-driven form, in the look of the lookup field: the
 * parties as chips, a text box that searches the column's tables (the primary name and the email column, 5 per table,
 * grouped) and the search button that opens the lookup dialog (`Xrm.Utility.lookupObjects`, multi-select). A typed address
 * that matches exactly one record resolves to it, several ask, none stays an unresolved chip (`addressused`); pasted
 * addresses separated by `;` or `,` add one chip each. `from` and `organizer` take one party; an email's `from` offers the
 * senders the policy allows.
 */
export function PartyListControl({ attr, value, onChange, disabled, error, sender }: PartyListControlProps) {
  const styles = useStyles();
  const { api } = useAuth();
  const host = useContext(XrmHostContext);
  const popupId = useId();
  const single = !!sender || SINGLE.has(attr.logicalName);
  const label = attr.displayName;
  const [targets, setTargets] = useState<Target[]>();
  const [term, setTerm] = useState('');
  const debounced = useDebounced(term.trim(), SEARCH_DELAY_MS);
  const [popup, setPopup] = useState<Popup>();
  const [busy, setBusy] = useState(false);
  const [active, setActive] = useState(0);
  // The latest value: an address resolved after a request adds to what the list holds by then.
  const latest = useRef(value);
  latest.current = value;
  const targetsKey = attr.targets.join(',');

  useEffect(() => {
    if (!api || disabled) return;
    let live = true;
    void loadTargets(api, attr.targets).then((found) => live && setTargets(found));
    return () => {
      live = false;
    };
    // The targets are the column's; their text stands for them.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [api, disabled, targetsKey]);

  useEffect(() => {
    if (!api || !targets || !debounced || sender) return;
    let live = true;
    void searchParties(api, targets, debounced).then((groups) => {
      if (!live) return;
      setActive(0);
      setPopup({ kind: 'results', groups });
    });
    return () => {
      live = false;
    };
  }, [api, targets, debounced, sender]);

  const commit = (added: Party[]) => {
    const next = single ? added.slice(-1) : addParties(latest.current, added);
    latest.current = next;
    onChange(next);
  };
  const pick = (party: Party) => {
    commit([party]);
    setTerm('');
    setPopup(undefined);
  };

  /** Adds each address: resolved when it matches one record, asked when several, unresolved when none. */
  async function addAddresses(addresses: string[]) {
    if (!api || addresses.length === 0) return;
    setBusy(true);
    try {
      const list = targets ?? (await loadTargets(api, attr.targets));
      const added: Party[] = [];
      let asked: Popup | undefined;
      for (const address of addresses) {
        const found = await recordsWithAddress(api, list, address);
        if (found.length === 1) added.push({ ...found[0]!.party, address: found[0]!.party.address ?? address });
        else if (found.length === 0) added.push({ address });
        else asked ??= { kind: 'ambiguous', address, found };
      }
      if (added.length > 0) commit(added);
      setTerm('');
      setActive(0);
      setPopup(asked);
    } finally {
      setBusy(false);
    }
  }

  const options = (): Party[] =>
    popup?.kind === 'results'
      ? popup.groups.flatMap((g) => g.parties)
      : popup?.kind === 'ambiguous'
        ? [...popup.found.map((f) => f.party), { address: popup.address }]
        : [];

  const onKeyDown = (e: KeyboardEvent<HTMLInputElement>) => {
    const all = options();
    if (e.key === 'ArrowDown' && all.length) {
      e.preventDefault();
      setActive((i) => (i + 1) % all.length);
    } else if (e.key === 'ArrowUp' && all.length) {
      e.preventDefault();
      setActive((i) => (i - 1 + all.length) % all.length);
    } else if (e.key === 'Escape') {
      setPopup(undefined);
    } else if (e.key === 'Enter' || e.key === ';' || e.key === ',') {
      const typed = term.trim();
      if (!typed && e.key !== 'Enter') return;
      e.preventDefault();
      const parts = splitAddresses(typed);
      if (parts.length > 0 && parts.every(isEmailAddress)) void addAddresses(parts);
      else if (e.key === 'Enter' && all[active]) pick(all[active]!);
    } else if (e.key === 'Backspace' && !term && latest.current.length > 0) {
      onChange(latest.current.slice(0, -1));
    }
  };

  const onPaste = (e: ClipboardEvent<HTMLInputElement>) => {
    const pasted = e.clipboardData.getData('text');
    const parts = splitAddresses(pasted);
    if (parts.length === 0 || !parts.every(isEmailAddress)) return;
    e.preventDefault();
    void addAddresses(parts);
  };

  const onBlur = () => {
    const typed = term.trim();
    if (typed && isEmailAddress(typed) && popup?.kind !== 'ambiguous') void addAddresses([typed]);
  };

  async function openLookup() {
    const lookupObjects = host?.xrm.Utility.lookupObjects as ((o: LookupOptions) => Promise<LookupResult[]>) | undefined;
    if (!lookupObjects) return;
    const picked = await lookupObjects({
      entityTypes: attr.targets,
      defaultEntityType: attr.targets.includes('contact') ? 'contact' : attr.targets[0],
      allowMultiSelect: !single,
    });
    if (picked.length === 0) return;
    commit(picked.map((p) => ({ id: p.id.replace(/[{}]/g, '').toLowerCase(), logicalName: p.entityType, name: p.name })));
  }

  async function openSenders() {
    if (!api || !sender) return;
    if (popup?.kind === 'senders') return setPopup(undefined);
    setPopup({ kind: 'senders', options: undefined });
    const [user, queues] = await Promise.all([
      api
        .get<{ fullname?: string | null; internalemailaddress?: string | null }>(`systemusers(${sender.userId})?$select=fullname,internalemailaddress`)
        .catch(() => undefined),
      api.get<{ value: { queueid: string; name?: string | null; emailaddress?: string | null }[] }>('queues?$select=queueid,name,emailaddress&$orderby=name').catch(() => ({ value: [] })),
    ]);
    const senders: { party: Party; refused: boolean }[] = [];
    if (user)
      senders.push({
        party: { id: sender.userId, logicalName: 'systemuser', name: text(user.fullname), ...(text(user.internalemailaddress) ? { address: user.internalemailaddress! } : {}) },
        refused: sender.mode === 'queueOnly',
      });
    const sorted = [...queues.value].sort((a, b) => (a.name ?? '').localeCompare(b.name ?? ''));
    for (const q of sorted) senders.push({ party: { id: q.queueid, logicalName: 'queue', name: text(q.name), ...(text(q.emailaddress) ? { address: q.emailaddress! } : {}) }, refused: false });
    setPopup((p) => (p?.kind === 'senders' ? { kind: 'senders', options: senders } : p));
  }

  const chipList = (
    <ul className={styles.list} aria-label={label}>
      {value.map((party, i) => (
        <li key={`${party.id ?? party.address}-${i}`} className={mergeClasses(styles.chip, !party.id && styles.unresolved, !disabled && styles.removable)} title={party.address}>
          <PartyIcon party={party} />
          <span>{shown(party)}</span>
          {!party.id && <span className={styles.note}>{t('form.partyList.unresolved')}</span>}
          {!disabled && (
            <Button
              appearance="transparent"
              size="small"
              icon={<DismissIcon />}
              aria-label={t('form.partyList.remove', { name: shown(party) })}
              onClick={() => onChange(latest.current.filter((_, j) => j !== i))}
            />
          )}
        </li>
      ))}
    </ul>
  );

  if (disabled) {
    return (
      <Field label={label} required={attr.required} validationMessage={error}>
        <div className={mergeClasses(styles.box, styles.readOnly)} data-field-box="">{value.length > 0 ? chipList : <Text>{t('field.empty')}</Text>}</div>
      </Field>
    );
  }

  const optionProps = (party: Party, index: number, refused = false) => ({
    role: 'option' as const,
    'aria-selected': index === active,
    'aria-disabled': refused || undefined,
    className: mergeClasses(styles.option, index === active && styles.active, refused && styles.refused),
    // The text box keeps the focus: a click picks without blurring it first.
    onMouseDown: (e: React.MouseEvent) => e.preventDefault(),
    onClick: () => {
      if (refused) return;
      pick(party);
    },
  });

  let index = -1;
  const popupContent =
    popup?.kind === 'results' ? (
      popup.groups.length === 0 ? (
        <Text className={styles.heading}>{t('form.partyList.noResults')}</Text>
      ) : (
        popup.groups.map((g) => (
          <div key={g.target.table} role="group" aria-label={g.target.label}>
            <Text className={styles.heading}>{g.target.label}</Text>
            {g.parties.map((p) => {
              index += 1;
              return (
                <div key={`${g.target.table}-${p.id}`} {...optionProps(p, index)}>
                  <PartyIcon party={p} />
                  <span>{optionText(p)}</span>
                </div>
              );
            })}
          </div>
        ))
      )
    ) : popup?.kind === 'ambiguous' ? (
      <>
        <Text className={styles.heading}>{t('form.partyList.ambiguous', { address: popup.address })}</Text>
        {popup.found.map(({ target, party }) => {
          index += 1;
          return (
            <div key={`${target.table}-${party.id}`} {...optionProps({ ...party, address: party.address ?? popup.address }, index)}>
              <PartyIcon party={party} />
              <span>
                {shown(party)} · {target.label}
              </span>
            </div>
          );
        })}
        <div {...optionProps({ address: popup.address }, ++index)}>
          <PartyIcon party={{ address: popup.address }} />
          <span>{t('form.partyList.keepAddress', { address: popup.address })}</span>
        </div>
      </>
    ) : popup?.kind === 'senders' ? (
      <>
        <Text className={styles.heading}>{t(sender?.mode === 'queueOnly' ? 'form.partyList.sendFromQueues' : 'form.partyList.sendFrom')}</Text>
        {!popup.options ? (
          <Spinner size="tiny" label={t('common.loading')} />
        ) : (
          popup.options.map(({ party, refused }) => {
            index += 1;
            return (
              <div key={`${party.logicalName}-${party.id}`} {...optionProps(party, index, refused)}>
                <PartyIcon party={party} />
                <span>{refused ? `${shown(party)} — ${t('form.partyList.notAllowed')}` : optionText(party)}</span>
              </div>
            );
          })
        )}
      </>
    ) : null;

  const lookupAvailable = !sender && typeof host?.xrm.Utility.lookupObjects === 'function';
  return (
    <Field label={label} required={attr.required} validationMessage={error}>
      <div
        className={styles.anchor}
        // Focus leaving the field closes its popup.
        onBlur={(e) => {
          if (!(e.relatedTarget instanceof Node && e.currentTarget.contains(e.relatedTarget))) setPopup((p) => (p?.kind === 'ambiguous' ? p : undefined));
        }}
      >
        <div className={styles.box} data-field-box="">
          {chipList}
          {!sender && (
            <input
              className={styles.input}
              role="combobox"
              aria-label={label}
              aria-expanded={!!popup}
              aria-controls={popupId}
              aria-autocomplete="list"
              placeholder={value.length === 0 || !single ? t('form.partyList.placeholder') : undefined}
              value={term}
              onChange={(e) => {
                setTerm(e.target.value);
                if (!e.target.value.trim()) setPopup(undefined);
              }}
              onKeyDown={onKeyDown}
              onPaste={onPaste}
              onBlur={onBlur}
            />
          )}
          <span className={styles.actions}>
            {busy && <Spinner size="extra-tiny" aria-label={t('common.loading')} />}
            {sender && (
              <Button appearance="transparent" size="small" icon={<CollapseItemIcon />} aria-label={t('form.partyList.choose', { label })} aria-expanded={popup?.kind === 'senders'} onClick={() => void openSenders()} />
            )}
            {lookupAvailable && (
              <Button appearance="transparent" size="small" icon={<LookupSearchIcon />} aria-label={t('form.partyList.search', { label })} onClick={() => void openLookup()} />
            )}
          </span>
        </div>
        {popup && (
          <div id={popupId} className={styles.popup} role="listbox" aria-label={label}>
            {popupContent}
          </div>
        )}
      </div>
    </Field>
  );
}
