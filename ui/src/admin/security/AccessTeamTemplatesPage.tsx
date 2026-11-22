import {
  Button,
  Checkbox,
  Field,
  Input,
  makeStyles,
  mergeClasses,
  MessageBar,
  MessageBarBody,
  Select,
  Spinner,
  Switch,
  Table,
  TableBody,
  TableCell,
  TableHeader,
  TableHeaderCell,
  TableRow,
  Toolbar,
} from '@fluentui/react-components';
import { useCallback, useEffect, useState } from 'react';
import { useAuth } from '../../shell/auth/AuthProvider';
import { t } from '../../shell/i18n/t';
import { useNotify } from '../../shell/notify/Notifications';
import { ConfirmDialog } from '../common/ConfirmDialog';
import { RowCommand, SelectCell, SelectHeaderCell, selectableRow, useRowSelection } from '../common/rowSelection';
import { useSurfaceStyles } from '../../shell/layout/surface';
import {
  ACCESS_RIGHTS,
  createTemplate,
  deleteTemplate,
  loadTemplateTables,
  loadTemplates,
  maskOf,
  setAutoCreateAccessTeams,
  setTemplateMask,
  type AccessTeamTemplate,
  type RightKey,
  type TemplateTable,
} from './accessTeamTemplatesApi';
import { PageHeader } from '../../shell/layout/PageHeader';

const useStyles = makeStyles({ commands: { display: 'flex', flexWrap: 'wrap', gap: '8px', minHeight: '32px', marginTop: '8px' } });

const messageOf = (e: unknown) => (e instanceof Error ? e.message : String(e));
const rightLabel = (key: RightKey) => t(`accessTeams.right.${key}`);

/**
 * Access team templates: the rights an auto-created access team gets on a record, per table, and the table's
 * "Auto-create access teams" switch. The server judges every value (mask, table, limit of 4); its 400 is shown as it is.
 */
