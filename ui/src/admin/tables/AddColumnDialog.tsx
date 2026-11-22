import { Button, Checkbox, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, Field, Input, Select } from '@fluentui/react-components';
import { useState } from 'react';
import { useAuth } from '../../shell/auth/AuthProvider';
import { t } from '../../shell/i18n/t';
import { useNotify } from '../../shell/notify/Notifications';
import { columnTypes, maxSizeInKB, type ColumnType } from './columnTypes';
import { SchemaNameFields, useSchemaName } from './SchemaNameFields';
import { attributeBody, customerBody, customerRelationshipName, lookupBody, type ColumnInput } from './schemaBodies';
import { entityPath, loadEntities, loadGlobalChoices } from './tableApi';
import { useLoad } from './useLoad';

/** SchemaDiff.MaxRelationshipName: the longest relationship name the schema accepts. */
const MAX_RELATIONSHIP_NAME = 100;

interface Props {
  table: string;
  onClose: () => void;
  onAdded: () => void;
}

/**
 * Adds a column the way the CLI does: a plain column is `POST .../Attributes`; a lookup is `POST RelationshipDefinitions` with
 * the one-to-many relationship named `{table}_{column}_{target}` (logical names); a customer column is `POST CreateCustomerRelationships`
 * with two (to account and to contact). The column's SchemaName is collected
 * as Dataverse takes it (publisher prefix and casing); its logical name is the SchemaName in lowercase. A global choice and a lookup target are picked from what exists.
 */
