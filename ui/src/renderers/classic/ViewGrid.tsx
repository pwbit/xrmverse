import {
  Checkbox,
  createTableColumn,
  DataGrid,
  DataGridBody,
  DataGridCell,
  DataGridHeader,
  DataGridHeaderCell,
  DataGridRow,
  Link,
  makeStyles,
  mergeClasses,
  type TableColumnDefinition,
  type TableColumnSizingOptions,
  tokens,
} from '@fluentui/react-components';
import { createContext, useContext, useMemo, type ReactNode } from 'react';
import type { ViewColumn } from '../../model/view';
import { spacedMoney, type CellKind } from '../../apps/columnHeaders';
import { t } from '../../shell/i18n/t';

export interface GridRow {
  /** A key unique in the page. */
  key: string;
  /** The record id (primary key); absent when the row has none. */
  id?: string;
  /** The text of each cell, by column name. */
  cells: Record<string, string>;
  /** The raw Web API row the cells come from (the modern look reads choice values from it). */
  values?: Record<string, unknown>;
}

export interface GridSort {
  attribute: string;
  descending: boolean;
  /** The link-entity alias of a related table's column (`alias.attr`, #100). */
  linkAlias?: string;
}

export interface ViewGridProps {
  /** The accessible name of the grid (the view's name). */
  label: string;
  /** The visible columns, in LayoutXml order. */
  columns: ViewColumn[];
  /** The header label of each column, by column name. */
  headers: Record<string, string>;
  rows: GridRow[];
  /** The order shown on the headers (a root column). */
  sort?: GridSort;
  onSort: (sort: GridSort) => void;
  /** A row was clicked; the event tells Ctrl/Cmd and the row element (a side pane is sized from the form around it). */
  onOpen: (id: string, event?: React.MouseEvent<HTMLElement>) => void;
  /** The columns that are numbers, money or dates, by column name: shown at the end of the cell, header included. */
  kinds?: Record<string, CellKind>;
  /** How the modern renderer shows the rows; the classic grid ignores it. */
  layout?: 'grid' | 'cards';
  /** The table of the rows (the modern look reads its choice colours). */
  table?: string;
  /** The content of a non-first cell in place of its text (the first cell stays its record link or text); classic never passes it. */
  renderCellContent?: (row: GridRow, column: ViewColumn, text: string) => ReactNode;
  /**
   * The selected record ids (a view's command bar): a checkbox column comes first, its header checkbox selects or clears the
   * current page; a row click still opens the record.
   */
  selection?: ViewSelection;
  /**
   * The cell names (lowercase) whose text wraps onto more lines (the overlay's `wrap` of the view). Every other cell stays on
   * one line, cut with an ellipsis, its full text in a tooltip, as in Dataverse.
   */
  wrap?: readonly string[];
  /**
   * The grid sits in a scrolling area of the page (a table's list): it does not scroll on its own, and its column headers
   * stay at the top of that area while the rows scroll under them.
   */
  stickyHeader?: boolean;
}

export interface ViewSelection {
  selected: ReadonlySet<string>;
  onChange(next: Set<string>): void;
}

const SelectionContext = createContext<ReturnType<typeof selectionControls> | undefined>(undefined);
const PageCheckbox = () => useContext(SelectionContext)?.page ?? null;
const RowCheckbox = ({ row }: { row: GridRow }) => useContext(SelectionContext)?.row(row) ?? null;

/** The column id of the selection checkboxes (never a column name of a view: those are attribute names). */
const SELECT_COLUMN = '__select';

/** The accessible name of a row's checkbox: the text of its first cell, else its id. */
const rowName = (row: GridRow, columns: ViewColumn[]) => (columns[0] ? row.cells[columns[0].name] : undefined) || row.id || row.key;

