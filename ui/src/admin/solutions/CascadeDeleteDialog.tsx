import {
  Button,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  Field,
  Input,
  MessageBar,
  MessageBarBody,
  Spinner,
} from '@fluentui/react-components';
import { useMemo, useState } from 'react';
import { useAuth } from '../../shell/auth/AuthProvider';
import { t } from '../../shell/i18n/t';
import { useNotify } from '../../shell/notify/Notifications';
import { usePagedList } from '../common/usePagedList';
import { componentTypeLabel } from './componentTypes';

export interface CascadeSolution {
  solutionid: string;
  uniquename: string;
}

/** `DELETE /admin/solutions/{name}?cascade=true`: `deleted` lists `{type,name}`, `kept` lists `{type,name,reason}`. */
interface CascadeResult {
  deleted: { type: number; name: string }[];
  kept: { type: number; name: string; reason: string }[];
}

/**
 * The cascade delete of a solution (System Administrator only; the Admin API refuses anyone else). The server has no
 * dry run, so the dialog lists the solution's own components (`solutioncomponents`) as what is at stake, says that those
 * another solution also holds are kept, and asks for the unique name to be typed. The result lists `deleted` and `kept`.
 */
export function CascadeDeleteDialog({ solution, onClose }: { solution: CascadeSolution; onClose: (deleted: boolean) => void }) {
  const { api } = useAuth();
  const notify = useNotify();
  const components = usePagedList<{ objectid: string; componenttype: number }>(
    `solutioncomponents?$select=objectid,componenttype&$filter=${encodeURIComponent(`_solutionid_value eq ${solution.solutionid}`)}`,
  );
  const [typed, setTyped] = useState('');
  const [busy, setBusy] = useState(false);
  const [result, setResult] = useState<CascadeResult>();

  const counts = useMemo(() => {
    const byType = new Map<number, number>();
    for (const c of components.items) byType.set(c.componenttype, (byType.get(c.componenttype) ?? 0) + 1);
    return [...byType.entries()].sort((a, b) => a[0] - b[0]);
  }, [components.items]);

  async function cascade() {
    if (!api) return;
    setBusy(true);
    try {
      setResult(await api.admin<CascadeResult>('DELETE', `solutions/${encodeURIComponent(solution.uniquename)}?cascade=true`));
    } catch (e) {
      notify.error(e);
    } finally {
      setBusy(false);
    }
  }

  if (result) {
    return (
      <Dialog open onOpenChange={(_, d) => !d.open && onClose(true)}>
        <DialogSurface>
          <DialogBody>
            <DialogTitle>{t('solutions.cascade.resultTitle', { name: solution.uniquename })}</DialogTitle>
            <DialogContent>
              <h4>{t('solutions.cascade.deleted', { count: result.deleted.length })}</h4>
              {result.deleted.length === 0 ? (
                <p>{t('solutions.cascade.none')}</p>
              ) : (
                <ul>
                  {result.deleted.map((d, i) => (
                    <li key={i}>{t('solutions.cascade.deletedItem', { name: d.name, type: componentTypeLabel(d.type) })}</li>
                  ))}
                </ul>
              )}
              <h4>{t('solutions.cascade.kept', { count: result.kept.length })}</h4>
              {result.kept.length === 0 ? (
                <p>{t('solutions.cascade.none')}</p>
              ) : (
                <ul>
                  {result.kept.map((k, i) => (
                    <li key={i}>{t('solutions.cascade.keptItem', { name: k.name, type: componentTypeLabel(k.type), reason: k.reason })}</li>
                  ))}
                </ul>
              )}
            </DialogContent>
            <DialogActions>
              <Button appearance="primary" onClick={() => onClose(true)}>
                {t('common.close')}
              </Button>
            </DialogActions>
          </DialogBody>
        </DialogSurface>
      </Dialog>
    );
  }

  return (
    <Dialog open onOpenChange={(_, d) => !d.open && !busy && onClose(false)}>
      <DialogSurface>
        <DialogBody>
          <DialogTitle>{t('solutions.cascade.title')}</DialogTitle>
          <DialogContent>
            <MessageBar intent="warning">
              <MessageBarBody>{t('solutions.cascade.warning', { name: solution.uniquename })}</MessageBarBody>
            </MessageBar>
            <h4>{t('solutions.cascade.listTitle')}</h4>
            {components.loading && <Spinner label={t('solutions.cascade.listLoading')} />}
            {components.error && <p>{t('solutions.cascade.listFailed', { detail: components.error })}</p>}
            {!components.loading && !components.error && counts.length === 0 && <p>{t('solutions.cascade.listEmpty')}</p>}
            <ul>
              {counts.map(([type, count]) => (
                <li key={type}>{t('solutions.cascade.count', { label: componentTypeLabel(type), count })}</li>
              ))}
            </ul>
            {components.nextLink && (
              <>
                <p>{t('solutions.cascade.more')}</p>
                <Button disabled={components.loading} onClick={components.loadMore}>
                  {t('common.loadMore')}
                </Button>
              </>
            )}
            <Field label={t('solutions.cascade.typeName', { name: solution.uniquename })}>
              <Input value={typed} onChange={(_, d) => setTyped(d.value)} autoComplete="off" />
            </Field>
          </DialogContent>
          <DialogActions>
            <Button appearance="secondary" disabled={busy} onClick={() => onClose(false)}>
              {t('common.cancel')}
            </Button>
            <Button appearance="primary" disabled={typed !== solution.uniquename || busy} onClick={() => void cascade()}>
              {t('solutions.cascade.confirm')}
            </Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}
