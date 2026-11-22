import { Button, Field, Input, makeStyles, MessageBar, MessageBarBody, Select, tokens } from '@fluentui/react-components';
import type { CustomControlParameter } from '../../model/form';
import { manifestTypeOf } from '../../pcf/context';
import type { PcfManifest, PcfProperty } from '../../pcf/manifest';
import { loadStrings } from '../../pcf/resx';
import { t } from '../../shell/i18n/t';
import type { AttributeMeta } from '../tables/metadata';
import { useLoadFor, type Loaded } from '../tables/useLoad';
import { lower } from './formCells';
import type { DesignCell, FormDesign } from './formDesign';
import {
  installedName, missingRequired, newPcfBinding, pcfChoices, pcfFits, pcfRow, setPcf, type PcfBinding, type PcfControlRow,
} from './formPcf';
import { LoadFailed } from './LoadFailed';

const useStyles = makeStyles({
  heading: { margin: 0, fontSize: tokens.fontSizeBase300, fontWeight: tokens.fontWeightSemibold },
  hint: { margin: 0, color: tokens.colorNeutralForeground3, overflowWrap: 'anywhere' },
  list: { margin: 0, paddingLeft: tokens.spacingHorizontalL, overflowWrap: 'anywhere' },
  start: { alignSelf: 'flex-start' },
});

export interface PcfPanelProps {
  design: FormDesign;
  attributes: AttributeMeta[];
  /** A field cell with a column, or a subgrid cell. */
  cell: DesignCell;
  onChange: (next: FormDesign) => void;
  readOnly: boolean;
  /** The `customcontrols` rows, as the designer page loads them. */
  controls: Loaded<PcfControlRow[]>;
}

const STANDARD = '';

/** The table's columns a `bound` property can take: those whose manifest type is one of the property's. */
function columnsFor(p: PcfProperty, attributes: AttributeMeta[]): AttributeMeta[] {
  const types = new Set(p.types.map(lower));
  return attributes
    .filter((a) => !a.attributeOf && !a.hidden && a.validForRead && types.has(lower(manifestTypeOf(a))))
    .sort((a, b) => (a.displayName || a.logicalName).localeCompare(b.displayName || b.logicalName));
}

/**
 * The custom (PCF) control of a field or subgrid cell (G5): the standard control or one of the installed controls that
 * fit the cell ({@link pcfChoices}), then a value for each property of its manifest. The bound control is matched ignoring
 * case and shown with its installed spelling (H3). A binding to a control that is not installed, whose manifest does not
 * parse, or that does not fit the cell (H4) is shown read-only with the reason; it can only be removed.
 */
export function PcfPanel({ design, attributes, cell, onChange, readOnly, controls }: PcfPanelProps) {
  const styles = useStyles();
  const binding = cell.pcf;
  const rows = controls.data;
  const attr = cell.kind === 'field' ? attributes.find((a) => lower(a.logicalName) === lower(cell.attribute ?? '')) : undefined;
  const choices = rows ? pcfChoices(rows, cell, attr) : [];
  const row = binding && rows ? pcfRow(rows, binding.name) : undefined;
  const manifest = row?.manifest;
  /** The control's resx strings, for the display names of its properties (A3). */
  const strings = useLoadFor(manifest ? `strings:${row!.name}` : undefined, () => loadStrings(row!.name, manifest!, design.lcid));
  /** Why the binding cannot be edited here: not installed, a manifest that failed, or a control unfit for the cell (H4). */
  const unavailable = binding && rows && !pcfFits(rows, cell, attr)
    ? row?.error ?? (row ? t('forms.pcf.unfit') : t('forms.pcf.notInstalled'))
    : undefined;
  const set = (next: PcfBinding | undefined) => onChange(setPcf(design, cell.key, next));
  const useStandard = (
    <Button className={styles.start} disabled={readOnly} onClick={() => set(undefined)}>
      {t('forms.pcf.useStandard')}
    </Button>
  );

  if (binding && unavailable !== undefined) {
    return (
      <>
        <h4 className={styles.heading}>{t('forms.pcf.control')}</h4>
        <MessageBar intent="warning">
          <MessageBarBody>
            {t('forms.pcf.unavailable', { name: binding.name, reason: unavailable })}
          </MessageBarBody>
        </MessageBar>
        {Object.keys(binding.parameters).length > 0 && (
          <ul className={styles.list}>
            {Object.entries(binding.parameters).map(([name, p]) => (
              <li key={name}>{t('forms.pcf.parameter', { name, value: p.value })}</li>
            ))}
          </ul>
        )}
        {useStandard}
      </>
    );
  }

  const pick = (name: string) => {
    if (name === STANDARD) return set(undefined);
    const choice = choices.find((c) => c.name === name);
    if (choice) set(newPcfBinding(choice.name, choice.manifest, cell));
  };
  // The installed spelling of the bound control (H3); the binding keeps its own until another control is picked.
  const shown = binding ? (rows ? installedName(rows, binding.name) : undefined) ?? binding.name : STANDARD;
  const offered = binding && !choices.some((c) => c.name === shown);

  return (
    <>
      <LoadFailed error={controls.error} />
      <Field label={t('forms.pcf.control')}>
        <Select value={shown} disabled={readOnly || !rows} onChange={(_, d) => pick(d.value)}>
          <option value={STANDARD}>{t('forms.pcf.standard')}</option>
          {binding && offered && <option value={shown}>{shown}</option>}
          {choices.map((c) => (
            <option key={c.name} value={c.name}>
              {c.name}
            </option>
          ))}
        </Select>
      </Field>
      {binding && (
        <>
          {binding.ownFactors && <p className={styles.hint}>{t('forms.pcf.ownFactors')}</p>}
          {manifest && (
            <Properties
              binding={binding}
              manifest={manifest}
              strings={strings.data ?? {}}
              attributes={attributes}
              readOnly={readOnly}
              onChange={set}
            />
          )}
          {useStandard}
        </>
      )}
    </>
  );
}

