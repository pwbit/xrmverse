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
import { useEffect, useState } from 'react';
import { useAuth } from '../../shell/auth/AuthProvider';
import { t } from '../../shell/i18n/t';
import { componentTypeLabel } from './componentTypes';

/** One row of `GET /admin/solutions/{name}/layers`. */
export interface LayerRow {
  componentType?: number;
  objectId?: string;
  solution: string;
  kind: string;
  order: number;
  version?: string | null;
}

/**
 * The layers a solution owns (`GET /admin/solutions/{name}/layers`), the top layer first. (The layers of one component,
 * `GET /admin/layers/{type}/{id}`, are served by the Admin API, the CLI and MCP; the web admin does not show them.)
 */
export function LayersPanel({ target, onClose }: { target: { solution: string }; onClose: () => void }) {
  const { api } = useAuth();
  const path = `solutions/${encodeURIComponent(target.solution)}/layers`;
  const [rows, setRows] = useState<LayerRow[]>();
  const [error, setError] = useState<string>();

  useEffect(() => {
    if (!api) return;
    let live = true;
    api
      .admin<LayerRow[]>('GET', path)
      .then((r) => live && setRows([...r].sort((a, b) => b.order - a.order)))
      .catch((e) => live && setError(e instanceof Error ? e.message : String(e)));
    return () => {
      live = false;
    };
  }, [api, path]);

  return (
    <Dialog open onOpenChange={(_, d) => !d.open && onClose()}>
      <DialogSurface>
        <DialogBody>
          <DialogTitle>{t('solutions.layers.title')}</DialogTitle>
          <DialogContent>
            {!rows && !error && <Spinner label={t('solutions.layers.loading')} />}
            {error && (
              <MessageBar intent="error">
                <MessageBarBody>{t('solutions.layers.failed', { detail: error })}</MessageBarBody>
              </MessageBar>
            )}
            {rows && rows.length === 0 && <p>{t('solutions.layers.empty')}</p>}
            {rows && rows.length > 0 && (
              <Table aria-label={t('solutions.layers.title')}>
                <TableHeader>
                  <TableRow>
                    <TableHeaderCell>{t('solutions.layers.col.component')}</TableHeaderCell>
                    <TableHeaderCell>{t('solutions.layers.col.solution')}</TableHeaderCell>
                    <TableHeaderCell>{t('solutions.layers.col.kind')}</TableHeaderCell>
                    <TableHeaderCell>{t('solutions.layers.col.order')}</TableHeaderCell>
                    <TableHeaderCell>{t('solutions.layers.col.version')}</TableHeaderCell>
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {rows.map((r, i) => (
                    <TableRow key={i}>
                      <TableCell>
                        {r.componentType === undefined ? '' : `${componentTypeLabel(r.componentType)} ${r.objectId ?? ''}`}
                      </TableCell>
                      <TableCell>{r.solution}</TableCell>
                      <TableCell>{r.kind}</TableCell>
                      <TableCell>{r.order}</TableCell>
                      <TableCell>{r.version ?? ''}</TableCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
            )}
          </DialogContent>
          <DialogActions>
            <Button appearance="primary" onClick={onClose}>
              {t('common.close')}
            </Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}
