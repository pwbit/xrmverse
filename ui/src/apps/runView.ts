import type { ViewColumn } from '../model/view';
import { parseFetch, withPaging } from '../parsers/fetchXml';
import { parseXml } from '../parsers/xml';
import type { XrmApi } from '../shell/api/client';

export const FORMATTED_VALUE = '@OData.Community.Display.V1.FormattedValue';
const PAGING_COOKIE = '@Microsoft.Dynamics.CRM.fetchxmlpagingcookie';
const MORE_RECORDS = '@Microsoft.Dynamics.CRM.morerecords';
const TOTAL = '@Microsoft.Dynamics.CRM.totalrecordcount';
const TOTAL_LIMIT_EXCEEDED = '@Microsoft.Dynamics.CRM.totalrecordcountlimitexceeded';
/** `returntotalrecordcount` has the same result as `$count` (Learn: "Count rows using FetchXml"). */
const ODATA_COUNT = '@odata.count';

const PAGE_SIZES = [25, 50, 75, 100, 250];
const DEFAULT_PAGE_SIZE = 50;

/** `usersettings.paginglimit` as a page size: one of 25, 50, 75, 100 and 250, else 50. */
export function pageSize(pagingLimit: number | null | undefined): number {
  return pagingLimit !== null && pagingLimit !== undefined && PAGE_SIZES.includes(pagingLimit) ? pagingLimit : DEFAULT_PAGE_SIZE;
}

export interface ViewPage {
  rows: Record<string, unknown>[];
  /** The paging cookie to send for the next page (the inner `<cookie>` the fetch's `paging-cookie` takes). */
  cookie?: string;
  /** The total record count when the fetch asked for it: at most 5,000 (500 for an elastic table). */
  total?: number;
  /** More records match than {@link total} says (the count stopped at its limit). */
  totalMore?: boolean;
  more: boolean;
}

/**
 * The inner cookie of a `fetchxmlpagingcookie` annotation: `<cookie pagenumber="N" pagingcookie="…" />`, whose
 * `pagingcookie` attribute is the cookie URL-encoded twice.
 */
function innerCookie(annotation: unknown): string | undefined {
  if (typeof annotation !== 'string' || annotation === '') return undefined;
  const encoded = parseXml(annotation).documentElement.getAttribute('pagingcookie');
  return encoded ? decodeURIComponent(decodeURIComponent(encoded)) : undefined;
}

function totalOf(body: Record<string, unknown>): Pick<ViewPage, 'total' | 'totalMore'> {
  const total = body[ODATA_COUNT] ?? body[TOTAL];
  if (typeof total !== 'number' || total < 0) return {};
  return body[TOTAL_LIMIT_EXCEEDED] === true ? { total, totalMore: true } : { total };
}

/**
 * `GET {entitySet}?fetchXml=…` of page `page` (the fetch's own `count`, else 50) with the formatted value annotations;
 * `signal` cancels it.
 */
export async function runView(
  client: XrmApi,
  entitySet: string,
  fetchXml: string,
  page: number,
  cookie?: string,
  signal?: AbortSignal,
): Promise<ViewPage> {
  const count = parseFetch(fetchXml).count ?? DEFAULT_PAGE_SIZE;
  const fetch = withPaging(fetchXml, page, count, cookie);
  const body = await client.get<Record<string, unknown> & { value?: Record<string, unknown>[] }>(
    `${entitySet}?fetchXml=${encodeURIComponent(fetch)}`,
    { headers: { Prefer: 'odata.include-annotations="*"' }, signal },
  );
  return {
    rows: body.value ?? [],
    cookie: innerCookie(body[PAGING_COOKIE]),
    ...totalOf(body),
    more: body[MORE_RECORDS] === true,
  };
}

function text(value: unknown): string {
  return typeof value === 'string' || typeof value === 'number' || typeof value === 'boolean' ? String(value) : '';
}

const LOOKUP_LOGICAL_NAME = '@Microsoft.Dynamics.CRM.lookuplogicalname';
const GUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

/**
 * The text of a cell: the formatted value when there is one, a lookup by its name (never its id), a link-entity column
 * (`alias.attr`) under its alias key. A column the row lacks (field security leaves it out) is empty.
 */
export function cellText(row: Record<string, unknown>, column: ViewColumn): string {
  if (column.alias) {
    const key = `${column.alias}.${column.attribute}`;
    const formatted = row[key + FORMATTED_VALUE];
    if (formatted !== undefined) return text(formatted);
    // A related table's lookup is its id: Dataverse marks it with lookuplogicalname, xrmverse writes the bare GUID
    // (ODataJson.cs). Without its name the cell stays empty rather than show the id.
    const value = row[key];
    if (row[key + LOOKUP_LOGICAL_NAME] !== undefined || (typeof value === 'string' && GUID.test(value))) return '';
    return text(value);
  }
  const lookup = `_${column.attribute}_value`;
  return text(row[column.attribute + FORMATTED_VALUE] ?? row[lookup + FORMATTED_VALUE] ?? row[column.attribute]);
}