/** The checkbox of a row and the one of the page (its header), for the grid and the cards. */
export function selectionControls(selection: ViewSelection, rows: GridRow[], columns: ViewColumn[]) {
  const ids = rows.flatMap((r) => (r.id ? [r.id] : []));
  const count = ids.filter((id) => selection.selected.has(id)).length;
  const page = (
    <Checkbox
      aria-label={t('apps.views.select.page')}
      checked={count === 0 ? false : count === ids.length ? true : 'mixed'}
      disabled={ids.length === 0}
      onChange={(_e, data) => selection.onChange(new Set(data.checked === true ? [...selection.selected, ...ids] : [...selection.selected].filter((id) => !ids.includes(id))))}
    />
  );
  const row = (item: GridRow) =>
    item.id ? (
      <Checkbox
        aria-label={t('apps.views.select.row', { name: rowName(item, columns) })}
        checked={selection.selected.has(item.id)}
        // The row's click opens the record: the checkbox keeps its own click.
        onClick={(e) => e.stopPropagation()}
        onChange={(_e, data) => {
          const next = new Set(selection.selected);
          if (data.checked) next.add(item.id as string);
          else next.delete(item.id as string);
          selection.onChange(next);
        }}
      />
    ) : null;
  return { page, row };
}

const useStyles = makeStyles({
  grid: { overflowX: 'auto' },
  // In a scrolling area: the area scrolls both ways, the grid takes the width its columns need.
  inArea: { width: 'max-content', minWidth: '100%' },
  sticky: { position: 'sticky', top: 0, zIndex: 1, backgroundColor: tokens.colorNeutralBackground1 },
  row: { cursor: 'pointer' },
  link: { color: tokens.colorBrandForegroundLink, textAlign: 'start' },
  // One line, cut with an ellipsis (the default, as in Dataverse); the cell's flex layout needs `minWidth: 0` to let it shrink.
  clip: {
    display: 'block',
    minWidth: 0,
    width: '100%',
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
    '& .fui-Link': { maxWidth: '100%', overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', verticalAlign: 'bottom' },
    '& .fui-Badge': { maxWidth: '100%', overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', display: 'inline-block' },
  },
  // A column set to wrap: its text breaks onto more lines, long words included.
  wrapped: { display: 'block', minWidth: 0, width: '100%', whiteSpace: 'normal', overflowWrap: 'anywhere', paddingBlock: tokens.spacingVerticalXS },
  // Numbers and dates line up at the end of the cell; the header's sort button and label follow.
  // The column titles are bold, as the view's header.
  header: { fontWeight: tokens.fontWeightSemibold, '& .fui-TableHeaderCell__button': { fontWeight: tokens.fontWeightSemibold } },
  end: { justifyContent: 'flex-end', textAlign: 'end', '& .fui-TableHeaderCell__button': { justifyContent: 'flex-end' } },
});

const KEEP_ORDER = (_a: GridRow, _b: GridRow) => 0;

/**
 * The read-only grid of a view: the LayoutXml columns with their pixel widths (resizable), keyboard navigation,
 * header sorting on root and related-table columns, and the first cell as a link that opens the record.
 */
export function ViewGrid({ label, columns, headers, rows, sort, onSort, onOpen, kinds = {}, renderCellContent, selection, wrap, stickyHeader }: ViewGridProps) {
  const styles = useStyles();
  // The definitions change only when the list of wrapped columns does, not when the caller passes an equal new array.
  const wrapKey = (wrap ?? []).map((n) => n.toLowerCase()).join('|');
  const selected = selection?.selected;
  const onSelect = selection?.onChange;
  const controls = useMemo(
    () => (selected && onSelect ? selectionControls({ selected, onChange: onSelect }, rows, columns) : undefined),
    [selected, onSelect, rows, columns],
  );
  // The checkbox cells read the controls from a context: the column definitions (and the column widths) stay as they are
  // when the selection changes.
  const selectable = !!controls;
  const wrapped = useMemo(() => new Set(wrapKey ? wrapKey.split('|') : []), [wrapKey]);
  const definitions = useMemo<TableColumnDefinition<GridRow>[]>(
    () => [
      ...(selectable
        ? [
            createTableColumn<GridRow>({
              columnId: SELECT_COLUMN,
              renderHeaderCell: () => <PageCheckbox />,
              renderCell: (row) => <RowCheckbox row={row} />,
            }),
          ]
        : []),
      ...columns.map((column, index) =>
        createTableColumn<GridRow>({
          columnId: column.name,
          // The rows come sorted by the server; a two-argument compare only marks the column sortable.
          compare: KEEP_ORDER,
          renderHeaderCell: () => headers[column.name] ?? column.name,
          renderCell: (row) => {
            const raw = row.cells[column.name] ?? '';
            const text = kinds[column.name] === 'money' ? spacedMoney(raw) : raw;
            const content =
              index === 0 && row.id ? (
                <Link as="button" className={styles.link}>
                  {text}
                </Link>
              ) : renderCellContent && index !== 0 ? (
                renderCellContent(row, column, text)
              ) : (
                text
              );
            const wraps = wrapped.has(column.name.toLowerCase());
            return (
              <span className={wraps ? styles.wrapped : styles.clip} title={!wraps && text ? text : undefined} data-wrap={wraps ? '' : undefined}>
                {content}
              </span>
            );
          },
        }),
      ),
    ],
    [columns, headers, styles.link, styles.clip, styles.wrapped, kinds, renderCellContent, selectable, wrapped],
  );
  const sizing = useMemo<TableColumnSizingOptions>(
    () => ({
      [SELECT_COLUMN]: { idealWidth: 44, defaultWidth: 44, minWidth: 44 },
      ...Object.fromEntries(columns.map((c) => [c.name, { idealWidth: c.width, defaultWidth: c.width, minWidth: Math.min(c.width, 40) }])),
    }),
    [columns],
  );
  const sortColumn = sort ? columns.find((c) => c.attribute === sort.attribute && c.alias === sort.linkAlias)?.name : undefined;

  return (
    <SelectionContext.Provider value={controls}>
      <div className={stickyHeader ? styles.inArea : styles.grid}>
        <DataGrid
          aria-label={label}
          items={rows}
          columns={definitions}
          getRowId={(row: GridRow) => row.key}
          sortable
          sortState={sortColumn ? { sortColumn, sortDirection: sort?.descending ? 'descending' : 'ascending' } : { sortColumn: undefined, sortDirection: 'ascending' }}
          onSortChange={(_e, state) => {
            const column = columns.find((c) => c.name === state.sortColumn);
            if (column) onSort({ attribute: column.attribute, descending: state.sortDirection === 'descending', linkAlias: column.alias });
          }}
          resizableColumns
          resizableColumnsOptions={{ autoFitColumns: false }}
          columnSizingOptions={sizing}
          focusMode="composite"
        >
          <DataGridHeader className={stickyHeader ? styles.sticky : undefined}>
            <DataGridRow>
              {({ renderHeaderCell, columnId }) => (
                <DataGridHeaderCell className={mergeClasses(styles.header, kinds[String(columnId)] && styles.end)}>{renderHeaderCell()}</DataGridHeaderCell>
              )}
            </DataGridRow>
          </DataGridHeader>
          <DataGridBody<GridRow>>
            {({ item, rowId }) => (
              <DataGridRow<GridRow>
                key={rowId}
                className={styles.row}
                onClick={(e: React.MouseEvent<HTMLElement>) => {
                  // A click anywhere in the selection cell (the checkbox or the padding around it) selects, never opens.
                  if ((e.target as HTMLElement).closest('[data-select-cell]')) return;
                  if (item.id) onOpen(item.id, e);
                }}
              >
                {({ renderCell, columnId }) => (
                  <DataGridCell
                    className={kinds[String(columnId)] ? styles.end : undefined}
                    data-select-cell={columnId === SELECT_COLUMN ? '' : undefined}
                  >
                    {renderCell(item)}
                  </DataGridCell>
                )}
              </DataGridRow>
            )}
          </DataGridBody>
        </DataGrid>
      </div>
    </SelectionContext.Provider>
  );
}
