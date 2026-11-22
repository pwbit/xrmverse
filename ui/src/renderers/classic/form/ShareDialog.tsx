import {
  Button,
  Checkbox,
  Dialog,
  DialogSurface,
  DialogTitle,
  Field,
  Label,
  makeStyles,
  mergeClasses,
  MessageBar,
  MessageBarBody,
  Select,
  Spinner,
  Tab,
  Table,
  TableBody,
  TableCell,
  TableCellLayout,
  TableHeader,
  TableHeaderCell,
  TableRow,
  TabList,
  tokens,
  Tooltip,
} from '@fluentui/react-components';
import { DeleteRegular, DismissRegular, PeopleTeamRegular, PersonRegular } from '@fluentui/react-icons';
import { useCallback, useEffect, useState } from 'react';
import { EntityPicker } from '../../../admin/common/EntityPicker';
import {
  grantAccess,
  modifyAccess,
  retrieveShares,
  revokeAccess,
  type AccessRight,
  type Principal,
  type PrincipalType,
  type Share,
} from '../../../apps/record/access';
import { dialogSurface } from '../../../client-api/DialogHost';
import type { XrmApi } from '../../../shell/api/client';
import { useAuth } from '../../../shell/auth/AuthProvider';
import { t } from '../../../shell/i18n/t';

/** The rights the pane offers, in the order of the mask it sends and of its columns. */
export const SHARE_RIGHTS = ['Read', 'Write', 'Delete', 'Append', 'AppendTo', 'Assign', 'Share'] as const satisfies readonly AccessRight[];
type ShareRight = (typeof SHARE_RIGHTS)[number];

const rightLabel = (right: ShareRight) => t(`record.share.right.${right}`);
const sameRights = (a: AccessRight[], b: AccessRight[]) => a.length === b.length && a.every((r) => b.includes(r));
/** The checked rights in the pane's order, then the rights of the original share that the pane does not offer. */
const mask = (checked: AccessRight[], original: AccessRight[] = []) => [
  ...SHARE_RIGHTS.filter((r) => checked.includes(r)),
  ...original.filter((r) => !(SHARE_RIGHTS as readonly AccessRight[]).includes(r)),
];
const keyOf = (p: Principal) => `${p.type}:${p.id.toLowerCase()}`;

const SOURCES = {
  systemuser: { set: 'systemusers', id: 'systemuserid', name: 'fullname' },
  team: { set: 'teams', id: 'teamid', name: 'name' },
} as const;

interface Row extends Share {
  /** The user's full name or the team's name; undefined when it could not be read. */
  name?: string;
}

interface Candidate extends Principal {
  name: string;
}

/** The display name of a user or a team; undefined when it cannot be read (the pane then says so instead of showing the id). */
async function principalName(api: XrmApi, principal: Principal): Promise<string | undefined> {
  const source = SOURCES[principal.type];
  try {
    const row = await api.get<Record<string, unknown>>(`${source.set}(${principal.id})?$select=${source.name}`);
    const name = row[source.name];
    return typeof name === 'string' && name.trim() ? name : undefined;
  } catch {
    return undefined;
  }
}

const useStyles = makeStyles({
  surface: { display: 'flex', flexDirection: 'column', maxWidth: '100vw', maxHeight: '100vh', padding: 0, overflow: 'hidden' },
  side: { marginRight: 0, marginLeft: 'auto', height: '100vh', borderRadius: 0 },
  header: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    paddingInline: tokens.spacingHorizontalXL,
    paddingBlockStart: tokens.spacingVerticalL,
  },
  tabs: { paddingInline: tokens.spacingHorizontalM, borderBottom: `${tokens.strokeWidthThin} solid ${tokens.colorNeutralStroke2}` },
  body: { flexGrow: 1, overflowY: 'auto', padding: tokens.spacingHorizontalXL, display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalM },
  footer: {
    display: 'flex',
    justifyContent: 'flex-end',
    gap: tokens.spacingHorizontalS,
    padding: tokens.spacingHorizontalXL,
    borderTop: `${tokens.strokeWidthThin} solid ${tokens.colorNeutralStroke2}`,
  },
  table: { tableLayout: 'auto' },
  rightHeader: { textAlign: 'center', whiteSpace: 'nowrap', width: '1%' },
  rightCell: { textAlign: 'center' },
  changed: { backgroundColor: tokens.colorPaletteYellowBackground1 },
  removed: { textDecoration: 'line-through', color: tokens.colorNeutralForeground3 },
  unknown: { color: tokens.colorNeutralForeground3, fontStyle: 'italic' },
  rights: { display: 'flex', flexWrap: 'wrap', columnGap: tokens.spacingHorizontalM },
  chips: { display: 'flex', flexWrap: 'wrap', gap: tokens.spacingHorizontalS },
  chip: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalXS,
    paddingInlineStart: tokens.spacingHorizontalS,
    borderRadius: tokens.borderRadiusCircular,
    backgroundColor: tokens.colorBrandBackground2,
    color: tokens.colorBrandForeground2,
  },
});

