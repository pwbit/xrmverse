import { Field, makeStyles, MessageBar, MessageBarBody, Select, tokens } from '@fluentui/react-components';
import { useState } from 'react';
import type { NavItem, SubgridControl as SubgridModel } from '../../../model/form';
import { labelFor } from '../../../parsers/formXml';
import { t } from '../../../shell/i18n/t';
import type { ControlEnv } from './controlEnv';
import { SubgridControl } from './SubgridControl';

const useStyles = makeStyles({
  root: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalM },
  chooser: { maxWidth: '24rem' },
});

/** The label of a related-tables item in the user's language, else its id. */
export const navLabel = (item: NavItem, lcid: number) => labelFor(item.labels, lcid) || item.id;

/** The subgrid a related-tables item shows: the relationship only; the table and the view follow from it. */
const relatedSubgrid = (item: Extract<NavItem, { kind: 'relationship' }>): SubgridModel => ({
  kind: 'subgrid',
  id: `related-${item.id}`,
  relationship: item.relationship,
  targetEntity: '',
  viewId: '',
  recordsPerPage: 10,
  enableViewPicker: false,
  related: true,
});

/**
 * The form's Related tab (#120): the items of its `<Navigation>`, one at a time. A relationship item lists the related rows in a
 * subgrid; any other item (a link to a page or a web resource) says that it is not available, instead of being left out.
 */
export function RelatedTab({ items, env }: { items: NavItem[]; env: ControlEnv }) {
  const styles = useStyles();
  const [chosen, setChosen] = useState(items[0]?.id ?? '');
  const item = items.find((i) => i.id === chosen) ?? items[0];
  return (
    <div className={styles.root}>
      <Field label={t('record.related.choose')} className={styles.chooser}>
        <Select value={item?.id ?? ''} onChange={(_, d) => setChosen(d.value)}>
          {items.map((i) => (
            <option key={i.id} value={i.id}>
              {navLabel(i, env.lcid)}
            </option>
          ))}
        </Select>
      </Field>
      {item?.kind === 'relationship' && <SubgridControl key={item.id} control={relatedSubgrid(item)} env={env} />}
      {item?.kind === 'unsupported' && (
        <MessageBar intent="info">
          <MessageBarBody>{t('record.related.unsupported', { label: navLabel(item, env.lcid) })}</MessageBarBody>
        </MessageBar>
      )}
    </div>
  );
}
