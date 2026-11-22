import { Button, MessageBar, MessageBarBody, Spinner, Switch, Tab, TabList, Table, TableBody, TableCell, TableHeader, TableHeaderCell, TableRow, Tooltip } from '@fluentui/react-components';
import { useState } from 'react';
import { useParams } from 'react-router-dom';
import { useAuth } from '../../shell/auth/AuthProvider';
import { ConfirmDialog } from '../common/ConfirmDialog';
import { t } from '../../shell/i18n/t';
import { useNotify } from '../../shell/notify/Notifications';
import { setColumnAuditing, setTableAuditing } from '../audit/auditApi';
import { setAutoCreateAccessTeams } from '../security/accessTeamTemplatesApi';
import { setColumnSecured } from '../security/fieldSecurityApi';
import { FormsTab } from '../forms/FormsTab';
import { ViewsTab } from '../views/ViewsTab';
import { AddColumnDialog } from './AddColumnDialog';
import { ChoiceColorsDialog } from './ChoiceColorsDialog';
import type { AttributeMeta, AuditSetting } from './metadata';
import { forgetMostUsed } from './mostUsed';
import { RecordList } from './RecordList';
import { enableChangeTracking, enableTableSetting, loadEntity, loadRelationships, lookupsOf, setDuplicateDetection } from './tableApi';
import { useLoad } from './useLoad';
import { PageHeader } from '../../shell/layout/PageHeader';
import { NewIcon } from '../../shell/icons';

type TabId = 'columns' | 'relationships' | 'views' | 'forms' | 'records';

/** The one-way settings of plan 2.11 (and plan 4.1a's queues), with their catalogue keys. */
const ACTIVITY_SETTINGS = {
  HasNotes: {
    label: 'table.notes.enable',
    alreadyOn: 'table.notes.alreadyOn',
    confirmTitle: 'table.notes.confirmTitle',
    confirmBody: 'table.notes.confirmBody',
  },
  HasActivities: {
    label: 'table.activities.enable',
    alreadyOn: 'table.activities.alreadyOn',
    confirmTitle: 'table.activities.confirmTitle',
    confirmBody: 'table.activities.confirmBody',
  },
  IsValidForQueue: {
    label: 'table.queues.enable',
    alreadyOn: 'table.queues.alreadyOn',
    confirmTitle: 'table.queues.confirmTitle',
    confirmBody: 'table.queues.confirmBody',
  },
} as const;
type ActivitySetting = keyof typeof ACTIVITY_SETTINGS;

/** The tables that cannot be enabled for queues (plan 4.1a, the store's rule besides intersect tables). */
const QUEUE_REFUSED = new Set(['queue', 'queueitem', 'queuemembership', 'activitypointer', 'activityparty', 'annotation']);

/** A column whose values can be coloured: a choice, choices or status column (`UpdateOptionValue` does not take a state's). */
const hasColours = (a: AttributeMeta) => (a.kind === 'choice' || a.kind === 'choices') && a.typeName !== 'State';

