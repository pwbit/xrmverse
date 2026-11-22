import { Button, Card, Link, makeStyles, Menu, MenuItem, MenuList, MenuPopover, MenuTrigger, Text, tokens } from '@fluentui/react-components';
import { t } from '../../shell/i18n/t';
import { selectionControls, type ViewGridProps } from '../classic/ViewGrid';

const useStyles = makeStyles({
  bar: { display: 'flex', alignItems: 'center', justifyContent: 'flex-end', marginBottom: tokens.spacingVerticalS },
  page: { marginInlineEnd: 'auto' },
  head: { display: 'flex', alignItems: 'center', gap: tokens.spacingHorizontalXS },
  list: {
    listStyleType: 'none',
    margin: 0,
    padding: 0,
    display: 'grid',
    // A card is 260px wide at least, unless the list is narrower (a phone): then one card fills the row.
    gridTemplateColumns: 'repeat(auto-fill, minmax(min(260px, 100%), 1fr))',
    gap: tokens.spacingVerticalM,
  },
  title: { color: tokens.colorBrandForegroundLink, textAlign: 'start', fontWeight: tokens.fontWeightSemibold },
  line: { color: tokens.colorNeutralForeground2 },
});

/** How many columns after the title a card shows (ruling B6). */
const LINES = 4;

/**
 * The rows as cards (ruling B6): the first visible column is the title (a link that opens the record), the next four
 * columns are `header: value` lines with the empty values skipped; sorting is a "Sort by" menu over the columns.
 */
export function CardList({ label, columns, headers, rows, sort, onSort, onOpen, renderCellContent, selection }: ViewGridProps) {
  const styles = useStyles();
  const controls = selection ? selectionControls(selection, rows, columns) : undefined;
  const [first, ...rest] = columns;
  const lines = rest.slice(0, LINES);
  const header = (name: string) => headers[name] ?? name;
  return (
    <div>
      <div className={styles.bar}>
        {controls && <div className={styles.page}>{controls.page}</div>}
        <Menu>
          <MenuTrigger disableButtonEnhancement>
            <Button appearance="subtle">{t('apps.views.cards.sortBy')}</Button>
          </MenuTrigger>
          <MenuPopover>
            <MenuList>
              {columns.map((column) => {
                const current = !!sort && sort.attribute === column.attribute && sort.linkAlias === column.alias;
                return (
                  <MenuItem
                    key={column.name}
                    aria-current={current || undefined}
                    onClick={() => onSort({ attribute: column.attribute, descending: current && !sort.descending, linkAlias: column.alias })}
                  >
                    {header(column.name)}
                    {current ? (sort.descending ? ' ↓' : ' ↑') : ''}
                  </MenuItem>
                );
              })}
            </MenuList>
          </MenuPopover>
        </Menu>
      </div>
      <ul className={styles.list} role="list" aria-label={label}>
        {rows.map((row) => {
          const title = first ? (row.cells[first.name] ?? '') : '';
          return (
            <li key={row.key}>
              <Card>
                <div className={styles.head}>
                  {controls?.row(row)}
                  {row.id ? (
                    <Link as="button" className={styles.title} onClick={() => onOpen(row.id as string)}>
                      {title}
                    </Link>
                  ) : (
                    <Text weight="semibold">{title}</Text>
                  )}
                </div>
                {lines.map((column) => {
                  const value = row.cells[column.name] ?? '';
                  return value === '' ? null : (
                    <Text key={column.name} className={styles.line}>
                      {header(column.name)}: {renderCellContent ? renderCellContent(row, column, value) : value}
                    </Text>
                  );
                })}
              </Card>
            </li>
          );
        })}
      </ul>
    </div>
  );
}
