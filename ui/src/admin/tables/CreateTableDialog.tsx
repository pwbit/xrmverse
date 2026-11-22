import { Button, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, Field, Input, Select, Switch } from '@fluentui/react-components';
import { useState } from 'react';
import { useAuth } from '../../shell/auth/AuthProvider';
import { t } from '../../shell/i18n/t';
import { useNotify } from '../../shell/notify/Notifications';
import { SchemaNameFields, useSchemaName } from './SchemaNameFields';
import { ACTIVITY_PRIMARY_NAME, entityBody } from './schemaBodies';
import { logicalNameOf, prefixOf, schemaNameError } from './schemaNames';

/** The primary name column of a new table: `<prefix>_Name`, the prefix being the table's; undefined without a prefix. */
export const defaultPrimarySchemaName = (tableSchemaName: string): string | undefined => {
  const prefix = prefixOf(tableSchemaName);
  return prefix ? `${prefix}_Name` : undefined;
};

/**
 * Creates a table with its primary name column, like `xrmd table create`, from a SchemaName as Dataverse takes it. With
 * "Activity table" (`--activity`), a custom activity: primary name column `Subject`, user owned.
 */
export function CreateTableDialog({ onClose, onCreated }: { onClose: () => void; onCreated: (table: string) => void }) {
  const { api } = useAuth();
  const notify = useNotify();
  const [label, setLabel] = useState('');
  const [plural, setPlural] = useState('');
  const [ownership, setOwnership] = useState<'user' | 'organization'>('user');
  const [primary, setPrimary] = useState('');
  const [activity, setActivity] = useState(false);
  const [busy, setBusy] = useState(false);
  const name = useSchemaName(label);

  const primarySchemaName = activity ? ACTIVITY_PRIMARY_NAME.schemaName : primary.trim() || defaultPrimarySchemaName(name.schemaName) || '';
  const primaryError = !activity && primary.trim() ? schemaNameError(primary) : undefined;
  const complete = name.schemaName && !name.error && label.trim() && plural.trim() && primarySchemaName && !primaryError;

  async function create() {
    if (!api || !complete) return;
    setBusy(true);
    try {
      await api.post(
        'EntityDefinitions',
        entityBody({
          name: name.logicalName,
          schemaName: name.schemaName,
          label: label.trim(),
          pluralLabel: plural.trim(),
          ownership: activity ? 'user' : ownership,
          ...(activity ? { isActivity: true } : {}),
          primaryName: activity
            ? { ...ACTIVITY_PRIMARY_NAME, label: t('table.create.activityPrimaryNameLabel') }
            : { name: logicalNameOf(primarySchemaName), schemaName: primarySchemaName, label: t('table.create.primaryNameLabel') },
        }),
      );
      notify.success(t('table.create.created', { name: name.logicalName }));
      onCreated(name.logicalName);
    } catch (e) {
      notify.error(e);
    } finally {
      setBusy(false);
    }
  }

  return (
    <Dialog open onOpenChange={(_, d) => !d.open && onClose()}>
      <DialogSurface>
        <DialogBody>
          <DialogTitle>{t('table.create.title')}</DialogTitle>
          <DialogContent>
            <Field label={t('table.field.label')} required>
              <Input value={label} onChange={(_, d) => setLabel(d.value)} />
            </Field>
            <Field label={t('table.field.plural')} required>
              <Input value={plural} onChange={(_, d) => setPlural(d.value)} />
            </Field>
            <SchemaNameFields name={name} scope="table" />
            <Field hint={t('table.field.activity.hint')}>
              <Switch label={t('table.field.activity')} checked={activity} onChange={(_, d) => setActivity(d.checked)} />
            </Field>
            <Field label={t('table.field.ownership')}>
              <Select value={activity ? 'user' : ownership} disabled={activity} onChange={(_, d) => setOwnership(d.value as 'user' | 'organization')}>
                <option value="user">{t('table.ownership.user')}</option>
                <option value="organization">{t('table.ownership.organization')}</option>
              </Select>
            </Field>
            <Field
              label={t('table.field.primaryName')}
              hint={t('table.field.primaryName.hint')}
              validationMessage={primaryError}
              validationState={primaryError ? 'error' : undefined}
            >
              <Input
                value={activity ? ACTIVITY_PRIMARY_NAME.schemaName : primary}
                disabled={activity}
                placeholder={defaultPrimarySchemaName(name.schemaName) ?? ''}
                onChange={(_, d) => setPrimary(d.value)}
              />
            </Field>
          </DialogContent>
          <DialogActions>
            <Button appearance="secondary" onClick={onClose}>
              {t('common.cancel')}
            </Button>
            <Button appearance="primary" disabled={!complete || busy} onClick={() => void create()}>
              {t('table.create.submit')}
            </Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}
