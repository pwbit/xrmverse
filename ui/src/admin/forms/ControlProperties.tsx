import {
  Field, Input, makeStyles, MessageBar, MessageBarBody, Radio, RadioGroup, Select, Switch, tokens,
} from '@fluentui/react-components';
import { useEffect, useState } from 'react';
import { t } from '../../shell/i18n/t';
import type { AttributeMeta } from '../tables/metadata';
import { loadEntity } from '../tables/tableApi';
import { useLoad, useLoadFor } from '../tables/useLoad';
import { ExtensionNote, MatchingLabel } from './AddControlDialog';
import {
  MAX_RECORDS_PER_PAGE, setControl, validPerPage, type ControlSpec, type QuickViewSpec, type SubgridSpec, type WebResourceSpec,
} from './formControls';
import {
  formXmlId, listPublicViews, listQuickViewForms, listWebResources, sameId, WEB_RESOURCE_TYPES,
} from './formControlsApi';
import type { DesignCell, FormDesign } from './formDesign';
import { LoadFailed } from './LoadFailed';
import { filterProblem, filterSources, filterTargets, placedColumns } from './subgridFilterChoices';

const useStyles = makeStyles({
  hint: { margin: 0, color: tokens.colorNeutralForeground3, overflowWrap: 'anywhere' },
  /** The columns of a subgrid matching a form column, set off under the Records choice. */
  nested: {
    display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalS,
    borderLeft: `${tokens.strokeWidthThickest} solid ${tokens.colorBrandStroke1}`, paddingLeft: tokens.spacingHorizontalL,
  },
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

type RecordsMode = 'related' | 'all' | 'matching';

function SubgridProperties({ design, attributes, cell, spec, onChange, readOnly }: Props<SubgridSpec>) {
  const styles = useStyles();
  const views = useLoadFor(spec.targetEntity || undefined, (api) => listPublicViews(api, spec.targetEntity));
  /** The records per page being typed: applied while it is a whole number in 1–250, shown as typed until the field is left. */
  const [draft, setDraft] = useState<string>();
  /** "Matching" chosen while no filter is set yet: the source picked so far (no target of the table can match it yet). */
  const [pending, setPending] = useState<{ source: string }>();
  const set = (patch: Partial<SubgridSpec>) => onChange(setControl(design, cell.key, { ...spec, ...patch }));
  const known = views.data?.some((v) => sameId(v.id, spec.viewId)) ?? false;
  const perPage = draft ?? String(spec.recordsPerPage);
  const mode: RecordsMode = spec.relationship ? 'related' : spec.filter || pending ? 'matching' : 'all';
  // Read for a subgrid without a relationship, so the columns are there when Matching is chosen.
  const table = useLoadFor(spec.relationship ? undefined : spec.targetEntity || undefined, (api) => loadEntity(api, spec.targetEntity));
  const form = { attributes };
  const sources = filterSources(form, placedColumns(design));
  const sourceName = spec.filter?.source ?? pending?.source ?? '';
  const sourceAttr = sources.find((a) => a.logicalName.toLowerCase() === sourceName.toLowerCase());
  const targets = sourceAttr && table.data ? filterTargets(sourceAttr, table.data) : [];
  const problem = filterProblem(design, spec, form, table.data);
  const nameOf = (list: AttributeMeta[], name: string) =>
    list.find((a) => a.logicalName.toLowerCase() === name.toLowerCase())?.displayName || name;
  const tableName = table.data?.displayName || spec.targetEntity;

  /** Filters by `source` and the first column of the table that can match it, or waits for one when none can. */
  const chooseSource = (source: string) => {
    const attr = sources.find((a) => a.logicalName === source);
    const first = attr && table.data ? filterTargets(attr, table.data)[0] : undefined;
    if (first) {
      setPending(undefined);
      set({ filter: { source, target: first.logicalName } });
    } else {
      setPending({ source });
      if (spec.filter) set({ filter: undefined });
    }
  };
  // A source chosen before the table was read gets its first matching column once it is.
  const firstTarget = pending ? targets[0]?.logicalName : undefined;
  useEffect(() => {
    if (pending && firstTarget) chooseSource(pending.source);
    // chooseSource is recreated on every render; the pending source and its first target are what matter.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [pending, firstTarget]);
  const chooseMode = (next: RecordsMode) => {
    if (next === 'all') {
      setPending(undefined);
      if (spec.filter) set({ filter: undefined });
    } else if (next === 'matching') {
      chooseSource(sources[0]?.logicalName ?? '');
    }
  };

  return (
    <>
      <p className={styles.hint}>
        {spec.relationship
          ? t('forms.props.subgridRelated', { table: spec.targetEntity, relationship: spec.relationship })
          : spec.filter
            ? t('forms.props.subgridMatching', {
              table: tableName, target: nameOf(table.data?.attributes ?? [], spec.filter.target), source: nameOf(attributes, spec.filter.source),
            })
            : pending
              // Matching chosen, no column to match yet: nothing is stored, so a save keeps All records.
              ? t('forms.props.subgridMatchingPending', { table: spec.targetEntity })
              : t('forms.props.subgridAll', { table: spec.targetEntity })}
      </p>
      <Field label={t('forms.controls.records')}>
        <RadioGroup value={mode} disabled={readOnly} onChange={(_, d) => chooseMode(d.value as RecordsMode)}>
          <Radio value="related" label={t('forms.controls.records.related')} disabled={!spec.relationship} />
          <Radio value="all" label={t('forms.controls.records.all')} disabled={!!spec.relationship} />
          <Radio
            value="matching"
            label={<MatchingLabel text={t('forms.controls.records.matchingShort')} />}
            disabled={!!spec.relationship}
          />
        </RadioGroup>
      </Field>
      {mode === 'matching' && (
        <div className={styles.nested}>
          <Field label={t('forms.controls.filterSource')}>
            <Select value={sourceName} disabled={readOnly} onChange={(_, d) => chooseSource(d.value)}>
              {!sourceAttr && <option value={sourceName}>{sourceName ? nameOf(attributes, sourceName) : t('forms.controls.choose')}</option>}
              {sources.map((a) => (
                <option key={a.logicalName} value={a.logicalName}>
                  {a.displayName || a.logicalName}
                </option>
              ))}
            </Select>
          </Field>
          <LoadFailed error={table.error} />
          <Field
            label={t('forms.controls.filterTarget', { table: tableName })}
            hint={t('forms.controls.filterTargetHint', { table: tableName })}
          >
            <Select
              value={spec.filter?.target ?? ''}
              disabled={readOnly || !sourceAttr || !table.data}
              onChange={(_, d) => set({ filter: { source: sourceName, target: d.value } })}
            >
              {!targets.some((a) => a.logicalName === spec.filter?.target) && (
                <option value={spec.filter?.target ?? ''}>
                  {spec.filter ? nameOf(table.data?.attributes ?? [], spec.filter.target) : t('forms.controls.choose')}
                </option>
              )}
              {targets.map((a) => (
                <option key={a.logicalName} value={a.logicalName}>
                  {a.displayName || a.logicalName}
                </option>
              ))}
            </Select>
          </Field>
          {problem && (
            <MessageBar intent="warning">
              <MessageBarBody>{problem}</MessageBarBody>
            </MessageBar>
          )}
          <ExtensionNote />
        </div>
      )}
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
