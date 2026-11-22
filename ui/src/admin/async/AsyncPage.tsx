import {
  Button,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  Field,
  MessageBar,
  MessageBarBody,
  OverlayDrawer,
  DrawerHeader,
  DrawerHeaderTitle,
  DrawerBody,
  Select,
  Spinner,
  Table,
  TableBody,
  TableCell,
  TableHeader,
  TableHeaderCell,
  TableRow,
} from '@fluentui/react-components';
import { useState } from 'react';
import { useAuth } from '../../shell/auth/AuthProvider';
import { t } from '../../shell/i18n/t';
import { useNotify } from '../../shell/notify/Notifications';
import { RowCommand, SelectCell, SelectHeaderCell, selectableRow, useRowSelection } from '../common/rowSelection';
import { usePagedList } from '../common/usePagedList';
import { PageHeader } from '../../shell/layout/PageHeader';

const FV = 'OData.Community.Display.V1.FormattedValue';
const PREFER_FORMATTED = { Prefer: `odata.include-annotations="${FV}"` };
const SELECT =
  'asyncoperationid,name,operationtype,statecode,statuscode,createdon,completedon,message,friendlymessage';
const STATES = [0, 1, 2, 3] as const;
/** Ready (0) and Suspended (1) are the states the server lets a job be cancelled from. */
const isWaiting = (state: number) => state === 0 || state === 1;

type Job = {
  asyncoperationid: string;
  name: string;
  operationtype: number;
  statecode: number;
  statuscode: number;
  createdon?: string | null;
  completedon?: string | null;
  message?: string | null;
  friendlymessage?: string | null;
} & Record<string, unknown>;

const formatted = (job: Job, attribute: string): string => {
  const value = job[`${attribute}@${FV}`];
  return typeof value === 'string' ? value : String(job[attribute] ?? '');
};
const when = (iso?: string | null) => (iso ? new Date(iso).toLocaleString() : '');