export function AddColumnDialog({ table, onClose, onAdded }: Props) {
  const { api } = useAuth();
  const notify = useNotify();
  const [label, setLabel] = useState('');
  const name = useSchemaName(label);
  const [type, setType] = useState<ColumnType>('string');
  const [maxLength, setMaxLength] = useState('');
  const [precision, setPrecision] = useState('');
  const [choice, setChoice] = useState('');
  const [target, setTarget] = useState('');
  const [maxSize, setMaxSize] = useState('');
  const [fullImage, setFullImage] = useState(false);
  const [busy, setBusy] = useState(false);

  const isChoice = type === 'choice' || type === 'choices';
  const choices = useLoad(isChoice ? 'choices' : undefined, (a) => loadGlobalChoices(a));
  const tables = useLoad(type === 'lookup' ? 'tables' : undefined, (a) => loadEntities(a));
  const choiceNames = Object.keys(choices.data ?? {}).sort();

  const blank = (text: string) => text.trim() === '';
  const isCount = (text: string, min: number, max: number) => /^\d+$/.test(text.trim()) && Number(text) >= min && Number(text) <= max;
  const maxLengthError = (type === 'string' || type === 'memo') && !blank(maxLength) && !isCount(maxLength, 1, 1_000_000) ? t('column.err.maxLength') : undefined;
  const precisionError = type === 'decimal' && !blank(precision) && !isCount(precision, 0, 10) ? t('column.err.precision') : undefined;
  // A file or image column takes MaxSizeInKB in Dataverse's range for its type; blank leaves the default to the server.
  const sizeRange = type === 'file' || type === 'image' ? maxSizeInKB[type] : undefined;
  const maxSizeError = sizeRange && !blank(maxSize) && !isCount(maxSize, sizeRange.min, sizeRange.max) ? t('column.err.maxSizeInKB', { min: sizeRange.min, max: sizeRange.max }) : undefined;
  // The CLI's relationship name (SchemaDiff): logical names of the table, the column and the target.
  const relationshipName = `${table}_${name.logicalName}_${target}`;
  // A customer column has two relationships, `..._account` and `..._contact`: the longer one decides.
  const customerNames = ['account', 'contact'].map((c) => customerRelationshipName(table, name.logicalName, c));
  const longest = type === 'customer' ? customerNames.reduce((a, b) => (b.length > a.length ? b : a)) : relationshipName;
  const relationshipError = (type === 'lookup' || type === 'customer') && longest.length > MAX_RELATIONSHIP_NAME ? t('column.err.relationshipName', { name: longest, max: MAX_RELATIONSHIP_NAME }) : undefined;

  async function add() {
    if (!api) return;
    const column: ColumnInput = {
      name: name.logicalName,
      schemaName: name.schemaName,
      type,
      label: label.trim() || name.schemaName,
      maxLength: (type === 'string' || type === 'memo') && !blank(maxLength) ? Number(maxLength) : undefined,
      precision: type === 'decimal' && !blank(precision) ? Number(precision) : undefined,
      choice: isChoice ? choice : undefined,
      target: type === 'lookup' ? target : undefined,
      maxSizeInKB: sizeRange && !blank(maxSize) ? Number(maxSize) : undefined,
      // IsPrimaryImage is not offered: the server makes the first image column of a table primary, and only an update of
      // another image column moves it.
      canStoreFullImage: type === 'image' ? fullImage : undefined,
    };
    setBusy(true);
    try {
      if (type === 'customer') {
        await api.post('CreateCustomerRelationships', customerBody(column, table));
      } else if (type === 'lookup') {
        const referenced = await api.get<{ PrimaryIdAttribute?: string }>(`${entityPath(target)}?$select=PrimaryIdAttribute`);
        if (!referenced.PrimaryIdAttribute) throw new Error(t('column.add.noPrimaryId', { table: target }));
        await api.post('RelationshipDefinitions', lookupBody(column, table, relationshipName, referenced.PrimaryIdAttribute));
      } else {
        await api.post(`${entityPath(table)}/Attributes`, attributeBody(column));
      }
      notify.success(t('column.add.added', { name: column.name }));
      onAdded();
    } catch (e) {
      notify.error(e);
    } finally {
      setBusy(false);
    }
  }

  const complete = name.schemaName && !name.error && !maxLengthError && !precisionError && !maxSizeError && !relationshipError && (!isChoice || choice) && (type !== 'lookup' || target);
  return (
    <Dialog open onOpenChange={(_, d) => !d.open && onClose()}>
      <DialogSurface>
        <DialogBody>
          <DialogTitle>{t('column.add.title', { table })}</DialogTitle>
          <DialogContent>
            <Field label={t('column.field.label')}>
              <Input value={label} onChange={(_, d) => setLabel(d.value)} />
            </Field>
            <SchemaNameFields name={name} scope="column" />
            <Field label={t('column.field.type')} required>
              <Select value={type} onChange={(_, d) => setType(d.value as ColumnType)}>
                {columnTypes.map((c) => (
                  <option key={c} value={c}>
                    {t(`column.type.${c}`)}
                  </option>
                ))}
              </Select>
            </Field>
            {(type === 'string' || type === 'memo') && (
              <Field label={t('column.field.maxLength')} validationMessage={maxLengthError}>
                <Input inputMode="numeric" value={maxLength} onChange={(_, d) => setMaxLength(d.value)} />
              </Field>
            )}
            {type === 'decimal' && (
              <Field label={t('column.field.precision')} validationMessage={precisionError}>
                <Input inputMode="numeric" value={precision} onChange={(_, d) => setPrecision(d.value)} />
              </Field>
            )}
            {sizeRange && (
              <Field
                label={t('column.field.maxSizeInKB')}
                hint={t('column.field.maxSizeInKB.hint', { default: sizeRange.default, min: sizeRange.min, max: sizeRange.max })}
                validationMessage={maxSizeError}
              >
                <Input inputMode="numeric" value={maxSize} onChange={(_, d) => setMaxSize(d.value)} />
              </Field>
            )}
            {type === 'image' && (
              <Field hint={t('column.field.canStoreFullImage.hint')}>
                <Checkbox label={t('column.field.canStoreFullImage')} checked={fullImage} onChange={(_, d) => setFullImage(d.checked === true)} />
              </Field>
            )}
            {isChoice && (
              <Field label={t('column.field.choice')} required validationMessage={choices.error ?? (choices.data && choiceNames.length === 0 ? t('column.field.choice.none') : undefined)}>
                <Select value={choice} onChange={(_, d) => setChoice(d.value)}>
                  <option value="">{t('field.empty')}</option>
                  {choiceNames.map((c) => (
                    <option key={c} value={c}>
                      {c}
                    </option>
                  ))}
                </Select>
              </Field>
            )}
            {type === 'customer' && (
              <Field validationMessage={relationshipError} hint={t('column.field.customer.hint')} />
            )}
            {type === 'lookup' && (
              <Field label={t('column.field.target')} required validationMessage={relationshipError ?? tables.error}>
                <Select value={target} onChange={(_, d) => setTarget(d.value)}>
                  <option value="">{t('field.empty')}</option>
                  {(tables.data ?? [])
                    .map((e) => e.logicalName)
                    .sort()
                    .map((c) => (
                      <option key={c} value={c}>
                        {c}
                      </option>
                    ))}
                </Select>
              </Field>
            )}
          </DialogContent>
          <DialogActions>
            <Button appearance="secondary" onClick={onClose}>
              {t('common.cancel')}
            </Button>
            <Button appearance="primary" disabled={!complete || busy} onClick={() => void add()}>
              {t('column.add.submit')}
            </Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}