export function AccessTeamTemplatesPage() {
  const { api } = useAuth();
  const notify = useNotify();
  const [tables, setTables] = useState<TemplateTable[]>();
  const [templates, setTemplates] = useState<AccessTeamTemplate[]>();
  const [loadError, setLoadError] = useState<string>();
  const [error, setError] = useState<string>();
  const [busy, setBusy] = useState(false);
  const [table, setTable] = useState('');
  const [name, setName] = useState('');
  const [deleting, setDeleting] = useState<AccessTeamTemplate>();
  const [rights, setRights] = useState<Set<RightKey>>(new Set(['read']));
  const selection = useRowSelection(templates ?? [], (x) => x.id);
  const sel = selection.selected;
  const styles = useStyles();
  const surface = useSurfaceStyles();

  const reload = useCallback(
    async (live: () => boolean = () => true) => {
      if (!api) return;
      try {
        const [tableRows, templateRows] = await Promise.all([loadTemplateTables(api), loadTemplates(api)]);
        if (!live()) return;
        setTables(tableRows);
        setTemplates(templateRows);
      } catch (e) {
        if (live()) setLoadError(messageOf(e));
      }
    },
    [api],
  );

  useEffect(() => {
    let live = true;
    void reload(() => live);
    return () => {
      live = false;
    };
  }, [reload]);

  /** Runs one write, then reads everything again, so the page shows what the server stored. */
  async function write(action: () => Promise<void>, done?: string) {
    setBusy(true);
    setError(undefined);
    try {
      await action();
      if (done) notify.success(done);
      await reload();
    } catch (e) {
      setError(messageOf(e));
    } finally {
      setBusy(false);
    }
  }

  const selected = tables?.find((x) => x.logicalName === table);
  const tableOf = (code: number) => tables?.find((x) => x.objectTypeCode === code)?.logicalName ?? String(code);

  function toggleRight(key: RightKey, on: boolean) {
    const next = new Set(rights);
    if (on) next.add(key);
    else next.delete(key);
    setRights(next);
  }

  async function create() {
    if (!api || !selected) return;
    const templateName = name.trim();
    await write(async () => {
      await createTemplate(api, templateName, selected.objectTypeCode, maskOf(rights));
      setName('');
    }, t('accessTeams.created', { name: templateName }));
  }

  return (
    <section>
      <PageHeader title={t('nav.accessTeamTemplates')} />
      <p>{t('accessTeams.intro')}</p>
      {loadError && (
        <MessageBar intent="error">
          <MessageBarBody>{t('accessTeams.loadFailed', { detail: loadError })}</MessageBarBody>
        </MessageBar>
      )}
      {!(tables && templates) && !loadError && <Spinner label={t('accessTeams.loading')} />}
      {tables && templates && (
        <>
          <form
            onSubmit={(e) => {
              e.preventDefault();
              void create();
            }}
            style={{ display: 'flex', flexDirection: 'column', gap: 12, maxWidth: 480 }}
          >
            <Field label={t('accessTeams.table')}>
              <Select value={table} onChange={(_, d) => setTable(d.value)}>
                <option value="">{t('accessTeams.tablePlaceholder')}</option>
                {tables.map((x) => (
                  <option key={x.logicalName} value={x.logicalName}>
                    {x.logicalName}
                  </option>
                ))}
              </Select>
            </Field>
            {selected && (
              <Switch
                label={t('accessTeams.enable')}
                checked={selected.enabled}
                disabled={busy}
                onChange={(_, d) => void write(() => setAutoCreateAccessTeams(api!, selected.logicalName, d.checked))}
              />
            )}
            <Field label={t('accessTeams.name')}>
              <Input value={name} onChange={(_, d) => setName(d.value)} />
            </Field>
            <div role="group" aria-label={t('accessTeams.rights')} style={{ display: 'flex', flexWrap: 'wrap', gap: 8 }}>
              {ACCESS_RIGHTS.map((r) => (
                <Checkbox key={r.key} label={rightLabel(r.key)} checked={rights.has(r.key)} onChange={(_, d) => toggleRight(r.key, d.checked === true)} />
              ))}
            </div>
            <div>
              <Button type="submit" appearance="primary" disabled={busy || !selected?.enabled || !name.trim()}>
                {t('accessTeams.create')}
              </Button>
            </div>
          </form>
          {error && (
            <MessageBar intent="error">
              <MessageBarBody>{error}</MessageBarBody>
            </MessageBar>
          )}
          {templates.length === 0 ? (
            <p>{t('accessTeams.none')}</p>
          ) : (
            <>
              {/* The list's own command bar: the create form sits between the page's header and this list. */}
              <Toolbar aria-label={t('page.commands', { title: t('nav.accessTeamTemplates') })} className={mergeClasses(surface.commandBar, styles.commands)}>
                {sel && (
                  <RowCommand disabled={busy} aria-label={t('accessTeams.delete', { name: sel.name })} onClick={() => setDeleting(sel)}>
                    {t('accessTeams.deleteButton')}
                  </RowCommand>
                )}
              </Toolbar>
              <Table aria-label={t('nav.accessTeamTemplates')}>
                <TableHeader>
                  <TableRow>
                    <SelectHeaderCell />
                    <TableHeaderCell>{t('accessTeams.col.table')}</TableHeaderCell>
                    <TableHeaderCell>{t('accessTeams.col.name')}</TableHeaderCell>
                    <TableHeaderCell>{t('accessTeams.col.rights')}</TableHeaderCell>
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {templates.map((x) => (
                    <TableRow key={x.id} {...selectableRow(selection, x)}>
                      <SelectCell selection={selection} item={x} name={x.name} />
                      <TableCell>{tableOf(x.objectTypeCode)}</TableCell>
                      <TableCell>{x.name}</TableCell>
                      <TableCell>
                        {ACCESS_RIGHTS.map((r) => (
                          <Checkbox
                            key={r.key}
                            label={rightLabel(r.key)}
                            aria-label={t('accessTeams.rightFor', { right: rightLabel(r.key), name: x.name })}
                            checked={(x.mask & r.bit) !== 0}
                            disabled={busy}
                            onChange={(_, d) => void write(() => setTemplateMask(api!, x.id, d.checked === true ? x.mask | r.bit : x.mask & ~r.bit))}
                          />
                        ))}
                      </TableCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
            </>
          )}
        </>
      )}
      <ConfirmDialog
        open={!!deleting}
        title={t('accessTeams.confirmTitle')}
        body={deleting ? t('accessTeams.confirmBody', { name: deleting.name }) : ''}
        confirmLabel={t('accessTeams.deleteButton')}
        cancelLabel={t('common.cancel')}
        onConfirm={() => {
          const x = deleting;
          setDeleting(undefined);
          if (x) void write(() => deleteTemplate(api!, x.id), t('accessTeams.deleted', { name: x.name }));
        }}
        onCancel={() => setDeleting(undefined)}
      />
    </section>
  );
}
