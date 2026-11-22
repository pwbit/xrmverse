import {
  Button, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, Field, Input, makeStyles, Radio,
  RadioGroup, Select, tokens,
} from '@fluentui/react-components';
import { useState, type ReactNode } from 'react';
import { t } from '../../shell/i18n/t';
import type { AttributeMeta } from '../tables/metadata';
import { loadEntities, loadRelationships } from '../tables/tableApi';
import { useLoad, useLoadFor } from '../tables/useLoad';
import { addQuickView, addSubgrid, addWebResource, MAX_RECORDS_PER_PAGE, validPerPage } from './formControls';
import {
  formXmlId, listPublicViews, listQuickViewForms, listWebResources, subgridSources, WEB_RESOURCE_TYPES,
} from './formControlsApi';
import type { FormDesign } from './formDesign';
import { LoadFailed } from './LoadFailed';

const useStyles = makeStyles({
  content: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalS },
  hint: { margin: 0, color: tokens.colorNeutralForeground3 },
});

export type AddControlKind = 'subgrid' | 'quickview' | 'webresource';

export interface AddControlDialogProps {
  kind: AddControlKind;
  design: FormDesign;
  attributes: AttributeMeta[];
  sectionKey: string;
  sectionLabel: string;
  /** The design with the control added at the end of the section. */
  onAdd: (next: FormDesign) => void;
  onClose: () => void;
}

const TITLES: Record<AddControlKind, Parameters<typeof t>[0]> = {
  subgrid: 'forms.controls.subgridTitle',
  quickview: 'forms.controls.quickViewTitle',
  webresource: 'forms.controls.webResourceTitle',
};

/**
 * Adds a subgrid (G2), a quick view (G3) or a web resource (G4) at the end of a section. Each list is read from the server;
 * one that fails shows the server's text and Add stays disabled.
 */
export function AddControlDialog(props: AddControlDialogProps) {
  if (props.kind === 'subgrid') return <SubgridDialog {...props} />;
  if (props.kind === 'quickview') return <QuickViewDialog {...props} />;
  return <WebResourceDialog {...props} />;
}