export function AsyncPage() {
  const { api } = useAuth();
  const notify = useNotify();
  const [state, setState] = useState('');
  /** The job whose details the drawer shows. */
  const [detail, setDetail] = useState<Job>();
  const [confirming, setConfirming] = useState<Job>();
  const filter = state === '' ? '' : `&$filter=${encodeURIComponent(`statecode eq ${state}`)}`;
  const {
    items: jobs,
    setItems: setJobs,
    nextLink,
    loading,
    error,
    loadMore,
  } = usePagedList<Job>(`asyncoperations?$select=${SELECT}&$orderby=createdon desc${filter}`, 50, { headers: PREFER_FORMATTED });
  const selection = useRowSelection(jobs, (j) => j.asyncoperationid);
  const sel = selection.selected;

  async function cancel(job: Job) {
    if (!api) return;
    try {
      await api.patch(`asyncoperations(${job.asyncoperationid})`, { statecode: 3, statuscode: 32 });
      notify.success(t('async.cancelled'));
      const done = await api.get<Job>(`asyncoperations(${job.asyncoperationid})?$select=${SELECT}`, {
        headers: PREFER_FORMATTED,
      });
      setJobs((prev) => prev.map((j) => (j.asyncoperationid === job.asyncoperationid ? done : j)));
      setDetail((cur) => (cur?.asyncoperationid === job.asyncoperationid ? done : cur));
    } catch (e) {
      notify.error(e);
    } finally {
      setConfirming(undefined);
    }
  }

  /** The Cancel button of the details drawer (the command bar offers the same command for the selected row). */
  const cancelButton = (job: Job) =>
    isWaiting(job.statecode) ? (
      <Button size="small" onClick={() => setConfirming(job)}>
        {t('async.cancel')}
      </Button>
    ) : null;

  return (
    <section>
      <PageHeader
        title={t('async.title')}
        actions={
          sel &&
          isWaiting(sel.statecode) && (
            <RowCommand aria-label={t('async.cancelFor', { name: sel.name })} onClick={() => setConfirming(sel)}>
              {t('async.cancel')}
            </RowCommand>
          )
        }
      />
      <Field label={t('async.filter.state')}>
        <Select value={state} onChange={(_, d) => setState(d.value)}>
          <option value="">{t('async.filter.all')}</option>
          {STATES.map((s) => (
            <option key={s} value={String(s)}>
              {t(`async.state.${s}` as const)}
            </option>
          ))}
        </Select>
      </Field>
      {error && (
        <MessageBar intent="error">
          <MessageBarBody>{t('async.loadFailed', { detail: error })}</MessageBarBody>
        </MessageBar>
      )}
      {!loading && !error && jobs.length === 0 && <p>{t('async.empty')}</p>}
      {jobs.length > 0 && (
        <Table aria-label={t('async.title')}>
          <TableHeader>
            <TableRow>
              <SelectHeaderCell />
              <TableHeaderCell>{t('async.col.name')}</TableHeaderCell>
              <TableHeaderCell>{t('async.col.type')}</TableHeaderCell>
              <TableHeaderCell>{t('async.col.state')}</TableHeaderCell>
              <TableHeaderCell>{t('async.col.status')}</TableHeaderCell>
              <TableHeaderCell>{t('async.col.created')}</TableHeaderCell>
              <TableHeaderCell>{t('async.col.completed')}</TableHeaderCell>
            </TableRow>
          </TableHeader>
          <TableBody>
            {jobs.map((job) => (
              <TableRow key={job.asyncoperationid} {...selectableRow(selection, job)}>
                <SelectCell selection={selection} item={job} name={job.name} />
                <TableCell>
                  <Button appearance="transparent" onClick={() => setDetail(job)}>
                    {job.name}
                  </Button>
                </TableCell>
                <TableCell>{formatted(job, 'operationtype')}</TableCell>
                <TableCell>{formatted(job, 'statecode')}</TableCell>
                <TableCell>{formatted(job, 'statuscode')}</TableCell>
                <TableCell>{when(job.createdon)}</TableCell>
                <TableCell>{when(job.completedon)}</TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      )}
      {loading && <Spinner label={t('async.loading')} />}
      {nextLink && !loading && <Button onClick={loadMore}>{t('async.loadMore')}</Button>}
      <Dialog open={!!confirming} onOpenChange={(_, d) => !d.open && setConfirming(undefined)}>
        <DialogSurface>
          <DialogBody>
            <DialogTitle>{t('async.confirm.title')}</DialogTitle>
            <DialogContent>{confirming && t('async.confirm.body', { name: confirming.name })}</DialogContent>
            <DialogActions>
              <Button appearance="secondary" onClick={() => setConfirming(undefined)}>
                {t('async.confirm.no')}
              </Button>
              <Button appearance="primary" onClick={() => confirming && void cancel(confirming)}>
                {t('async.confirm.yes')}
              </Button>
            </DialogActions>
          </DialogBody>
        </DialogSurface>
      </Dialog>
      <OverlayDrawer position="end" open={!!detail} onOpenChange={(_, d) => !d.open && setDetail(undefined)}>
        {detail && (
          <>
            <DrawerHeader>
              <DrawerHeaderTitle
                action={<Button appearance="subtle" onClick={() => setDetail(undefined)}>{t('async.detail.close')}</Button>}
              >
                {detail.name}
              </DrawerHeaderTitle>
            </DrawerHeader>
            <DrawerBody>
              <h3>{t('async.detail.friendlyMessage')}</h3>
              <p>{detail.friendlymessage || t('async.detail.none')}</p>
              <h3>{t('async.detail.message')}</h3>
              <pre style={{ whiteSpace: 'pre-wrap' }}>{detail.message || t('async.detail.none')}</pre>
              {cancelButton(detail)}
            </DrawerBody>
          </>
        )}
      </OverlayDrawer>
    </section>
  );
}