/** One table: its columns (with "Add column"), its relationships and its records. */
export function TableDetail() {
  const { table = '' } = useParams();
  const meta = useLoad(table, (api) => loadEntity(api, table));
  const rels = useLoad(table, (api) => loadRelationships(api));
  const [tab, setTab] = useState<TabId>('columns');
  const [adding, setAdding] = useState(false);
  const [colouring, setColouring] = useState<AttributeMeta>();
  const [confirmTracking, setConfirmTracking] = useState(false);
  // The setting stays while the dialog closes, so its exit transition keeps its title and body (`open` alone goes false).
  const [confirmSetting, setConfirmSetting] = useState<{ setting: ActivitySetting; open: boolean }>();
  const closeConfirmSetting = () => setConfirmSetting((c) => c && { ...c, open: false });
  const [settingBusy, setSettingBusy] = useState(false);
  const { api } = useAuth();
  const notify = useNotify();
  const entity = meta.data;

  /** Writes one switch (auditing, field security), then reads the table again so the switches show what the server stored. */
  async function writeSetting(write: () => Promise<void>) {
    setSettingBusy(true);
    try {
      await write();
      meta.reload(true);
    } catch (e) {
      notify.error(e);
    } finally {
      setSettingBusy(false);
    }
  }

  /** A switch the server may have fixed (`CanBeChanged` false): disabled, with the reason on hover. */
  const auditSwitch = (setting: AuditSetting, label: string, id: string, write: (enabled: boolean) => Promise<void>) => (
    <Tooltip content={t('audit.locked')} relationship="description" visible={setting.locked ? undefined : false}>
      <span data-testid={`audit-lock-${id}`}>
        <Switch
          aria-label={label}
          checked={setting.enabled}
          disabled={setting.locked || settingBusy || !api}
          onChange={(_, d) => void writeSetting(() => write(d.checked))}
        />
      </span>
    </Tooltip>
  );

  /**
   * A one-way switch (notes, activities, queues): on, it stays checked and disabled; off, it asks for confirmation first. Shown only
   * when the server reported the setting.
   */
  const activitySwitch = (setting: ActivitySetting, on: boolean | undefined) => {
    if (on === undefined) return null;
    const keys = ACTIVITY_SETTINGS[setting];
    return (
      <div>
        <Switch
          aria-label={t(keys.label)}
          checked={on}
          disabled={on || settingBusy || !api}
          onChange={(_, d) => d.checked && setConfirmSetting({ setting, open: true })}
        />
        <span>{t(keys.label)}</span>
        {on && <span>{t(keys.alreadyOn)}</span>}
      </div>
    );
  };

  const lookups = rels.data ? lookupsOf(rels.data, table) : [];
  const referencedBy = rels.data?.oneToMany.filter((r) => r.referencedEntity === table) ?? [];
  const manyToMany = rels.data?.manyToMany.filter((r) => r.entity1 === table || r.entity2 === table) ?? [];

  return (
    <section>
      <PageHeader
        title={entity ? `${entity.displayName} (${entity.logicalName})` : table}
        breadcrumb={[{ label: t('nav.tables'), to: '/manage/tables' }]}
      />
      {(meta.error || rels.error) && (
        <MessageBar intent="error">
          <MessageBarBody>{t('table.loadFailed', { detail: meta.error ?? rels.error ?? '' })}</MessageBarBody>
        </MessageBar>
      )}
      {meta.loading && <Spinner label={t('common.loading')} />}
      {entity && (
        <>
          <TabList selectedValue={tab} onTabSelect={(_, d) => setTab(d.value as TabId)}>
            <Tab value="columns">{t('table.tab.columns')}</Tab>
            <Tab value="relationships">{t('table.tab.relationships')}</Tab>
            <Tab value="views">{t('table.tab.views')}</Tab>
            <Tab value="forms">{t('table.tab.forms')}</Tab>
            <Tab value="records">{t('table.tab.records')}</Tab>
          </TabList>
          {tab === 'columns' && (
            <div>
              {entity.audit && (
                <div>
                  {auditSwitch(entity.audit, t('audit.table.switch'), entity.logicalName, (on) => setTableAuditing(api!, table, on))}
                  <span>{t('audit.table.switch')}</span>
                </div>
              )}
              {entity.autoCreateAccessTeams !== undefined && (
                <div>
                  <Switch
                    aria-label={t('accessTeams.enable')}
                    checked={entity.autoCreateAccessTeams}
                    disabled={settingBusy || !api}
                    onChange={(_, d) => void writeSetting(() => setAutoCreateAccessTeams(api!, table, d.checked))}
                  />
                  <span>{t('accessTeams.enable')}</span>
                </div>
              )}
              {entity.changeTracking && (
                <div>
                  <Switch
                    aria-label={t('changeTracking.enable')}
                    checked={entity.changeTracking.enabled}
                    disabled={entity.changeTracking.enabled || !entity.changeTracking.canEnable || settingBusy || !api}
                    onChange={(_, d) => d.checked && setConfirmTracking(true)}
                  />
                  <span>{t('changeTracking.enable')}</span>
                  {entity.changeTracking.enabled ? (
                    <span>{t('changeTracking.alreadyOn')}</span>
                  ) : (
                    !entity.changeTracking.canEnable && <span>{t('changeTracking.notEligible')}</span>
                  )}
                </div>
              )}
              {entity.isActivity && <p>{t('table.isActivity')}</p>}
              {activitySwitch('HasNotes', entity.hasNotes)}
              {/* An activity table cannot have activities (plan 2.11 A4): the switch is not offered. */}
              {!entity.isActivity && activitySwitch('HasActivities', entity.hasActivities)}
              {/* Plan 4.1a: not offered on the tables the server refuses (the queue tables, the pointer, parties, notes; an intersect table is refused by the server). */}
              {!QUEUE_REFUSED.has(table) && activitySwitch('IsValidForQueue', entity.isValidForQueue)}
              <ConfirmDialog
                open={confirmSetting?.open ?? false}
                title={confirmSetting ? t(ACTIVITY_SETTINGS[confirmSetting.setting].confirmTitle) : ''}
                body={confirmSetting ? t(ACTIVITY_SETTINGS[confirmSetting.setting].confirmBody) : ''}
                confirmLabel={t('table.flag.confirmButton')}
                cancelLabel={t('common.cancel')}
                onConfirm={() => {
                  const setting = confirmSetting!.setting;
                  closeConfirmSetting();
                  void writeSetting(() => enableTableSetting(api!, table, setting));
                }}
                onCancel={closeConfirmSetting}
              />
              {entity.duplicateDetection && (
                <div>
                  <Switch
                    aria-label={t('duplicates.table.switch')}
                    checked={entity.duplicateDetection.enabled}
                    disabled={entity.duplicateDetection.locked || settingBusy || !api}
                    onChange={(_, d) => void writeSetting(() => setDuplicateDetection(api!, table, d.checked))}
                  />
                  <span>{t('duplicates.table.switch')}</span>
                  <span>{t('duplicates.table.hint')}</span>
                </div>
              )}
              <ConfirmDialog
                open={confirmTracking}
                title={t('changeTracking.confirmTitle')}
                body={t('changeTracking.confirmBody')}
                confirmLabel={t('changeTracking.confirmButton')}
                cancelLabel={t('common.cancel')}
                onConfirm={() => {
                  setConfirmTracking(false);
                  void writeSetting(() => enableChangeTracking(api!, table));
                }}
                onCancel={() => setConfirmTracking(false)}
              />
              <Button appearance="primary" icon={<NewIcon />} onClick={() => setAdding(true)}>
                {t('column.add.open')}
              </Button>
              <Table aria-label={t('table.tab.columns')}>
                <TableHeader>
                  <TableRow>
                    <TableHeaderCell>{t('table.col.name')}</TableHeaderCell>
                    <TableHeaderCell>{t('table.col.label')}</TableHeaderCell>
                    <TableHeaderCell>{t('table.col.type')}</TableHeaderCell>
                    <TableHeaderCell>{t('table.col.required')}</TableHeaderCell>
                    <TableHeaderCell>{t('audit.col.audit')}</TableHeaderCell>
                    <TableHeaderCell>{t('fieldSecurity.secured.col')}</TableHeaderCell>
                    <TableHeaderCell>{t('common.col.actions')}</TableHeaderCell>
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {entity.attributes.map((a) => (
                    <TableRow key={a.logicalName}>
                      <TableCell>{a.logicalName}</TableCell>
                      <TableCell>{a.displayName}</TableCell>
                      <TableCell>{a.typeName}</TableCell>
                      <TableCell>{a.requiredLevel}</TableCell>
                      <TableCell>
                        {a.audit &&
                          auditSwitch(a.audit, t('audit.column.switch', { column: a.logicalName }), a.logicalName, (on) =>
                            setColumnAuditing(api!, table, a.logicalName, on),
                          )}
                      </TableCell>
                      <TableCell>
                        <Tooltip content={t('fieldSecurity.secured.notSecurable')} relationship="description" visible={a.canBeSecured === false ? undefined : false}>
                          <span data-testid={`secured-${a.logicalName}`}>
                            <Switch
                              aria-label={t('fieldSecurity.secured.switch', { column: a.logicalName })}
                              checked={a.secured}
                              disabled={a.canBeSecured !== true || settingBusy || !api}
                              onChange={(_, d) => void writeSetting(() => setColumnSecured(api!, table, a.logicalName, d.checked))}
                            />
                          </span>
                        </Tooltip>
                      </TableCell>
                      <TableCell>
                        {hasColours(a) && (
                          <Button size="small" aria-label={t('choiceColors.openFor', { column: a.logicalName })} onClick={() => setColouring(a)}>
                            {t('choiceColors.open')}
                          </Button>
                        )}
                      </TableCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
            </div>
          )}
          {tab === 'relationships' && (
            <div>
              {rels.loading && <Spinner label={t('common.loading')} />}
              {rels.data && lookups.length + referencedBy.length + manyToMany.length === 0 && <p>{t('table.rel.empty')}</p>}
              {rels.data && lookups.length + referencedBy.length + manyToMany.length > 0 && (
                <Table aria-label={t('table.tab.relationships')}>
                  <TableHeader>
                    <TableRow>
                      <TableHeaderCell>{t('table.rel.name')}</TableHeaderCell>
                      <TableHeaderCell>{t('table.rel.kind')}</TableHeaderCell>
                      <TableHeaderCell>{t('table.rel.detail')}</TableHeaderCell>
                      <TableHeaderCell>{t('table.rel.nav')}</TableHeaderCell>
                    </TableRow>
                  </TableHeader>
                  <TableBody>
                    {lookups.map((r) => (
                      <TableRow key={`l-${r.schemaName}`}>
                        <TableCell>{r.schemaName}</TableCell>
                        <TableCell>{t('table.rel.kind.lookup')}</TableCell>
                        <TableCell>{t('table.rel.lookupDetail', { column: r.referencingAttribute, target: r.referencedEntity })}</TableCell>
                        <TableCell>{r.nav ?? ''}</TableCell>
                      </TableRow>
                    ))}
                    {referencedBy.map((r) => (
                      <TableRow key={`r-${r.schemaName}`}>
                        <TableCell>{r.schemaName}</TableCell>
                        <TableCell>{t('table.rel.kind.referencedBy')}</TableCell>
                        <TableCell>{t('table.rel.referencedByDetail', { table: r.referencingEntity, column: r.referencingAttribute })}</TableCell>
                        <TableCell />
                      </TableRow>
                    ))}
                    {manyToMany.map((r) => (
                      <TableRow key={`m-${r.schemaName}`}>
                        <TableCell>{r.schemaName}</TableCell>
                        <TableCell>{t('table.rel.kind.manyToMany')}</TableCell>
                        <TableCell>{r.entity1 === table ? r.entity2 : r.entity1}</TableCell>
                        <TableCell />
                      </TableRow>
                    ))}
                  </TableBody>
                </Table>
              )}
            </div>
          )}
          {tab === 'views' && <ViewsTab table={table} />}
          {tab === 'forms' && <FormsTab table={table} />}
          {tab === 'records' && <RecordList entity={entity} />}
        </>
      )}
      {colouring && (
        <ChoiceColorsDialog
          table={table}
          column={colouring}
          onClose={() => setColouring(undefined)}
          onSaved={() => {
            setColouring(undefined);
            meta.reload(true);
          }}
        />
      )}
      {adding && (
        <AddColumnDialog
          table={table}
          onClose={() => setAdding(false)}
          onAdded={() => {
            setAdding(false);
            forgetMostUsed(table);
            meta.reload();
            rels.reload();
          }}
        />
      )}
    </section>
  );
}
