import {
  Button, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, Field, Input, makeStyles, MessageBar,
  MessageBarBody, Select, Spinner, tokens,
} from '@fluentui/react-components';
import { useEffect, useMemo, useState } from 'react';
import { t } from '../../shell/i18n/t';
import { DismissIcon, NewIcon, NextPageIcon, PreviousPageIcon } from '../../shell/icons';
import type { AttributeMeta, EntityMeta } from '../tables/metadata';
import { loadEntity, type LookupRelationship } from '../tables/tableApi';
import { useLoad } from '../tables/useLoad';
import { addColumn, moveColumn, removeColumn, setWidth, type DesignColumn, type ViewDesign } from './viewDesign';

const useStyles = makeStyles({
  chips: { display: 'flex', flexWrap: 'wrap', gap: tokens.spacingHorizontalS, listStyle: 'none', margin: 0, padding: 0 },
  chip: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXS,
    padding: tokens.spacingHorizontalS,
    border: `1px solid ${tokens.colorNeutralStroke1}`,
    borderRadius: tokens.borderRadiusMedium,
    minWidth: '160px',
  },
  chipTitle: { fontWeight: tokens.fontWeightSemibold, overflowWrap: 'anywhere' },
  chipActions: { display: 'flex', alignItems: 'center', gap: tokens.spacingHorizontalXS },
  width: { width: '90px' },
  picks: { display: 'flex', flexDirection: 'column', alignItems: 'flex-start', maxHeight: '320px', overflowY: 'auto' },
});

const NOT_IN_VIEWS = new Set(['partylist', 'file', 'image', 'unsupported']);

/** A column a view may show: readable, not hidden, not a companion (`...name`), not the primary id. */
export const viewable = (a: AttributeMeta): boolean =>
  a.validForRead && !a.hidden && !a.attributeOf && !a.isPrimaryId && !NOT_IN_VIEWS.has(a.kind);

const byLabel = (a: AttributeMeta, b: AttributeMeta) => a.displayName.localeCompare(b.displayName);

/**
 * The header of a cell: the column's display name, or for a related column "{column} ({lookup})" with the display name of
 * the table's lookup the link-entity follows (as Dataverse: "Full Name (Primary Contact)"). Metadata not (yet) loaded
 * falls back to logical names.
 */
export function columnHeader(design: ViewDesign, column: DesignColumn, entity: EntityMeta | undefined, related: Record<string, EntityMeta>): string {
  const own = (name: string) => entity?.attributes.find((a) => a.logicalName === name)?.displayName ?? name;
  if (!column.alias) return own(column.attribute);
  const link = design.links.find((l) => l.alias === column.alias);
  const target = link ? related[link.entity] : undefined;
  const label = target?.attributes.find((a) => a.logicalName === column.attribute)?.displayName ?? column.attribute;
  return t('views.columns.related', { column: label, lookup: link ? own(link.to) : column.alias });
}

/** Clamps a typed width as the model does (25–1000, whole pixels). */
const clampWidth = (n: number) => Math.min(1000, Math.max(25, Math.round(n)));

export interface ColumnEditorProps {
  design: ViewDesign;
  entity: EntityMeta;
  /** The table's N:1 lookups (one per lookup and target). */
  lookups: LookupRelationship[];
  /** Related tables' metadata already loaded, by logical name. */
  related: Record<string, EntityMeta>;
  /** A related table's metadata the add dialog loaded, for the page to keep. */
  onRelated: (meta: EntityMeta) => void;
  onChange: (next: ViewDesign) => void;
}

/** The view's columns as chips, left to right: width, move left/right, remove; and "Add column". */
export function ColumnEditor({ design, entity, lookups, related, onRelated, onChange }: ColumnEditorProps) {
  const styles = useStyles();
  const [adding, setAdding] = useState(false);
  const header = (c: DesignColumn) => columnHeader(design, c, entity, related);
  return (
    <>
      {design.columns.length === 0 && <p>{t('views.columns.empty')}</p>}
      <ul className={styles.chips} aria-label={t('views.columns.title')}>
        {design.columns.map((c, i) => (
          <ColumnChip
            key={c.name}
            column={c}
            header={header(c)}
            first={i === 0}
            last={i === design.columns.length - 1}
            onMove={(delta) => onChange(moveColumn(design, c.name, delta))}
            onRemove={() => onChange(removeColumn(design, c.name))}
            onWidth={(w) => onChange(setWidth(design, c.name, w))}
          />
        ))}
      </ul>
      <div>
        <Button icon={<NewIcon />} onClick={() => setAdding(true)}>
          {t('views.columns.add')}
        </Button>
      </div>
      {adding && (
        <AddViewColumnDialog
          design={design}
          entity={entity}
          lookups={lookups}
          related={related}
          onRelated={onRelated}
          onClose={() => setAdding(false)}
          onAdd={(next) => {
            setAdding(false);
            onChange(next);
          }}
        />
      )}
    </>
  );
}

interface ChipProps {
  column: DesignColumn;
  header: string;
  first: boolean;
  last: boolean;
  onMove: (delta: -1 | 1) => void;
  onRemove: () => void;
  onWidth: (width: number) => void;
}

