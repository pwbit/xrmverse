import { Field, Input, makeStyles, Select, Switch, tokens } from '@fluentui/react-components';
import { useState } from 'react';
import { t } from '../../shell/i18n/t';
import type { AttributeMeta } from '../tables/metadata';
import { useLoad, useLoadFor } from '../tables/useLoad';
import {
  MAX_RECORDS_PER_PAGE, setControl, validPerPage, type ControlSpec, type QuickViewSpec, type SubgridSpec, type WebResourceSpec,
} from './formControls';
import {
  formXmlId, listPublicViews, listQuickViewForms, listWebResources, sameId, WEB_RESOURCE_TYPES,
} from './formControlsApi';
import type { DesignCell, FormDesign } from './formDesign';
import { LoadFailed } from './LoadFailed';

const useStyles = makeStyles({
  hint: { margin: 0, color: tokens.colorNeutralForeground3, overflowWrap: 'anywhere' },
});

export interface ControlPropertiesProps {
  design: FormDesign;
  attributes: AttributeMeta[];
  /** A cell with a control spec (subgrid, quick view or web resource). */
  cell: DesignCell & { control: ControlSpec };
  onChange: (next: FormDesign) => void;
  readOnly: boolean;
}

/**
 * The properties of a subgrid (G2), quick view (G3) or web resource (G4) cell that its control holds; the label, show label
 * and visible switches are the panel's. Each list is read from the server; one that fails shows the server's text.
 */
export function ControlProperties(props: ControlPropertiesProps) {
  const spec = props.cell.control;
  if (spec.kind === 'subgrid') return <SubgridProperties {...props} spec={spec} />;
  if (spec.kind === 'quickview') return <QuickViewProperties {...props} spec={spec} />;
  return <WebResourceProperties {...props} spec={spec} />;
}

type Props<S> = ControlPropertiesProps & { spec: S };

function SubgridProperties({ design, cell, spec, onChange, readOnly }: Props<SubgridSpec>) {
  const styles = useStyles();
  const views = useLoadFor(spec.targetEntity || undefined, (api) => listPublicViews(api, spec.targetEntity));
  /** The records per page being typed: applied while it is a whole number in 1–250, shown as typed until the field is left. */
  const [draft, setDraft] = useState<string>();
  const set = (patch: Partial<SubgridSpec>) => onChange(setControl(design, cell.key, { ...spec, ...patch }));
  const known = views.data?.some((v) => sameId(v.id, spec.viewId)) ?? false;
  const perPage = draft ?? String(spec.recordsPerPage);
  return (
    <>
      <p className={styles.hint}>
        {spec.relationship
          ? t('forms.props.subgridRelated', { table: spec.targetEntity, relationship: spec.relationship })
          : t('forms.props.subgridAll', { table: spec.targetEntity })}
      </p>
      <LoadFailed error={views.error} />
      <Field label={t('forms.controls.view')}>
        <Select
          value={known ? formXmlId(views.data!.find((v) => sameId(v.id, spec.viewId))!.id) : spec.viewId}
          disabled={readOnly || !views.data}
          onChange={(_, d) => set({ viewId: d.value })}
        >
          {!known && <option value={spec.viewId}>{views.data ? t('forms.props.viewMissing', { id: spec.viewId }) : spec.viewId}</option>}
          {(views.data ?? []).map((v) => (
            <option key={v.id} value={formXmlId(v.id)}>
              {v.name}
            </option>
          ))}
        </Select>
      </Field>
      <Field
        label={t('forms.controls.recordsPerPage')}
        validationMessage={validPerPage(perPage) ? undefined : t('forms.controls.recordsPerPage.invalid', { max: MAX_RECORDS_PER_PAGE })}
      >
        <Input
          type="number"
          min={1}
          max={MAX_RECORDS_PER_PAGE}
          value={perPage}
          disabled={readOnly}
          onChange={(_, d) => {
            setDraft(d.value);
            if (validPerPage(d.value)) set({ recordsPerPage: Number(d.value) });
          }}
          onBlur={() => setDraft(undefined)}
        />
      </Field>
      <Switch
        label={t('forms.props.viewPicker')}
        checked={spec.viewPicker}
        disabled={readOnly}
        onChange={(_, d) => set({ viewPicker: d.checked })}
      />
    </>
  );
}