/** The dialog around a kind's fields; `next` is the design with the control added, undefined while something is missing. */
function Shell({ kind, sectionLabel, onAdd, onClose, next, children }: AddControlDialogProps & { next?: FormDesign; children: ReactNode }) {
  const styles = useStyles();
  return (
    <Dialog open onOpenChange={(_, d) => !d.open && onClose()}>
      <DialogSurface>
        <DialogBody>
          <DialogTitle>{t(TITLES[kind], { section: sectionLabel })}</DialogTitle>
          <DialogContent>
            <div className={styles.content}>{children}</div>
          </DialogContent>
          <DialogActions>
            <Button appearance="primary" disabled={!next} onClick={() => next && onAdd(next)}>
              {t('forms.controls.add')}
            </Button>
            <Button onClick={onClose}>{t('common.cancel')}</Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}

/** A label the maker has not typed follows the choice made (the table, lookup or web resource chosen). */
function useLabel(): [string, (typed: string) => void, (suggested: string) => void] {
  const [label, setLabel] = useState('');
  const [typed, setTyped] = useState(false);
  return [
    label,
    (value) => {
      setTyped(true);
      setLabel(value);
    },
    (suggested) => {
      if (!typed) setLabel(suggested);
    },
  ];
}

function SubgridDialog(props: AddControlDialogProps) {
  const { design, sectionKey } = props;
  const styles = useStyles();
  const [mode, setMode] = useState<'related' | 'all'>('related');
  const [relationship, setRelationship] = useState('');
  const [table, setTable] = useState('');
  const [viewId, setViewId] = useState<string>();
  const [perPage, setPerPage] = useState('4');
  const [label, typeLabel, suggestLabel] = useLabel();
  const rels = useLoad(mode === 'related' ? 'relationships' : undefined, (api) => loadRelationships(api));
  const entities = useLoad('entities', (api) => loadEntities(api));
  const sources = rels.data ? subgridSources(rels.data, design.table) : [];
  const source = sources.find((s) => s.relationship === relationship);
  const target = mode === 'related' ? source?.target ?? '' : table;
  const views = useLoadFor(target || undefined, (api) => listPublicViews(api, target));
  const tables = [...(entities.data ?? [])].sort((a, b) => (a.displayName || a.logicalName).localeCompare(b.displayName || b.logicalName));
  const displayOf = (name: string) => entities.data?.find((e) => e.logicalName === name)?.displayName || name;
  const view = viewId ?? (views.data?.find((v) => v.isDefault) ?? views.data?.[0])?.id ?? '';
  const failed = mode === 'related' ? rels.error : entities.error;
  const ready = !failed && !views.error && !views.loading && target !== '' && view !== '' && validPerPage(perPage) && label.trim() !== '';
  const next = ready
    ? addSubgrid(design, sectionKey, {
      kind: 'subgrid', controlId: '', relationship: mode === 'related' ? relationship : '', targetEntity: target,
      viewId: formXmlId(view), recordsPerPage: Number(perPage), viewPicker: false,
    }, label.trim())
    : undefined;

  const chooseTarget = (name: string) => {
    setViewId(undefined);
    if (name) suggestLabel(displayOf(name));
  };

  return (
    <Shell {...props} next={next}>
      <Field label={t('forms.controls.records')}>
        <RadioGroup value={mode} onChange={(_, d) => {
          setMode(d.value as 'related' | 'all');
          setViewId(undefined);
        }}>
          <Radio value="related" label={t('forms.controls.records.related')} />
          <Radio value="all" label={t('forms.controls.records.all')} />
        </RadioGroup>
      </Field>
      <LoadFailed error={failed} />
      {mode === 'related' ? (
        <Field label={t('forms.controls.relationship')}>
          <Select value={relationship} disabled={rels.data === undefined} onChange={(_, d) => {
            setRelationship(d.value);
            chooseTarget(sources.find((s) => s.relationship === d.value)?.target ?? '');
          }}>
            <option value="">{t('forms.controls.choose')}</option>
            {sources.map((s) => (
              <option key={s.relationship} value={s.relationship}>
                {t('forms.controls.relationshipOption', { table: displayOf(s.target), relationship: s.relationship })}
              </option>
            ))}
          </Select>
        </Field>
      ) : (
        <Field label={t('forms.controls.table')}>
          <Select value={table} disabled={entities.data === undefined} onChange={(_, d) => {
            setTable(d.value);
            chooseTarget(d.value);
          }}>
            <option value="">{t('forms.controls.choose')}</option>
            {tables.map((e) => (
              <option key={e.logicalName} value={e.logicalName}>
                {e.displayName || e.logicalName}
              </option>
            ))}
          </Select>
        </Field>
      )}
      {mode === 'related' && rels.data && sources.length === 0 && <p className={styles.hint}>{t('forms.controls.noRelationships')}</p>}
      {target !== '' && (
        <>
          <LoadFailed error={views.error} />
          <Field label={t('forms.controls.view')}>
            <Select value={view} disabled={!views.data} onChange={(_, d) => setViewId(d.value)}>
              {(views.data ?? []).map((v) => (
                <option key={v.id} value={v.id}>
                  {v.name}
                </option>
              ))}
            </Select>
          </Field>
          {views.data && views.data.length === 0 && <p className={styles.hint}>{t('forms.controls.noViews')}</p>}
        </>
      )}
      <Field
        label={t('forms.controls.recordsPerPage')}
        validationMessage={validPerPage(perPage) ? undefined : t('forms.controls.recordsPerPage.invalid', { max: MAX_RECORDS_PER_PAGE })}
      >
        <Input type="number" min={1} max={MAX_RECORDS_PER_PAGE} value={perPage} onChange={(_, d) => setPerPage(d.value)} />
      </Field>
      <Field label={t('forms.controls.label')} required>
        <Input value={label} onChange={(_, d) => typeLabel(d.value)} />
      </Field>
    </Shell>
  );
}

/** The lookup columns of the table a quick view can show: readable, with at least one target. */
export const quickViewLookups = (attributes: AttributeMeta[]): AttributeMeta[] =>
  attributes
    .filter((a) => a.kind === 'lookup' && a.targets.length > 0 && a.validForRead && !a.attributeOf && !a.hidden)
    .sort((a, b) => (a.displayName || a.logicalName).localeCompare(b.displayName || b.logicalName));

function QuickViewDialog(props: AddControlDialogProps) {
  const { design, attributes, sectionKey } = props;
  const styles = useStyles();
  const [lookup, setLookup] = useState('');
  const [target, setTarget] = useState('');
  const [formId, setFormId] = useState<string>();
  const [label, typeLabel, suggestLabel] = useLabel();
  const lookups = quickViewLookups(attributes);
  const attr = lookups.find((a) => a.logicalName === lookup);
  const forms = useLoadFor(target || undefined, (api) => listQuickViewForms(api, target));
  const form = formId ?? forms.data?.[0]?.id ?? '';
  const ready = !forms.error && !forms.loading && lookup !== '' && target !== '' && form !== '' && label.trim() !== '';
  const next = ready
    ? addQuickView(design, sectionKey, { kind: 'quickview', controlId: '', lookup, forms: { [target]: formXmlId(form) } }, label.trim())
    : undefined;

  return (
    <Shell {...props} next={next}>
      {lookups.length === 0 && <p className={styles.hint}>{t('forms.controls.noLookups')}</p>}
      <Field label={t('forms.controls.lookup')}>
        <Select value={lookup} disabled={lookups.length === 0} onChange={(_, d) => {
          const chosen = lookups.find((a) => a.logicalName === d.value);
          setLookup(d.value);
          setTarget(chosen?.targets[0] ?? '');
          setFormId(undefined);
          if (chosen) suggestLabel(chosen.displayName || chosen.logicalName);
        }}>
          <option value="">{t('forms.controls.choose')}</option>
          {lookups.map((a) => (
            <option key={a.logicalName} value={a.logicalName}>
              {a.displayName || a.logicalName}
            </option>
          ))}
        </Select>
      </Field>
      {attr && (
        <Field label={t('forms.controls.target')}>
          <Select value={target} onChange={(_, d) => {
            setTarget(d.value);
            setFormId(undefined);
          }}>
            {attr.targets.map((name) => (
              <option key={name} value={name}>
                {name}
              </option>
            ))}
          </Select>
        </Field>
      )}
      {target !== '' && (
        <>
          <LoadFailed error={forms.error} />
          <Field label={t('forms.controls.form')}>
            <Select value={form} disabled={!forms.data} onChange={(_, d) => setFormId(d.value)}>
              {(forms.data ?? []).map((f) => (
                <option key={f.id} value={f.id}>
                  {f.name}
                </option>
              ))}
            </Select>
          </Field>
          {forms.data && forms.data.length === 0 && <p className={styles.hint}>{t('forms.controls.noForms')}</p>}
        </>
      )}
      <Field label={t('forms.controls.label')} required>
        <Input value={label} onChange={(_, d) => typeLabel(d.value)} />
      </Field>
    </Shell>
  );
}

function WebResourceDialog(props: AddControlDialogProps) {
  const { design, sectionKey } = props;
  const styles = useStyles();
  const [id, setId] = useState('');
  const [label, typeLabel, suggestLabel] = useLabel();
  const resources = useLoad('webresources', (api) => listWebResources(api, WEB_RESOURCE_TYPES));
  const chosen = resources.data?.find((r) => r.id === id);
  const ready = !resources.error && chosen !== undefined && label.trim() !== '';
  const next = ready
    ? addWebResource(design, sectionKey, {
      kind: 'webresource', controlId: '', url: chosen.name, webResourceId: chosen.id, data: '', passParameters: false,
      restrictScripting: true, scrolling: 'auto', border: true,
    }, label.trim())
    : undefined;

  return (
    <Shell {...props} next={next}>
      <LoadFailed error={resources.error} />
      <Field label={t('forms.controls.webResource')}>
        <Select value={id} disabled={!resources.data} onChange={(_, d) => {
          setId(d.value);
          const r = resources.data?.find((x) => x.id === d.value);
          if (r) suggestLabel(r.displayName || r.name);
        }}>
          <option value="">{t('forms.controls.choose')}</option>
          {(resources.data ?? []).map((r) => (
            <option key={r.id} value={r.id}>
              {r.displayName ? `${r.displayName} (${r.name})` : r.name}
            </option>
          ))}
        </Select>
      </Field>
      {resources.data && resources.data.length === 0 && <p className={styles.hint}>{t('forms.controls.noWebResources')}</p>}
      <Field label={t('forms.controls.label')} required>
        <Input value={label} onChange={(_, d) => typeLabel(d.value)} />
      </Field>
    </Shell>
  );
}
