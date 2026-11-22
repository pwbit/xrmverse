import {
  Button,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  MessageBar,
  MessageBarBody,
  Spinner,
  Table,
  TableBody,
  TableCell,
  TableHeader,
  TableHeaderCell,
  TableRow,
} from '@fluentui/react-components';
import { Link } from 'react-router-dom';
import { useState } from 'react';
import { RowCommand, SelectCell, SelectHeaderCell, selectableRow, useRowSelection } from '../common/rowSelection';
import { useNames } from '../common/useNames';
import { usePagedList } from '../common/usePagedList';
import { t } from '../../shell/i18n/t';
import { CreateTeamDialog } from './CreateTeamDialog';
import { LinkPanel } from './LinkPanel';
import { PageHeader } from '../../shell/layout/PageHeader';

type Team = { teamid: string; name?: string | null; teamtype?: number | null; _businessunitid_value?: string | null };
type Panel = { team: Team; kind: 'members' | 'roles' };

/** Only owner teams (teamtype 0, or unset) can hold roles; the server enforces it too. */
const isOwnerTeam = (team: Team) => !team.teamtype;

export function TeamsPage() {
  const list = usePagedList<Team>('teams?$select=teamid,name,teamtype,_businessunitid_value&$orderby=name');
  const names = useNames('businessunits', 'businessunitid', 'name', list.items.map((x) => x._businessunitid_value));
  const [creating, setCreating] = useState(false);
  const [panel, setPanel] = useState<Panel>();
  const selection = useRowSelection(list.items, (x) => x.teamid);
  const sel = selection.selected;
  const selName = sel?.name ?? '';

  return (
    <section>
      <PageHeader
        title={t('nav.teams')}
        actions={
          <>
            <Button appearance="primary" onClick={() => setCreating(true)}>
              {t('teams.create.open')}
            </Button>
            {sel && (
              <>
                <RowCommand aria-label={t('teams.members.open', { name: selName })} onClick={() => setPanel({ team: sel, kind: 'members' })}>
                  {t('teams.members.button')}
                </RowCommand>
                {isOwnerTeam(sel) && (
                  <RowCommand aria-label={t('teams.roles.open', { name: selName })} onClick={() => setPanel({ team: sel, kind: 'roles' })}>
                    {t('teams.roles.button')}
                  </RowCommand>
                )}
              </>
            )}
          </>
        }
      />
      {list.error && (
        <MessageBar intent="error">
          <MessageBarBody>{t('teams.loadFailed', { detail: list.error })}</MessageBarBody>
        </MessageBar>
      )}
      {!list.loading && !list.error && list.items.length === 0 && <p>{t('teams.empty')}</p>}
      {list.items.length > 0 && (
        <Table aria-label={t('nav.teams')}>
          <TableHeader>
            <TableRow>
              <SelectHeaderCell />
              <TableHeaderCell>{t('teams.col.name')}</TableHeaderCell>
              <TableHeaderCell>{t('teams.col.type')}</TableHeaderCell>
              <TableHeaderCell>{t('teams.col.businessUnit')}</TableHeaderCell>
            </TableRow>
          </TableHeader>
          <TableBody>
            {list.items.map((team) => (
              <TableRow key={team.teamid} {...selectableRow(selection, team)}>
                <SelectCell selection={selection} item={team} name={team.name ?? ''} />
                <TableCell>
                  <Link to={`/manage/tables/team/records/${team.teamid}`}>{team.name || team.teamid}</Link>
                </TableCell>
                <TableCell>{isOwnerTeam(team) ? t('teams.type.owner') : t('teams.type.access')}</TableCell>
                <TableCell>
                  {team._businessunitid_value ? (names[team._businessunitid_value] ?? team._businessunitid_value) : ''}
                </TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      )}
      {list.loading && <Spinner label={t('common.loading')} />}
      {list.nextLink && !list.loading && <Button onClick={list.loadMore}>{t('common.loadMore')}</Button>}

      {creating && (
        <CreateTeamDialog
          onClose={() => setCreating(false)}
          onCreated={() => {
            setCreating(false);
            list.reload();
          }}
        />
      )}
      <Dialog open={!!panel} onOpenChange={(_, d) => !d.open && setPanel(undefined)}>
        <DialogSurface>
          <DialogBody>
            <DialogTitle>
              {panel && t(panel.kind === 'members' ? 'teams.members.title' : 'teams.roles.title', { name: panel.team.name ?? '' })}
            </DialogTitle>
            <DialogContent>
              {panel?.kind === 'members' && (
                <LinkPanel
                  key={`m-${panel.team.teamid}`}
                  scope="teamMembers"
                  ownerPath={`teams(${panel.team.teamid})`}
                  ownerId={panel.team.teamid}
                  nav="teammembership_association"
                  linkSet="teammemberships"
                  ownerAttribute="teamid"
                  targetSet="systemusers"
                  targetIdAttribute="systemuserid"
                  targetNameAttribute="fullname"
                />
              )}
              {panel?.kind === 'roles' && (
                <LinkPanel
                  key={`r-${panel.team.teamid}`}
                  scope="teamRoles"
                  ownerPath={`teams(${panel.team.teamid})`}
                  ownerId={panel.team.teamid}
                  nav="teamroles_association"
                  linkSet="teamrolescollection"
                  ownerAttribute="teamid"
                  targetSet="roles"
                  targetIdAttribute="roleid"
                  targetNameAttribute="name"
                  pickerFilter={
                    panel.team._businessunitid_value ? `_businessunitid_value eq ${panel.team._businessunitid_value}` : undefined
                  }
                />
              )}
            </DialogContent>
            <DialogActions>
              <Button appearance="secondary" onClick={() => setPanel(undefined)}>
                {t('common.close')}
              </Button>
            </DialogActions>
          </DialogBody>
        </DialogSurface>
      </Dialog>
    </section>
  );
}
