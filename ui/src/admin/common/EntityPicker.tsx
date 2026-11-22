import { Button, Field, Input, makeStyles, Spinner, tokens } from '@fluentui/react-components';
import { useEffect, useState } from 'react';
import { literal } from '../../shell/api/odata';
import { t } from '../../shell/i18n/t';
import { useDebounced } from './useDebounced';
import { usePagedList } from './usePagedList';

export interface PickedEntity {
  id: string;
  name: string;
}

const usePickerStyles = makeStyles({
  list: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalXXS, marginBlock: tokens.spacingVerticalS },
  row: { justifyContent: 'flex-start', width: '100%' },
});

export interface EntityPickerProps {
  /** `list`: one result per row, as in the lookup's side panel; inline (default) otherwise. */
  layout?: 'inline' | 'list';
  /** Entity set to search, e.g. `roles`. */
  entitySet: string;
  idAttribute: string;
  /** The primary name attribute searched with `contains` and shown. */
  nameAttribute: string;
  label: string;
  /** An extra `$filter` expression (not encoded; wrapped in parentheses), e.g. `_businessunitid_value eq <guid>`. */
  filter?: string;
  selected?: PickedEntity;
  onSelect: (entity: PickedEntity) => void;
  /** The table's logical name: needed with `fetchFilters`. */
  entityName?: string;
  /**
   * Called before each search; the FetchXML `<filter>` elements it returns are ANDed with the name search, which then runs as
   * a FetchXML query (no "load more": the first page only). Not combined with `filter`.
   */
  fetchFilters?: () => string[];
  /** The search term the picker opens with. */
  initialTerm?: string;
}

const xmlText = (s: string) => s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;').replace(/'/g, '&apos;');

/** The FetchXML of a name search narrowed by `filters`. */
function fetchSearch(p: EntityPickerProps, entityName: string, term: string, filters: string[]): string {
  const name = xmlText(p.nameAttribute);
  const like = term ? `<condition attribute="${name}" operator="like" value="%${xmlText(term)}%"/>` : '';
  const xml =
    `<fetch count="${PAGE_SIZE}"><entity name="${xmlText(entityName)}"><attribute name="${xmlText(p.idAttribute)}"/><attribute name="${name}"/>` +
    `<order attribute="${name}"/><filter type="and">${like}${filters.join('')}</filter></entity></fetch>`;
  return `${p.entitySet}?fetchXml=${encodeURIComponent(xml)}`;
}

const SEARCH_DELAY_MS = 300;
const PAGE_SIZE = 20;

/** Search-as-you-type picker over one table: `contains` on the primary name, debounced, 20 per page. */
export function EntityPicker(p: EntityPickerProps) {
  const styles = usePickerStyles();
  const [term, setTerm] = useState(p.initialTerm ?? '');
  const debounced = useDebounced(term.trim(), SEARCH_DELAY_MS);
  const clauses = [debounced ? `contains(${p.nameAttribute},${literal(debounced)})` : '', p.filter ? `(${p.filter})` : ''].filter(Boolean);
  const filter = clauses.length ? `&$filter=${encodeURIComponent(clauses.join(' and '))}` : '';
  const plain = `${p.entitySet}?$select=${p.idAttribute},${p.nameAttribute}&$orderby=${p.nameAttribute}${filter}`;
  // The extra filters are asked for once per search term (they run the caller's handlers), never during render.
  const { fetchFilters, entityName } = p;
  const [extra, setExtra] = useState<{ term: string; filters: string[] }>();
  useEffect(() => {
    if (fetchFilters) setExtra({ term: debounced, filters: fetchFilters() });
  }, [fetchFilters, debounced]);
  const path = !fetchFilters
    ? plain
    : extra?.term !== debounced
      ? undefined
      : extra.filters.length > 0 && entityName
        ? fetchSearch(p, entityName, debounced, extra.filters)
        : plain;
  const list = usePagedList<Record<string, string>>(path, PAGE_SIZE);
  return (
    <div>
      <Field label={p.label}>
        <Input value={term} onChange={(_, d) => setTerm(d.value)} placeholder={t('picker.search')} />
      </Field>
      {list.loading && <Spinner size="tiny" label={t('picker.loading')} />}
      {!list.loading && !list.error && list.items.length === 0 && <p>{t('picker.empty')}</p>}
      {list.items.length > 0 && (
        <div role="listbox" className={p.layout === 'list' ? styles.list : undefined} aria-label={t('picker.results', { label: p.label })}>
          {list.items.map((row) => {
            const id = row[p.idAttribute];
            const name = row[p.nameAttribute] ?? '';
            return (
              <Button
                key={id}
                role="option"
                aria-selected={p.selected?.id === id}
                appearance={p.selected?.id === id ? 'primary' : 'subtle'}
                className={p.layout === 'list' ? styles.row : undefined}
                onClick={() => p.onSelect({ id, name })}
              >
                {name}
              </Button>
            );
          })}
        </div>
      )}
      {list.nextLink && !list.loading && <Button onClick={list.loadMore}>{t('picker.loadMore')}</Button>}
      {p.selected && <p>{t('picker.selected', { name: p.selected.name })}</p>}
    </div>
  );
}