function Properties({ binding, manifest, strings, attributes, readOnly, onChange }: {
  binding: PcfBinding;
  manifest: PcfManifest;
  /** The control's resx strings (ruling R6). */
  strings: Record<string, string>;
  attributes: AttributeMeta[];
  readOnly: boolean;
  onChange: (b: PcfBinding) => void;
}) {
  const styles = useStyles();
  const missing = new Set(missingRequired(binding, manifest));
  const properties = manifest.properties.filter((p) => p.usage !== 'output' && p.name !== '');
  if (properties.length === 0) return null;
  const put = (name: string, p: CustomControlParameter | undefined) => {
    const parameters = { ...binding.parameters };
    if (p) parameters[name] = p;
    else delete parameters[name];
    onChange({ ...binding, parameters });
  };
  return (
    <>
      <h4 className={styles.heading}>{t('forms.pcf.properties')}</h4>
      {properties.map((p) => {
        const current = Object.hasOwn(binding.parameters, p.name) ? binding.parameters[p.name] : undefined;
        const validation = missing.has(p.name) ? t('forms.pcf.propertyRequired') : undefined;
        // A3: the display name from the resx, else its key as the runtime shows it, else the name; the name is the hint.
        const key = p.displayNameKey;
        const label = key !== undefined && Object.hasOwn(strings, key) ? strings[key] : (key ?? p.name);
        const hint = label !== p.name ? p.name : undefined;
        if (p.usage === 'bound') {
          const columns = columnsFor(p, attributes);
          const value = current?.value ?? '';
          const listed = value === '' || (!current?.static && columns.some((a) => a.logicalName === value));
          return (
            <Field key={p.name} label={label} hint={hint} required={p.required} validationMessage={validation}>
              <Select
                value={value}
                disabled={readOnly}
                onChange={(_, d) => put(p.name, d.value === '' ? undefined : { value: d.value, static: false })}
              >
                <option value="">{t('forms.pcf.noColumn')}</option>
                {!listed && <option value={value}>{current?.static ? t('forms.pcf.literal', { value }) : value}</option>}
                {columns.map((a) => (
                  <option key={a.logicalName} value={a.logicalName}>
                    {a.displayName || a.logicalName}
                  </option>
                ))}
              </Select>
            </Field>
          );
        }
        const type = current?.type ?? (p.ofType || p.types[0]);
        return (
          <Field key={p.name} label={label} hint={hint} required={p.required} validationMessage={validation}>
            <Input
              value={current?.value ?? ''}
              disabled={readOnly}
              onChange={(_, d) => put(p.name, d.value === '' ? undefined : { value: d.value, static: true, ...(type ? { type } : {}) })}
            />
          </Field>
        );
      })}
    </>
  );
}
