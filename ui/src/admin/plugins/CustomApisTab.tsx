import {
  Button,
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

/** `$select` without `iscustomizable`: the server does not serve it. The plug-in type is the single-valued navigation property of the lookup. */
const API_QUERY =
  'customapis?$select=customapiid,uniquename,name,displayname,description,bindingtype,boundentitylogicalname,isfunction,isprivate' +
  '&$expand=plugintypeid($select=typename)&$orderby=uniquename';
/** The server expands only single-valued navigation properties, so the fields are read apart and joined by `_customapiid_value`. */
const PARAMETER_QUERY = 'customapirequestparameters?$select=uniquename,type,isoptional,logicalentityname,_customapiid_value&$orderby=uniquename';
const PROPERTY_QUERY = 'customapiresponseproperties?$select=uniquename,type,logicalentityname,_customapiid_value&$orderby=uniquename';

/** The labels of the global choice `customapifieldtype`, by value. */
const FIELD_TYPES = [
  'Boolean', 'DateTime', 'Decimal', 'Entity', 'EntityCollection', 'EntityReference', 'Float', 'Integer', 'Money', 'Picklist', 'String',
  'StringArray', 'Guid',
];

type ApiRow = {
  customapiid: string;
  uniquename: string;
  displayname?: string | null;
  description?: string | null;
  bindingtype?: number | null;
  boundentitylogicalname?: string | null;
  isfunction?: boolean | null;
  isprivate?: boolean | null;
  plugintypeid?: { typename?: string | null } | null;
};
type FieldRow = {
  uniquename: string;
  type?: number | null;
  isoptional?: boolean | null;
  logicalentityname?: string | null;
  _customapiid_value: string;
};

const binding = (a: ApiRow) =>
  a.bindingtype === 1 ? `entity:${a.boundentitylogicalname ?? ''}` : a.bindingtype === 2 ? `collection:${a.boundentitylogicalname ?? ''}` : 'global';
const typeName = (f: FieldRow) => (f.type == null ? '' : (FIELD_TYPES[f.type] ?? String(f.type)));
const yesNo = (v?: boolean | null) => t(v ? 'common.yes' : 'common.no');

async function readAll<T>(list: (path: string) => AsyncIterable<T[]>, path: string): Promise<T[]> {
  const rows: T[] = [];
  for await (const page of list(path)) rows.push(...page);
  return rows;
}

function groupByApi(fields: FieldRow[]): Map<string, FieldRow[]> {
  const byApi = new Map<string, FieldRow[]>();
  for (const f of fields) byApi.set(f._customapiid_value, [...(byApi.get(f._customapiid_value) ?? []), f]);
  return byApi;
}

function FieldTable({ label, rows, optional }: { label: string; rows: FieldRow[]; optional: boolean }) {
  return (
    <Table aria-label={label}>
      <TableHeader>
        <TableRow>
          <TableHeaderCell>{t('customApis.col.fieldName')}</TableHeaderCell>
          <TableHeaderCell>{t('customApis.col.fieldType')}</TableHeaderCell>
          {optional && <TableHeaderCell>{t('customApis.col.optional')}</TableHeaderCell>}
          <TableHeaderCell>{t('customApis.col.table')}</TableHeaderCell>
        </TableRow>
      </TableHeader>
      <TableBody>
        {rows.map((f) => (
          <TableRow key={f.uniquename}>
            <TableCell>{f.uniquename}</TableCell>
            <TableCell>{typeName(f)}</TableCell>
            {optional && <TableCell>{yesNo(f.isoptional)}</TableCell>}
            <TableCell>{f.logicalentityname ?? ''}</TableCell>
          </TableRow>
        ))}
      </TableBody>
    </Table>
  );
}

function Detail({ api, parameters, properties }: { api: ApiRow; parameters: FieldRow[]; properties: FieldRow[] }) {
  return (
    <section aria-label={t('customApis.detail', { name: api.uniquename })}>
      <h3>{api.displayname || api.uniquename}</h3>
      {api.description && <p>{api.description}</p>}
      <h4>{t('customApis.parameters')}</h4>
      {parameters.length === 0 ? <p>{t('customApis.noParameters')}</p> : <FieldTable label={t('customApis.parameters')} rows={parameters} optional />}
      <h4>{t('customApis.properties')}</h4>
      {properties.length === 0 ? <p>{t('customApis.noProperties')}</p> : <FieldTable label={t('customApis.properties')} rows={properties} optional={false} />}
    </section>
  );
}

/** The custom APIs of the organization, read-only: a list and, for the selected API, its parameters and properties. */
export function CustomApisTab() {
  const { api } = useAuth();
  const [apis, setApis] = useState<ApiRow[]>([]);
  const [parameters, setParameters] = useState(new Map<string, FieldRow[]>());
  const [properties, setProperties] = useState(new Map<string, FieldRow[]>());
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string>();
  const [selected, setSelected] = useState<string>();

  useEffect(() => {
    if (!api) return;
    let current = true;
    const list = <T,>(path: string) => api.list<T>(path);
    Promise.all([readAll<ApiRow>(list, API_QUERY), readAll<FieldRow>(list, PARAMETER_QUERY), readAll<FieldRow>(list, PROPERTY_QUERY)])
      .then(([a, p, q]) => {
        if (!current) return;
        setApis(a);
        setParameters(groupByApi(p));
        setProperties(groupByApi(q));
      })
      .catch((e: unknown) => current && setError(e instanceof Error ? e.message : String(e)))
      .finally(() => current && setLoading(false));
    return () => {
      current = false;
    };
  }, [api]);

  const open = apis.find((a) => a.customapiid === selected);
  return (
    <div>
      <p>{t('customApis.import')}</p>
      {error && (
        <MessageBar intent="error">
          <MessageBarBody>{t('customApis.loadFailed', { detail: error })}</MessageBarBody>
        </MessageBar>
      )}
      {loading && <Spinner label={t('common.loading')} />}
      {!loading && !error && apis.length === 0 && <p>{t('customApis.empty')}</p>}
      {apis.length > 0 && (
        <Table aria-label={t('plugins.tab.customApis')}>
          <TableHeader>
            <TableRow>
              <TableHeaderCell>{t('customApis.col.name')}</TableHeaderCell>
              <TableHeaderCell>{t('customApis.col.binding')}</TableHeaderCell>
              <TableHeaderCell>{t('customApis.col.kind')}</TableHeaderCell>
              <TableHeaderCell>{t('customApis.col.private')}</TableHeaderCell>
              <TableHeaderCell>{t('customApis.col.pluginType')}</TableHeaderCell>
              <TableHeaderCell>{t('customApis.col.parameters')}</TableHeaderCell>
              <TableHeaderCell>{t('customApis.col.properties')}</TableHeaderCell>
            </TableRow>
          </TableHeader>
          <TableBody>
            {apis.map((a) => (
              <TableRow key={a.customapiid}>
                <TableCell>
                  <Button appearance="transparent" aria-label={t('customApis.open', { name: a.uniquename })} onClick={() => setSelected(a.customapiid)}>
                    {a.uniquename}
                  </Button>
                </TableCell>
                <TableCell>{binding(a)}</TableCell>
                <TableCell>{t(a.isfunction ? 'customApis.kind.function' : 'customApis.kind.action')}</TableCell>
                <TableCell>{yesNo(a.isprivate)}</TableCell>
                <TableCell>{a.plugintypeid?.typename ?? ''}</TableCell>
                <TableCell>{parameters.get(a.customapiid)?.length ?? 0}</TableCell>
                <TableCell>{properties.get(a.customapiid)?.length ?? 0}</TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      )}
      {open && <Detail api={open} parameters={parameters.get(open.customapiid) ?? []} properties={properties.get(open.customapiid) ?? []} />}
    </div>
  );
}