/** One column: its width is typed freely and applied (clamped by the model) on blur or Enter. */
function ColumnChip({ column, header, first, last, onMove, onRemove, onWidth }: ChipProps) {
  const styles = useStyles();
  const [width, setWidthText] = useState(String(column.width));
  useEffect(() => setWidthText(String(column.width)), [column.width]);
  const commit = () => {
    const n = Number(width);
    if (width.trim() === '' || !Number.isFinite(n)) {
      setWidthText(String(column.width));
      return;
    }
    const clamped = clampWidth(n);
    setWidthText(String(clamped));
    if (clamped !== column.width) onWidth(clamped);
  };
  return (
    <li className={styles.chip} aria-label={header}>
      <span className={styles.chipTitle}>{header}</span>
      <div className={styles.chipActions}>
        <Input
          className={styles.width}
          type="number"
          min={25}
          max={1000}
          aria-label={t('views.columns.width', { column: header })}
          value={width}
          onChange={(_, d) => setWidthText(d.value)}
          onBlur={commit}
          onKeyDown={(e) => e.key === 'Enter' && commit()}
        />
        <Button appearance="subtle" icon={<PreviousPageIcon />} aria-label={t('views.columns.moveLeft', { column: header })} disabled={first} onClick={() => onMove(-1)} />
        <Button appearance="subtle" icon={<NextPageIcon />} aria-label={t('views.columns.moveRight', { column: header })} disabled={last} onClick={() => onMove(1)} />
        <Button appearance="subtle" icon={<DismissIcon />} aria-label={t('views.columns.remove', { column: header })} onClick={onRemove} />
      </div>
    </li>
  );
}

interface DialogProps {
  design: ViewDesign;
  entity: EntityMeta;
  lookups: LookupRelationship[];
  related: Record<string, EntityMeta>;
  onRelated: (meta: EntityMeta) => void;
  onClose: () => void;
  onAdd: (next: ViewDesign) => void;
}

const sourceKey = (l: LookupRelationship) => `${l.referencingAttribute}|${l.referencedEntity}`;

/** Picks a column of the table or, through one of its lookups, of the related table (E3). */
function AddViewColumnDialog({ design, entity, lookups, related, onRelated, onClose, onAdd }: DialogProps) {
  const styles = useStyles();
  const [source, setSource] = useState('');
  const [search, setSearch] = useState('');
  const lookup = lookups.find((l) => sourceKey(l) === source);
  const target = lookup?.referencedEntity;
  const loaded = useLoad(target && !related[target] ? target : undefined, (api) => loadEntity(api, target!));
  useEffect(() => {
    if (loaded.data) onRelated(loaded.data);
  }, [loaded.data, onRelated]);
  const targetMeta = target ? related[target] ?? loaded.data : undefined;

  // The lookup's display name; a lookup with several targets (customer, owner) also names the target table.
  const lookupLabel = (l: LookupRelationship) => {
    const lookupName = entity.attributes.find((a) => a.logicalName === l.referencingAttribute)?.displayName ?? l.referencingAttribute;
    const targets = lookups.filter((x) => x.referencingAttribute === l.referencingAttribute).length;
    return targets > 1
      ? t('views.addColumn.viaLookup', { lookup: lookupName, table: related[l.referencedEntity]?.displayName ?? l.referencedEntity })
      : lookupName;
  };

  const picks = useMemo(() => {
    const meta = lookup ? targetMeta : entity;
    if (!meta) return [];
    let taken: Set<string>;
    if (lookup) {
      const link = design.links.find((l) => l.entity === lookup.referencedEntity && l.to === lookup.referencingAttribute && l.from === meta.primaryIdAttribute);
      taken = new Set(design.columns.filter((c) => link && c.alias === link.alias).map((c) => c.attribute));
    } else {
      taken = new Set(design.columns.filter((c) => !c.alias).map((c) => c.attribute));
    }
    const needle = search.trim().toLowerCase();
    return meta.attributes
      .filter((a) => viewable(a) && !taken.has(a.logicalName))
      .filter((a) => !needle || a.displayName.toLowerCase().includes(needle) || a.logicalName.includes(needle))
      .sort(byLabel);
  }, [lookup, targetMeta, entity, design, search]);

  const pick = (a: AttributeMeta) => {
    if (!lookup) onAdd(addColumn(design, a.logicalName));
    else if (targetMeta) {
      onAdd(addColumn(design, a.logicalName, { lookup: lookup.referencingAttribute, entity: lookup.referencedEntity, from: targetMeta.primaryIdAttribute }));
    }
  };

  return (
    <Dialog open onOpenChange={(_, d) => !d.open && onClose()}>
      <DialogSurface>
        <DialogBody>
          <DialogTitle>{t('views.columns.add')}</DialogTitle>
          <DialogContent>
            <Field label={t('views.addColumn.source')}>
              <Select value={source} onChange={(_, d) => setSource(d.value)}>
                <option value="">{t('views.addColumn.thisTable', { table: entity.displayName })}</option>
                {lookups.map((l) => (
                  <option key={sourceKey(l)} value={sourceKey(l)}>
                    {lookupLabel(l)}
                  </option>
                ))}
              </Select>
            </Field>
            <Field label={t('views.addColumn.search')}>
              <Input value={search} onChange={(_, d) => setSearch(d.value)} />
            </Field>
            {loaded.error && (
              <MessageBar intent="error">
                <MessageBarBody>{t('views.addColumn.loadFailed', { detail: loaded.error })}</MessageBarBody>
              </MessageBar>
            )}
            {lookup && !targetMeta && !loaded.error && <Spinner label={t('common.loading')} />}
            {(!lookup || targetMeta) && (
              <div className={styles.picks}>
                {picks.length === 0 && <p>{t('views.addColumn.none')}</p>}
                {picks.map((a) => (
                  <Button key={a.logicalName} appearance="subtle" onClick={() => pick(a)}>
                    {a.displayName}
                  </Button>
                ))}
              </div>
            )}
          </DialogContent>
          <DialogActions>
            <Button onClick={onClose}>{t('common.cancel')}</Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}
