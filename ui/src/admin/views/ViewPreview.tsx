import { MessageBar, MessageBarBody, Spinner, Table, TableBody, TableCell, TableHeader, TableHeaderCell, TableRow } from '@fluentui/react-components';
import { useEffect, useMemo, useRef, useState } from 'react';
import { cellText } from '../../apps/runView';
import { useAuth } from '../../shell/auth/AuthProvider';
import { t } from '../../shell/i18n/t';
import { previewView } from './viewApi';
import { writeView, type DesignColumn, type ViewDesign } from './viewDesign';

/** How long after the last change the preview is read again (E8). */
export const PREVIEW_DELAY_MS = 500;

export interface ViewPreviewProps {
  design: ViewDesign;
  /** The table's entity set; without one there is no preview. */
  entitySet?: string;
  header: (column: DesignColumn) => string;
}

/**
 * The first 10 rows of the design (E8), read at once on open and again 500 ms after the last change of what the rows
 * depend on (the FetchXML and the cells; not the name or description); an answer to an older request is dropped. An error is shown here only: it never blocks saving.
 */
export function ViewPreview({ design, entitySet, header }: ViewPreviewProps) {
  const { api } = useAuth();
  const [rows, setRows] = useState<Record<string, unknown>[]>();
  const [error, setError] = useState<string>();
  const [loading, setLoading] = useState(false);
  // The columns of the rows on show: the headers change with the rows, not before.
  const shown = useRef<DesignColumn[]>();
  const first = useRef(true);
  const request = useRef(0);
  const latest = useRef(design);
  latest.current = design;
  // What the rows depend on; a design the model cannot write is read (and fails) once per change.
  const query = useMemo(() => {
    let fetch: string;
    try {
      fetch = writeView(design).fetchxml;
    } catch (e) {
      fetch = `invalid: ${e instanceof Error ? e.message : String(e)}`;
    }
    return `${fetch}\n${design.columns.map((c) => c.name).join(',')}`;
  }, [design]);

  useEffect(() => {
    if (!api || !entitySet) return;
    const run = () => {
      const design = latest.current;
      const mine = ++request.current;
      setLoading(true);
      previewView(api, entitySet, design)
        .then((value) => {
          if (mine !== request.current) return;
          shown.current = design.columns;
          setRows(value);
          setError(undefined);
        })
        .catch((e: unknown) => {
          if (mine !== request.current) return;
          setRows(undefined);
          setError(e instanceof Error ? e.message : String(e));
        })
        .finally(() => mine === request.current && setLoading(false));
    };
    if (first.current) {
      first.current = false;
      run();
      return undefined;
    }
    const timer = setTimeout(run, PREVIEW_DELAY_MS);
    return () => clearTimeout(timer);
  }, [api, entitySet, query]);

  // Requests still running when the page closes are ignored.
  useEffect(() => () => void (request.current += 1), []);

  if (!entitySet) return <p>{t('views.preview.unavailable')}</p>;
  const columns = shown.current ?? [];
  return (
    <>
      {loading && <Spinner size="tiny" label={t('common.loading')} />}
      {error && (
        <MessageBar intent="error">
          <MessageBarBody>{t('views.preview.failed', { detail: error })}</MessageBarBody>
        </MessageBar>
      )}
      {rows && rows.length === 0 && <p>{t('views.preview.empty')}</p>}
      {rows && rows.length > 0 && (
        <Table aria-label={t('views.preview.label')} size="small">
          <TableHeader>
            <TableRow>
              {columns.map((c) => (
                <TableHeaderCell key={c.name}>{header(c)}</TableHeaderCell>
              ))}
            </TableRow>
          </TableHeader>
          <TableBody>
            {rows.map((row, i) => (
              <TableRow key={i}>
                {columns.map((c) => (
                  <TableCell key={c.name}>{cellText(row, { ...c, isHidden: false })}</TableCell>
                ))}
              </TableRow>
            ))}
          </TableBody>
        </Table>
      )}
    </>
  );
}
