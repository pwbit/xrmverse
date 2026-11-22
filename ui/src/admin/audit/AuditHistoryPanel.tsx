import { Button, MessageBar, MessageBarBody, Spinner, Subtitle1, Table, TableBody, TableCell, TableHeader, TableHeaderCell, TableRow } from '@fluentui/react-components';
import { useEffect, useRef, useState } from 'react';
import { XrmError } from '../../shell/api/errors';
import { useAuth } from '../../shell/auth/AuthProvider';
import { t } from '../../shell/i18n/t';
import { changesOf, recordHistory, type HistoryEntry } from './auditApi';

const when = (iso?: string) => (iso ? new Date(iso).toLocaleString() : '');

/** The change history of one record (`RetrieveRecordChangeHistory`), newest first, 20 entries a page. Shown for audited tables only. */
export function AuditHistoryPanel({ id, reloadKey }: { id: string; reloadKey?: unknown }) {
  const { api } = useAuth();
  const [entries, setEntries] = useState<HistoryEntry[]>([]);
  // The link of the page to read: undefined is the first page. Bumping `start` begins again (a new record or a save).
  const [link, setLink] = useState<string>();
  const [start, setStart] = useState(0);
  const [nextLink, setNextLink] = useState<string>();
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<{ status: number; detail: string }>();

  const mounted = useRef(false);
  useEffect(() => {
    // Not on mount: the load below runs then.
    if (!mounted.current) {
      mounted.current = true;
      return;
    }
    setLink(undefined);
    setStart((n) => n + 1);
  }, [id, reloadKey]);

  useEffect(() => {
    if (!api) return;
    let live = true;
    setLoading(true);
    setError(undefined);
    recordHistory(api, id, link)
      .then((r) => {
        if (!live) return;
        setEntries((prev) => (link ? [...prev, ...r.entries] : r.entries));
        setNextLink(r.nextLink);
      })
      .catch((e: unknown) => {
        if (!live) return;
        setError({ status: e instanceof XrmError ? e.status : 0, detail: e instanceof XrmError ? e.serverMessage : e instanceof Error ? e.message : String(e) });
      })
      .finally(() => live && setLoading(false));
    return () => {
      live = false;
    };
  }, [api, id, link, start]);

  const rows = entries.flatMap((entry) => changesOf(entry.detail).map((c, j) => ({ entry, change: c, key: `${entry.auditid}-${j}` })));
  return (
    <section aria-label={t('audit.history.title')}>
      <h3>
        <Subtitle1>{t('audit.history.title')}</Subtitle1>
      </h3>
      {error && (
        <MessageBar intent="error">
          <MessageBarBody>
            {error.status === 403 ? t('audit.history.forbidden', { detail: error.detail }) : t('audit.history.loadFailed', { detail: error.detail })}
          </MessageBarBody>
        </MessageBar>
      )}
      {!loading && !error && rows.length === 0 && <p>{t('audit.history.empty')}</p>}
      {rows.length > 0 && (
        <Table aria-label={t('audit.history.title')}>
          <TableHeader>
            <TableRow>
              <TableHeaderCell>{t('audit.col.date')}</TableHeaderCell>
              <TableHeaderCell>{t('audit.col.user')}</TableHeaderCell>
              <TableHeaderCell>{t('audit.col.action')}</TableHeaderCell>
              <TableHeaderCell>{t('audit.detail.col.column')}</TableHeaderCell>
              <TableHeaderCell>{t('audit.detail.col.old')}</TableHeaderCell>
              <TableHeaderCell>{t('audit.detail.col.new')}</TableHeaderCell>
            </TableRow>
          </TableHeader>
          <TableBody>
            {rows.map(({ entry, change, key }) => (
              <TableRow key={key}>
                <TableCell>{when(entry.date)}</TableCell>
                <TableCell>{entry.user ?? ''}</TableCell>
                <TableCell>{entry.action ?? ''}</TableCell>
                <TableCell>{change.column}</TableCell>
                <TableCell>{change.oldValue || t('audit.history.empty.value')}</TableCell>
                <TableCell>{change.newValue || t('audit.history.empty.value')}</TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      )}
      {loading && <Spinner size="tiny" label={t('audit.loading')} />}
      {nextLink && !loading && <Button onClick={() => setLink(nextLink)}>{t('audit.history.more')}</Button>}
    </section>
  );
}