function PrincipalIcon({ type }: { type: PrincipalType }) {
  return type === 'team' ? <PeopleTeamRegular aria-hidden /> : <PersonRegular aria-hidden />;
}

/** The label of a row: the name, else "Unknown user/team" (never the bare id). */
const displayName = (row: Row) => row.name ?? t(row.principal.type === 'team' ? 'record.share.unknownTeam' : 'record.share.unknownUser');

/** Runs an operation on each record of a selection and reports each failure (a view's bulk runner, ruling W9). */
export type BulkRunner = (op: (id: string) => Promise<void>) => Promise<boolean>;

export type ShareDialogProps = {
  /** Entity set and id of the record. */
  set: string;
  id: string;
  onClose: () => void;
} & (
  | {
      /**
       * The records to share, when there are several (a view's selection): the pane then shows only the Add tab, and Share
       * grants the access on each of them through `onBulk`, which reports each record (one record: as without `ids`).
       */
      ids: string[];
      /** Runs the grant on each of `ids` (the view's bulk runner); true when the pane may close. Required with `ids`. */
      onBulk: BulkRunner;
    }
  | { ids?: undefined; onBulk?: undefined }
);

type TabId = 'current' | 'add';

/**
 * The Share command, in a side pane (as a lookup's record opens). Two tabs:
 * - Current shares: one row per user or team with its name, one column per right. Edits are kept until Save
 *   (`ModifyAccess`; a row left with no right is removed); Remove takes the share away at once (`RevokeAccess`).
 * - Add: users and teams picked by name, the rights to give them, then Share (`GrantAccess` for each).
 * With several records (`ids`, a view's selection, ruling W10) only the Add tab shows, and Share grants on each record.
 */
