import { Button, MessageBar, MessageBarBody, MessageBarActions, Table, TableBody, TableCell, TableHeader, TableHeaderCell, TableRow } from '@fluentui/react-components';
import { Fragment, useCallback, useEffect, useRef, useState } from 'react';
import { useAuth } from '../../shell/auth/AuthProvider';
import { t } from '../../shell/i18n/t';
import { useNotify } from '../../shell/notify/Notifications';
import { addPrivilege, DEPTHS, highestDepth, parsePrivilegeName, removePrivilege, type DepthName, type Privilege } from './privilegeModel';

export interface RoleRef {
  roleid: string;
  name?: string | null;
  _businessunitid_value?: string | null;
}

/**
 * Rows of privileges grouped by table (one row per privilege, one radio per depth, the first one "None") for one role. The depth held is read from
 * `roleprivilegescollection` for exactly the privileges shown; changes go to `AddPrivilegesRole` / `RemovePrivilegeRole` and
 * the held depth is read back from the server afterwards, so a refused call leaves the matrix as it was.
 */
export function PrivilegeMatrix({ role, privileges }: { role: RoleRef; privileges: Privilege[] }) {
  const { api } = useAuth();
  const notify = useNotify();
  const [masks, setMasks] = useState<Record<string, number>>({});
  const [busy, setBusy] = useState<string>();
  const [maskError, setMaskError] = useState<string>();
  const requested = useRef(new Set<string>());

  const loadMasks = useCallback(
    async (ids: string[]) => {
      if (!api || ids.length === 0) return;
      const ors = ids.map((id) => `privilegeid eq ${id}`).join(' or ');
      try {
        const page = await api.page<{ privilegeid: string; privilegedepthmask?: number | null }>(
          `roleprivilegescollection?$select=privilegeid,privilegedepthmask&$filter=${encodeURIComponent(`roleid eq ${role.roleid} and (${ors})`)}`,
        );
        setMaskError(undefined);
        setMasks((prev) => {
          const next = { ...prev };
          for (const id of ids) next[id] = 0;
          for (const row of page.value) next[row.privilegeid] = row.privilegedepthmask ?? 0;
          return next;
        });
      } catch (e) {
        ids.forEach((id) => requested.current.delete(id));
        setMaskError(e instanceof Error ? e.message : String(e));
        notify.error(e);
      }
    },
    [api, role.roleid, notify],
  );

  // The ids, not the array: TableMode builds a new `privileges` array on every render, and a read effect keyed on its identity
  // refetches by itself right after a failure (the failure forgets the ids), taking the Retry button away.
  const privilegesRef = useRef(privileges);
  privilegesRef.current = privileges;
  const idsKey = privileges.map((p) => p.privilegeid).join(',');

  const readMissing = useCallback(() => {
    const fresh = privilegesRef.current.map((p) => p.privilegeid).filter((id) => !requested.current.has(id));
    fresh.forEach((id) => requested.current.add(id));
    void loadMasks(fresh);
  }, [loadMasks]);

  useEffect(() => {
    readMissing();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [idsKey, api, role.roleid]);

  async function change(privilege: Privilege, depth: DepthName | undefined) {
    if (!api) return;
    setBusy(privilege.privilegeid);
    try {
      if (depth) await addPrivilege(api, role.roleid, privilege.privilegeid, depth, role._businessunitid_value);
      else await removePrivilege(api, role.roleid, privilege.privilegeid);
      notify.success(t(depth ? 'privileges.set' : 'privileges.removed', { privilege: privilege.name, depth: depth ?? '' }));
      await loadMasks([privilege.privilegeid]);
    } catch (e) {
      notify.error(e);
    } finally {
      setBusy(undefined);
    }
  }

  const groups: { table: string; rows: Privilege[] }[] = [];
  for (const p of privileges) {
    const table = parsePrivilegeName(p.name).table || t('privileges.group.other');
    const group = groups.find((g) => g.table === table);
    if (group) group.rows.push(p);
    else groups.push({ table, rows: [p] });
  }

  return (
    <div>
      {maskError && (
        <MessageBar intent="error">
          <MessageBarBody>{t('privileges.loadFailed', { detail: maskError })}</MessageBarBody>
          <MessageBarActions>
            <Button onClick={readMissing}>{t('common.retry')}</Button>
          </MessageBarActions>
        </MessageBar>
      )}
      {privileges.length > 0 && (
        <Table aria-label={t('privileges.title', { role: role.name ?? '' })}>
          <TableHeader>
            <TableRow>
              <TableHeaderCell>{t('privileges.col.privilege')}</TableHeaderCell>
              <TableHeaderCell>{t('privileges.depth.None')}</TableHeaderCell>
              {DEPTHS.map((d) => (
                <TableHeaderCell key={d.name}>{t(`privileges.depth.${d.name}`)}</TableHeaderCell>
              ))}
            </TableRow>
          </TableHeader>
          <TableBody>
            {groups.map((g) => (
              <Fragment key={g.table}>
                <TableRow>
                  <TableCell>
                    <strong>{g.table}</strong>
                  </TableCell>
                </TableRow>
                {g.rows.map((p) => {
                  const current = highestDepth(masks[p.privilegeid] ?? 0);
                  return (
                    <TableRow key={p.privilegeid}>
                      <TableCell>{p.name}</TableCell>
                      <TableCell>
                        {/* "None": the role does not hold the privilege; choosing it removes the privilege. */}
                        <input
                          type="radio"
                          name={`depth-${p.privilegeid}`}
                          aria-label={t('privileges.depthLabel', { privilege: p.name, depth: t('privileges.depth.None') })}
                          checked={p.privilegeid in masks && !current}
                          disabled={busy === p.privilegeid || !(p.privilegeid in masks)}
                          onChange={() => void change(p, undefined)}
                        />
                      </TableCell>
                      {DEPTHS.map((d) => (
                        <TableCell key={d.name}>
                          <input
                            type="radio"
                            name={`depth-${p.privilegeid}`}
                            aria-label={t('privileges.depthLabel', { privilege: p.name, depth: d.name })}
                            checked={current === d.name}
                            disabled={!p[d.canBe] || busy === p.privilegeid || !(p.privilegeid in masks)}
                            onChange={() => void change(p, d.name)}
                          />
                        </TableCell>
                      ))}
                    </TableRow>
                  );
                })}
              </Fragment>
            ))}
          </TableBody>
        </Table>
      )}
    </div>
  );
}
