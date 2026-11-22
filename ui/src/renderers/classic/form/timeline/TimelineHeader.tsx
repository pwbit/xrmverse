import { Button, makeStyles, Menu, MenuItem, MenuItemRadio, MenuList, MenuPopover, MenuTrigger, Text, tokens, Tooltip } from '@fluentui/react-components';
import type { ReactNode } from 'react';
import { t } from '../../../../shell/i18n/t';
import { CollapseAllIcon, ExpandAllIcon, FilterAppliedIcon, FilterIcon, MoreIcon, RefreshIcon, SortNewestIcon, SortOldestIcon } from '../../../../shell/icons';

const useStyles = makeStyles({
  header: { display: 'flex', alignItems: 'center', gap: tokens.spacingHorizontalXS, minHeight: '32px' },
  label: { flexGrow: 1, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' },
});

export interface TimelineHeaderProps {
  /** The cell's label; absent when the cell hides it. */
  label?: string;
  /** The create menu (+), when there is one. */
  createMenu?: ReactNode;
  paneOpen: boolean;
  filtersApplied: boolean;
  onTogglePane: () => void;
  allExpanded: boolean;
  onToggleExpandAll: () => void;
  sort: 'desc' | 'asc';
  onSort: (sort: 'desc' | 'asc') => void;
  onRefresh: () => void;
}

/** The timeline's title row: its label, then create, filter, expand or collapse all, and More (Refresh, sort order). */
export function TimelineHeader({ label, createMenu, paneOpen, filtersApplied, onTogglePane, allExpanded, onToggleExpandAll, sort, onSort, onRefresh }: TimelineHeaderProps) {
  const styles = useStyles();
  const filterLabel = filtersApplied ? t('record.timeline.filterApplied') : t('record.timeline.filter');
  const expandLabel = allExpanded ? t('record.timeline.collapseAll') : t('record.timeline.expandAll');
  return (
    <div className={styles.header}>
      <Text weight="semibold" className={styles.label}>
        {label || t('record.timeline.title')}
      </Text>
      <div role="toolbar" aria-label={t('record.timeline.commands')}>
        {createMenu}
        <Tooltip content={filterLabel} relationship="label">
          <Button appearance="subtle" icon={filtersApplied ? <FilterAppliedIcon /> : <FilterIcon />} aria-expanded={paneOpen} onClick={onTogglePane} />
        </Tooltip>
        <Tooltip content={expandLabel} relationship="label">
          <Button appearance="subtle" icon={allExpanded ? <CollapseAllIcon /> : <ExpandAllIcon />} onClick={onToggleExpandAll} />
        </Tooltip>
        <Menu checkedValues={{ sort: [sort] }} onCheckedValueChange={(_, d) => d.name === 'sort' && onSort(d.checkedItems[0] === 'asc' ? 'asc' : 'desc')}>
          <MenuTrigger disableButtonEnhancement>
            <Tooltip content={t('record.timeline.more')} relationship="label">
              <Button appearance="subtle" icon={<MoreIcon />} />
            </Tooltip>
          </MenuTrigger>
          <MenuPopover>
            <MenuList>
              <MenuItem icon={<RefreshIcon />} onClick={onRefresh}>
                {t('record.timeline.refresh')}
              </MenuItem>
              <MenuItemRadio name="sort" value="desc" icon={<SortNewestIcon />}>
                {t('record.timeline.sortNewest')}
              </MenuItemRadio>
              <MenuItemRadio name="sort" value="asc" icon={<SortOldestIcon />}>
                {t('record.timeline.sortOldest')}
              </MenuItemRadio>
            </MenuList>
          </MenuPopover>
        </Menu>
      </div>
    </div>
  );
}