export function ShareDialog({ set, id, ids, onBulk, onClose }: ShareDialogProps) {
  const styles = useStyles();
  const { api } = useAuth();
  const many = (ids?.length ?? 0) > 1;
  const [tab, setTab] = useState<TabId>(many ? 'add' : 'current');
  const [rows, setRows] = useState<Row[]>();
  const [error, setError] = useState<string>();
  const [busy, setBusy] = useState(false);
  // Edits of the current shares by principal key; they survive a reload of the list (a removed principal drops out).
  const [edits, setEdits] = useState<Record<string, AccessRight[]>>({});
  const [version, setVersion] = useState(0);
  // The Add tab.
  const [pickType, setPickType] = useState<PrincipalType>('systemuser');
  const [candidates, setCandidates] = useState<Candidate[]>([]);
  const [rights, setRights] = useState<AccessRight[]>(['Read']);

  useEffect(() => {
    if (!api || many) return;
    let cancelled = false;
    retrieveShares(api, set, id)
      .then(async (shares) => {
        const named = await Promise.all(shares.map(async (s) => ({ ...s, name: await principalName(api, s.principal) })));
        named.sort((a, b) => (a.principal.type === b.principal.type ? displayName(a).localeCompare(displayName(b)) : a.principal.type === 'systemuser' ? -1 : 1));
        if (cancelled) return;
        setRows(named);
        const present = new Set(named.map((r) => keyOf(r.principal)));
        setEdits((e) => Object.fromEntries(Object.entries(e).filter(([k]) => present.has(k))));
      })
      .catch((e: unknown) => !cancelled && setError(e instanceof Error ? e.message : String(e)));
    return () => {
      cancelled = true;
    };
  }, [api, set, id, version, many]);

  const run = useCallback(
    async (work: (api: XrmApi) => Promise<void>): Promise<boolean> => {
      if (!api || busy) return false;
      setBusy(true);
      setError(undefined);
      try {
        await work(api);
        return true;
      } catch (e) {
        setError(e instanceof Error ? e.message : String(e));
        return false;
      } finally {
        setBusy(false);
        setVersion((v) => v + 1);
      }
    },
    [api, busy],
  );

  const checkedOf = (row: Row) => edits[keyOf(row.principal)] ?? row.rights;
  const changedRows = (rows ?? []).filter((row) => {
    const edit = edits[keyOf(row.principal)];
    return edit !== undefined && !sameRights(mask(edit, row.rights), row.rights);
  });

  const toggle = (row: Row, right: ShareRight, on: boolean) => {
    const current = checkedOf(row);
    const next = on ? [...current, right] : current.filter((r) => r !== right);
    setEdits((e) => ({ ...e, [keyOf(row.principal)]: next }));
  };

  const saveChanges = async () => {
    await run(async (a) => {
      for (const row of changedRows) {
        const next = mask(edits[keyOf(row.principal)]!, row.rights);
        if (next.length === 0) await revokeAccess(a, set, id, row.principal);
        else await modifyAccess(a, set, id, row.principal, next);
      }
      // Cleared in the same render that ends the save, so an edit made right after it is kept.
      setEdits({});
    });
  };

  const remove = (row: Row) => void run((a) => revokeAccess(a, set, id, row.principal));

  const addCandidate = (picked: { id: string; name: string }) => {
    setCandidates((list) => (list.some((c) => c.type === pickType && c.id === picked.id) ? list : [...list, { type: pickType, id: picked.id, name: picked.name }]));
  };

  const grantAll = async (a: XrmApi, record: string) => {
    for (const c of candidates) await grantAccess(a, set, record, { type: c.type, id: c.id }, mask(rights));
  };

  const share = async () => {
    if (many && onBulk) {
      // Each record is granted on its own and reported by the bulk runner: a failure does not stop the others (W9).
      if (!api || busy) return;
      setBusy(true);
      try {
        if (await onBulk((record) => grantAll(api, record))) onClose();
      } finally {
        setBusy(false);
      }
      return;
    }
    const ok = await run((a) => grantAll(a, id));
    if (ok) {
      setCandidates([]);
      setRights(['Read']);
      setTab('current');
    }
  };

  const { style } = dialogSurface({ position: 2, width: { value: 66, unit: '%' } });
  const shared = new Set((rows ?? []).map((r) => keyOf(r.principal)));

  return (
    <Dialog open modalType="modal" onOpenChange={(_, d) => !d.open && onClose()}>
      <DialogSurface className={mergeClasses(styles.surface, styles.side)} style={{ ...style, minWidth: 'min(100vw, 640px)' }} aria-label={t('record.share.title')}>
        <div className={styles.header}>
          <DialogTitle>{t('record.share.title')}</DialogTitle>
          <Button appearance="subtle" icon={<DismissRegular />} aria-label={t('common.close')} onClick={onClose} />
        </div>
        <TabList className={styles.tabs} selectedValue={tab} onTabSelect={(_, d) => setTab(d.value as TabId)}>
          {!many && <Tab value="current">{t('record.share.tab.current', { count: rows?.length ?? 0 })}</Tab>}
          <Tab value="add">{t('record.share.tab.add')}</Tab>
        </TabList>
        <div className={styles.body}>
          {error && (
            <MessageBar intent="error">
              <MessageBarBody>{error}</MessageBarBody>
            </MessageBar>
          )}
          {tab === 'current' && (
            <>
              {!rows && !error && <Spinner label={t('record.share.loading')} />}
              {rows && rows.length === 0 && <p>{t('record.share.none')}</p>}
              {rows && rows.length > 0 && (
                <Table aria-label={t('record.share.current')} size="small" className={styles.table}>
                  <TableHeader>
                    <TableRow>
                      <TableHeaderCell>{t('record.share.principal')}</TableHeaderCell>
                      {SHARE_RIGHTS.map((right) => (
                        <TableHeaderCell key={right} className={styles.rightHeader}>
                          {rightLabel(right)}
                        </TableHeaderCell>
                      ))}
                      <TableHeaderCell className={styles.rightHeader} aria-label={t('record.share.remove')} />
                    </TableRow>
                  </TableHeader>
                  <TableBody>
                    {rows.map((row) => {
                      const checked = checkedOf(row);
                      const changed = changedRows.includes(row);
                      const none = mask(checked, row.rights).length === 0;
                      const name = displayName(row);
                      return (
                        <TableRow key={keyOf(row.principal)} className={changed ? styles.changed : undefined}>
                          <TableCell>
                            <TableCellLayout media={<PrincipalIcon type={row.principal.type} />} description={t(row.principal.type === 'team' ? 'record.principal.team' : 'record.principal.user')}>
                              <Tooltip content={row.principal.id} relationship="description">
                                <span className={mergeClasses(!row.name && styles.unknown, none && styles.removed)}>{name}</span>
                              </Tooltip>
                            </TableCellLayout>
                          </TableCell>
                          {SHARE_RIGHTS.map((right) => (
                            <TableCell key={right} className={styles.rightCell}>
                              <Checkbox
                                aria-label={t('record.share.rightOf', { right: rightLabel(right), name })}
                                checked={checked.includes(right)}
                                disabled={busy}
                                onChange={(_, d) => toggle(row, right, !!d.checked)}
                              />
                            </TableCell>
                          ))}
                          <TableCell className={styles.rightCell}>
                            <Tooltip content={t('record.share.removeOf', { name })} relationship="label">
                              <Button appearance="subtle" icon={<DeleteRegular />} disabled={busy} onClick={() => remove(row)} />
                            </Tooltip>
                          </TableCell>
                        </TableRow>
                      );
                    })}
                  </TableBody>
                </Table>
              )}
              {changedRows.some((r) => mask(checkedOf(r), r.rights).length === 0) && (
                <MessageBar intent="warning">
                  <MessageBarBody>{t('record.share.noRightsRemoves')}</MessageBarBody>
                </MessageBar>
              )}
            </>
          )}
          {tab === 'add' && (
            <>
              <Field label={t('record.share.addLabel')}>
                <Select value={pickType} onChange={(_, d) => setPickType(d.value as PrincipalType)}>
                  <option value="systemuser">{t('record.principal.user')}</option>
                  <option value="team">{t('record.principal.team')}</option>
                </Select>
              </Field>
              <EntityPicker
                key={pickType}
                layout="list"
                entitySet={SOURCES[pickType].set}
                idAttribute={SOURCES[pickType].id}
                nameAttribute={SOURCES[pickType].name}
                label={t(pickType === 'team' ? 'record.principal.searchTeam' : 'record.principal.searchUser')}
                onSelect={addCandidate}
              />
              <Field label={t('record.share.selected', { count: candidates.length })}>
                {candidates.length === 0 ? (
                  <p>{t('record.share.noneSelected')}</p>
                ) : (
                  <div className={styles.chips}>
                    {candidates.map((c) => (
                      <span key={keyOf(c)} className={styles.chip}>
                        <PrincipalIcon type={c.type} />
                        {c.name}
                        {shared.has(keyOf(c)) && ` · ${t('record.share.alreadyShared')}`}
                        <Button
                          appearance="transparent"
                          size="small"
                          icon={<DismissRegular />}
                          aria-label={t('record.share.unselect', { name: c.name })}
                          onClick={() => setCandidates((list) => list.filter((x) => keyOf(x) !== keyOf(c)))}
                        />
                      </span>
                    ))}
                  </div>
                )}
              </Field>
              <div role="group" aria-label={t('record.share.rights')}>
                <Label weight="semibold">{t('record.share.rights')}</Label>
                <div className={styles.rights}>
                  {SHARE_RIGHTS.map((right) => (
                    <Checkbox
                      key={right}
                      label={rightLabel(right)}
                      checked={rights.includes(right)}
                      onChange={(_, d) => setRights((r) => (d.checked ? [...r, right] : r.filter((x) => x !== right)))}
                    />
                  ))}
                </div>
              </div>
            </>
          )}
        </div>
        <div className={styles.footer}>
          {tab === 'current' ? (
            <>
              <Button disabled={busy || changedRows.length === 0} onClick={() => setEdits({})}>
                {t('record.share.discard')}
              </Button>
              <Button appearance="primary" disabled={busy || changedRows.length === 0} onClick={() => void saveChanges()}>
                {t('record.share.save', { count: changedRows.length })}
              </Button>
            </>
          ) : (
            <Button appearance="primary" disabled={busy || candidates.length === 0 || rights.length === 0} onClick={() => void share()}>
              {t('record.cmd.share')}
            </Button>
          )}
          <Button appearance="secondary" onClick={onClose}>
            {t('common.close')}
          </Button>
        </div>
      </DialogSurface>
    </Dialog>
  );
}
