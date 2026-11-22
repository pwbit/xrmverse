import {
  Button, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, Field, Input, makeStyles,
} from '@fluentui/react-components';
import { useMemo, useState } from 'react';
import { t } from '../../shell/i18n/t';
import { isSystemColumn, type AttributeMeta } from '../tables/metadata';
import { designable } from './formDesign';

const useStyles = makeStyles({
  picks: { display: 'flex', flexDirection: 'column', alignItems: 'flex-start', maxHeight: '320px', overflowY: 'auto' },
});

/** A column the header may show (ruling F8): one the designer can place, readable, not hidden, not a companion. */
export const headerPickable = (a: AttributeMeta): boolean => designable(a) && a.validForRead && !a.hidden && !a.attributeOf;

/** A column the body may show (ruling F6): as for the header, and not system-only unless it is the primary name. */
export const bodyPickable = (a: AttributeMeta): boolean =>
  headerPickable(a) && (!isSystemColumn(a.logicalName) || a.isPrimaryName);

export interface FieldPickerProps {
  /** The columns offered, already filtered; sorted here by display name. */
  attributes: AttributeMeta[];
  onPick: (attribute: AttributeMeta) => void;
  onClose: () => void;
}

/** Picks a column to place as a field, searching by display or logical name. */
export function FieldPicker({ attributes, onPick, onClose }: FieldPickerProps) {
  const styles = useStyles();
  const [search, setSearch] = useState('');
  const picks = useMemo(() => {
    const needle = search.trim().toLowerCase();
    return attributes
      .filter((a) => !needle || a.displayName.toLowerCase().includes(needle) || a.logicalName.toLowerCase().includes(needle))
      .sort((a, b) => (a.displayName || a.logicalName).localeCompare(b.displayName || b.logicalName));
  }, [attributes, search]);
  return (
    <Dialog open onOpenChange={(_, d) => !d.open && onClose()}>
      <DialogSurface>
        <DialogBody>
          <DialogTitle>{t('forms.picker.title')}</DialogTitle>
          <DialogContent>
            <Field label={t('forms.picker.search')}>
              <Input value={search} onChange={(_, d) => setSearch(d.value)} />
            </Field>
            <div className={styles.picks}>
              {picks.length === 0 && <p>{t('forms.picker.none')}</p>}
              {picks.map((a) => (
                <Button key={a.logicalName} appearance="subtle" onClick={() => onPick(a)}>
                  {a.displayName || a.logicalName}
                </Button>
              ))}
            </div>
          </DialogContent>
          <DialogActions>
            <Button onClick={onClose}>{t('common.cancel')}</Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}
