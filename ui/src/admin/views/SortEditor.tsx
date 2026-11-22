import { Button, makeStyles, MessageBar, MessageBarBody, Select, tokens } from '@fluentui/react-components';
import { t } from '../../shell/i18n/t';
import { DismissIcon, NewIcon } from '../../shell/icons';
import type { AttributeMeta } from '../tables/metadata';
import type { DesignCardProps } from './designerSlots';
import type { DesignOrder } from './viewDesign';

const MAX_ORDERS = 2;
const NOT_SORTABLE = new Set(['partylist', 'file', 'image', 'unsupported', 'choices']);

const useStyles = makeStyles({
  row: { display: 'flex', alignItems: 'center', flexWrap: 'wrap', gap: tokens.spacingHorizontalS },
});

const sortable = (a: AttributeMeta) => a.validForRead && !a.hidden && !a.attributeOf && !NOT_SORTABLE.has(a.kind);

/**
 * Up to two orders on the table's own columns (E4). A sort the model cannot show (`ordersEditable` false) is shown as a
 * notice only and kept as it is.
 */
export function SortEditor({ design, attributes, onChange }: DesignCardProps) {
  const styles = useStyles();
  if (!design.ordersEditable) {
    return (
      <MessageBar intent="info">
        <MessageBarBody>{t('views.sort.readOnly')}</MessageBarBody>
      </MessageBar>
    );
  }
  const columns = attributes.filter(sortable).sort((a, b) => a.displayName.localeCompare(b.displayName));
  const label = (name: string) => attributes.find((a) => a.logicalName === name)?.displayName ?? name;
  const set = (orders: DesignOrder[]) => onChange({ ...design, orders });
  const update = (i: number, order: DesignOrder) => set(design.orders.map((o, j) => (j === i ? order : o)));
  const add = () => {
    const used = new Set(design.orders.map((o) => o.attribute));
    const first = [design.primaryName, ...columns.map((c) => c.logicalName)].find((n) => n && !used.has(n) && columns.some((c) => c.logicalName === n));
    if (first) set([...design.orders, { attribute: first, descending: false }]);
  };

  return (
    <>
      {design.orders.length === 0 && <p>{t('views.sort.none')}</p>}
      {design.orders.map((o, i) => {
        const n = i + 1;
        // An order on a column the list lacks (not readable, or unknown) still shows under its own name.
        const options = columns.some((c) => c.logicalName === o.attribute) ? columns : [...columns, { logicalName: o.attribute, displayName: label(o.attribute) }];
        return (
          <div key={i} className={styles.row}>
            <Select aria-label={t('views.sort.column', { n })} value={o.attribute} onChange={(_, d) => update(i, { ...o, attribute: d.value })}>
              {options.map((c) => (
                <option key={c.logicalName} value={c.logicalName} disabled={c.logicalName !== o.attribute && design.orders.some((x) => x.attribute === c.logicalName)}>
                  {c.displayName}
                </option>
              ))}
            </Select>
            <Select
              aria-label={t('views.sort.direction', { n })}
              value={o.descending ? 'desc' : 'asc'}
              onChange={(_, d) => update(i, { ...o, descending: d.value === 'desc' })}
            >
              <option value="asc">{t('views.sort.ascending')}</option>
              <option value="desc">{t('views.sort.descending')}</option>
            </Select>
            <Button appearance="subtle" icon={<DismissIcon />} aria-label={t('views.sort.remove', { n })} onClick={() => set(design.orders.filter((_, j) => j !== i))} />
          </div>
        );
      })}
      <div>
        <Button icon={<NewIcon />} disabled={design.orders.length >= MAX_ORDERS} onClick={add}>
          {t('views.sort.add')}
        </Button>
      </div>
    </>
  );
}