/** The form picker of one target table of a quick view's lookup. */
function QuickFormPicker({ table, value, canClear, readOnly, onPick }: {
  table: string; value: string | undefined; canClear: boolean; readOnly: boolean; onPick: (id: string | undefined) => void;
}) {
  const forms = useLoadFor(table, (api) => listQuickViewForms(api, table));
  const known = value !== undefined && (forms.data?.some((f) => sameId(f.id, value)) ?? false);
  const NONE = '';
  return (
    <>
      <LoadFailed error={forms.error} />
      <Field label={t('forms.props.formFor', { table })}>
        <Select
          value={value === undefined ? NONE : known ? formXmlId(forms.data!.find((f) => sameId(f.id, value))!.id) : value}
          disabled={readOnly || !forms.data}
          onChange={(_, d) => onPick(d.value === NONE ? undefined : d.value)}
        >
          <option value={NONE} disabled={value !== undefined && !canClear}>
            {t('forms.props.noForm')}
          </option>
          {value !== undefined && !known && <option value={value}>{forms.data ? t('forms.props.formMissing', { id: value }) : value}</option>}
          {(forms.data ?? []).map((f) => (
            <option key={f.id} value={formXmlId(f.id)}>
              {f.name}
            </option>
          ))}
        </Select>
      </Field>
    </>
  );
}

function QuickViewProperties({ design, attributes, cell, spec, onChange, readOnly }: Props<QuickViewSpec>) {
  const styles = useStyles();
  const lookup = attributes.find((a) => a.logicalName.toLowerCase() === spec.lookup.toLowerCase());
  // The lookup's targets, then any table the control names that the metadata does not list.
  const targets = [...(lookup?.targets ?? [])];
  for (const name of Object.keys(spec.forms)) if (!targets.includes(name)) targets.push(name);
  const pick = (table: string, id: string | undefined) => {
    const forms = { ...spec.forms };
    if (id === undefined) delete forms[table];
    else forms[table] = id;
    onChange(setControl(design, cell.key, { ...spec, forms }));
  };
  return (
    <>
      <p className={styles.hint}>{t('forms.props.quickViewLookup', { lookup: lookup?.displayName || spec.lookup })}</p>
      {targets.map((table) => (
        <QuickFormPicker
          key={table}
          table={table}
          value={spec.forms[table]}
          canClear={Object.keys(spec.forms).length > 1}
          readOnly={readOnly}
          onPick={(id) => pick(table, id)}
        />
      ))}
    </>
  );
}

const SCROLLING: WebResourceSpec['scrolling'][] = ['auto', 'yes', 'no'];

function WebResourceProperties({ design, cell, spec, onChange, readOnly }: Props<WebResourceSpec>) {
  const resources = useLoad('webresources', (api) => listWebResources(api, WEB_RESOURCE_TYPES));
  const set = (patch: Partial<WebResourceSpec>) => onChange(setControl(design, cell.key, { ...spec, ...patch }));
  const current = resources.data?.find((r) => (spec.webResourceId ? sameId(r.id, spec.webResourceId) : r.name === spec.url));
  const MISSING = '';
  return (
    <>
      <LoadFailed error={resources.error} />
      <Field label={t('forms.controls.webResource')}>
        <Select
          value={current?.id ?? MISSING}
          disabled={readOnly || !resources.data}
          onChange={(_, d) => {
            const r = resources.data?.find((x) => x.id === d.value);
            if (r) set({ url: r.name, webResourceId: r.id });
          }}
        >
          {!current && <option value={MISSING}>{resources.data ? t('forms.props.webResourceMissing', { name: spec.url }) : spec.url}</option>}
          {(resources.data ?? []).map((r) => (
            <option key={r.id} value={r.id}>
              {r.displayName ? `${r.displayName} (${r.name})` : r.name}
            </option>
          ))}
        </Select>
      </Field>
      <Field label={t('forms.props.data')}>
        <Input value={spec.data} disabled={readOnly} onChange={(_, d) => set({ data: d.value })} />
      </Field>
      <Switch
        label={t('forms.props.passParameters')}
        checked={spec.passParameters}
        disabled={readOnly}
        onChange={(_, d) => set({ passParameters: d.checked })}
      />
      <Switch
        label={t('forms.props.restrictScripting')}
        checked={spec.restrictScripting}
        disabled={readOnly}
        onChange={(_, d) => set({ restrictScripting: d.checked })}
      />
      <Field label={t('forms.props.scrolling')}>
        <Select value={spec.scrolling} disabled={readOnly} onChange={(_, d) => set({ scrolling: d.value as WebResourceSpec['scrolling'] })}>
          {SCROLLING.map((s) => (
            <option key={s} value={s}>
              {t(`forms.props.scrolling.${s}`)}
            </option>
          ))}
        </Select>
      </Field>
      <Switch label={t('forms.props.border')} checked={spec.border} disabled={readOnly} onChange={(_, d) => set({ border: d.checked })} />
    </>
  );
}
