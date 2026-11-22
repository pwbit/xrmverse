import {
  Button,
  DrawerBody,
  DrawerHeader,
  DrawerHeaderTitle,
  Field,
  Input,
  MessageBar,
  MessageBarBody,
  OverlayDrawer,
  Spinner,
  Table,
  TableBody,
  TableCell,
  TableHeader,
  TableHeaderCell,
  TableRow,
} from '@fluentui/react-components';
import { useState } from 'react';
import { literal } from '../../shell/api/odata';
import { t } from '../../shell/i18n/t';
import { useDebounced } from '../common/useDebounced';
import { usePagedList } from '../common/usePagedList';

const SELECT = 'plugintracelogid,createdon,typename,messagename,primaryentity,depth,mode,messageblock,exceptiondetails';

type Trace = {
  plugintracelogid: string;
  createdon?: string | null;
  typename?: string | null;
  messagename?: string | null;
  primaryentity?: string | null;
  depth?: number | null;
  mode?: number | null;
  messageblock?: string | null;
  exceptiondetails?: string | null;
};

const when = (iso?: string | null) => (iso ? new Date(iso).toLocaleString() : '');

/** The newest plug-in trace logs, filtered by the full type name; a row opens the message block and exception details. */
export function TracesPage() {
  const [typeName, setTypeName] = useState('');
  const filter = useDebounced(typeName.trim(), 300);
  const [selected, setSelected] = useState<Trace>();
  const query = `plugintracelogs?$select=${SELECT}&$orderby=${encodeURIComponent('createdon desc')}${
    filter ? `&$filter=${encodeURIComponent(`typename eq ${literal(filter)}`)}` : ''
  }`;
  const list = usePagedList<Trace>(query);

  return (
    <div>
      <Field label={t('traces.filter.type')}>
        <Input value={typeName} onChange={(_, d) => setTypeName(d.value)} />
      </Field>
      {list.error && (
        <MessageBar intent="error">
          <MessageBarBody>{t('traces.loadFailed', { detail: list.error })}</MessageBarBody>
        </MessageBar>
      )}
      {!list.loading && !list.error && list.items.length === 0 && <p>{t('traces.empty')}</p>}
      {list.items.length > 0 && (
        <Table aria-label={t('plugins.tab.traces')}>
          <TableHeader>
            <TableRow>
              <TableHeaderCell>{t('traces.col.time')}</TableHeaderCell>
              <TableHeaderCell>{t('traces.col.type')}</TableHeaderCell>
              <TableHeaderCell>{t('traces.col.message')}</TableHeaderCell>
              <TableHeaderCell>{t('traces.col.entity')}</TableHeaderCell>
              <TableHeaderCell>{t('traces.col.mode')}</TableHeaderCell>
              <TableHeaderCell>{t('traces.col.failed')}</TableHeaderCell>
            </TableRow>
          </TableHeader>
          <TableBody>
            {list.items.map((trace) => (
              <TableRow key={trace.plugintracelogid}>
                <TableCell>{when(trace.createdon)}</TableCell>
                <TableCell>
                  <Button
                    appearance="transparent"
                    aria-label={t('traces.detail.open', { type: trace.typename ?? '' })}
                    onClick={() => setSelected(trace)}
                  >
                    {trace.typename}
                  </Button>
                </TableCell>
                <TableCell>{trace.messagename}</TableCell>
                <TableCell>{trace.primaryentity}</TableCell>
                <TableCell>{t(trace.mode === 1 ? 'plugins.mode.1' : 'plugins.mode.0')}</TableCell>
                <TableCell>{t(trace.exceptiondetails ? 'traces.failed' : 'traces.ok')}</TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      )}
      {list.loading && <Spinner label={t('common.loading')} />}
      {list.nextLink && !list.loading && <Button onClick={list.loadMore}>{t('common.loadMore')}</Button>}
      <OverlayDrawer position="end" open={!!selected} onOpenChange={(_, d) => !d.open && setSelected(undefined)}>
        {selected && (
          <>
            <DrawerHeader>
              <DrawerHeaderTitle
                action={<Button appearance="subtle" onClick={() => setSelected(undefined)}>{t('traces.detail.close')}</Button>}
              >
                {selected.typename}
              </DrawerHeaderTitle>
            </DrawerHeader>
            <DrawerBody>
              <h3>{t('traces.detail.messageBlock')}</h3>
              <pre style={{ whiteSpace: 'pre-wrap' }}>{selected.messageblock || t('traces.detail.none')}</pre>
              <h3>{t('traces.detail.exception')}</h3>
              <pre style={{ whiteSpace: 'pre-wrap' }}>{selected.exceptiondetails || t('traces.detail.none')}</pre>
            </DrawerBody>
          </>
        )}
      </OverlayDrawer>
    </div>
  );
}
