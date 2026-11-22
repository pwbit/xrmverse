import { Field, Text } from '@fluentui/react-components';
import { t } from '../../../shell/i18n/t';
import { PARTY_TYPE_OF, partiesOf, partyText, type PartyRow } from '../partyLists';
import type { FieldProps } from './fieldProps';
import { UnsupportedField } from './UnsupportedField';

/**
 * A party list of an activity (`to`, `from`, ...), read-only: the parties of its participation type among the rows of the
 * record's party navigation (plan 2.11, A8). Nothing is ever sent for it from the generic form.
 */
export function PartyListField(p: FieldProps) {
  const { attr, context } = p;
  if (PARTY_TYPE_OF[attr.logicalName] === undefined || (context.record && !context.partyNavigation)) return <UnsupportedField {...p} />;
  const raw = context.record && context.partyNavigation ? context.record.data[context.partyNavigation] : undefined;
  const rows = partiesOf(attr.logicalName, Array.isArray(raw) ? (raw as PartyRow[]) : []);
  return (
    <Field label={attr.displayName} hint={t('form.partyList.readOnly')}>
      <div role="group" aria-label={attr.displayName}>
        {rows.length === 0 ? (
          <Text>{t('form.partyList.none')}</Text>
        ) : (
          <ul>
            {rows.map((row, i) => (
              <li key={i}>{partyText(row, t('form.partyList.unknown'))}</li>
            ))}
          </ul>
        )}
      </div>
    </Field>
  );
}
