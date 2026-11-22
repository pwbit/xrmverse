import { Field, Input, makeStyles, Select, Switch, tokens } from '@fluentui/react-components';
import { t } from '../../shell/i18n/t';
import { allCells, bodySections } from './formCells';
import {
  FORM_TYPE, setCell, setSection, setTab, setTabLayout, type DesignCell, type DesignSection, type DesignTab, type TabLayout,
} from './formDesign';
import { ControlProperties } from './ControlProperties';
import { canBindPcf, type PcfControlRow } from './formPcf';
import { HandlerList } from './HandlerList';
import { PcfPanel } from './PcfPanel';
import type { Loaded } from '../tables/useLoad';
import { kindLabel, type FormDesignCardProps, type Selection } from './formDesignerSlots';

const useStyles = makeStyles({
  panel: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalS },
  caption: { margin: 0, color: tokens.colorNeutralForeground3, overflowWrap: 'anywhere' },
  hint: { margin: 0 },
  heading: { margin: 0, fontSize: tokens.fontSizeBase300, fontWeight: tokens.fontWeightSemibold },
});

const LAYOUTS: TabLayout[] = ['1', '2-50', '2-67', '3'];
const LAYOUT_OF: Record<string, TabLayout> = { '100%': '1', '50%,50%': '2-50', '67%,33%': '2-67', '33%,33%,34%': '3' };
const LAYOUT_LABELS: Record<TabLayout, Parameters<typeof t>[0]> = {
  '1': 'forms.props.layout.1',
  '2-50': 'forms.props.layout.2-50',
  '2-67': 'forms.props.layout.2-67',
  '3': 'forms.props.layout.3',
};
const OTHER = 'other';

function requirement(level: string | undefined): string {
  if (level === 'ApplicationRequired' || level === 'SystemRequired') return t('forms.props.required');
  if (level === 'Recommended') return t('forms.props.recommended');
  return t('forms.props.optional');
}

export interface FormPropertiesPanelProps extends FormDesignCardProps {
  selection?: Selection;
  readOnly: boolean;
  /** The `customcontrols` rows, for the custom control of a field or subgrid cell (G5). */
  customControls: Loaded<PcfControlRow[]>;
}

/**
 * The properties of the selected tab, section, field (F3, F4, F6) or subgrid, quick view or web resource (G2–G4): only
 * labels change, never names (F5). A field or subgrid can use a custom control (G5). Other controls (an IFRAME, the
 * timeline, a custom or unknown control) are not configured.
 */
export function FormPropertiesPanel({ design, attributes, onChange, selection, readOnly, customControls }: FormPropertiesPanelProps) {
  const styles = useStyles();
  const lcid = design.lcid;
  const sections = bodySections(design.tabs);
  let tab: DesignTab | undefined;
  let section: DesignSection | undefined;
  let cell: DesignCell | undefined;
  if (selection?.kind === 'tab') tab = design.tabs.find((x) => x.key === selection.key);
  if (selection?.kind === 'section') section = sections.find((s) => s.key === selection.key);
  if (selection?.kind === 'cell') cell = allCells(design).find((c) => c.key === selection.key);
  const item = tab ?? section ?? cell;
  if (!item) return <p className={styles.hint}>{t('forms.props.none')}</p>;

  const patch = (p: { label?: string; showLabel?: boolean; visible?: boolean }) => {
    if (tab) onChange(setTab(design, tab.key, p));
    else if (section) onChange(setSection(design, section.key, p));
    else if (cell) onChange(setCell(design, cell.key, p));
  };

  let caption: string;
  if (tab) caption = t('forms.props.tabName', { name: tab.name });
  else if (section) caption = t('forms.props.sectionName', { name: section.name });
  else if (cell!.kind === 'field') caption = t('forms.props.fieldName', { name: cell!.attribute ?? '' });
  else if (cell!.control) caption = t('forms.props.controlName', { kind: kindLabel(cell!.kind), id: cell!.control.controlId });
  else caption = kindLabel(cell!.kind);

  if (cell && cell.kind !== 'field' && !cell.control) {
    return (
      <div className={styles.panel}>
        <p className={styles.caption}>{caption}</p>
        <p className={styles.hint}>{t('forms.props.control', { kind: kindLabel(cell.kind) })}</p>
      </div>
    );
  }

  const layout: string | undefined = tab ? LAYOUT_OF[tab.columns.map((c) => c.width).join(',')] ?? OTHER : undefined;
  const attr = cell ? attributes.find((a) => a.logicalName.toLowerCase() === cell!.attribute?.toLowerCase()) : undefined;

  return (
    <div className={styles.panel}>
      <p className={styles.caption}>{caption}</p>
      <Field label={t('forms.props.label')}>
        <Input value={item.labels[lcid] ?? ''} disabled={readOnly} onChange={(_, d) => patch({ label: d.value })} />
      </Field>
      <Switch
        label={t('forms.props.showLabel')}
        checked={item.showLabel}
        disabled={readOnly}
        onChange={(_, d) => patch({ showLabel: d.checked })}
      />
      <Switch label={t('forms.props.visible')} checked={item.visible} disabled={readOnly} onChange={(_, d) => patch({ visible: d.checked })} />
      {tab && (
        <Field label={t('forms.props.layout')}>
          <Select value={layout} disabled={readOnly} onChange={(_, d) => d.value !== OTHER && onChange(setTabLayout(design, tab!.key, d.value as TabLayout))}>
            {LAYOUTS.map((l) => (
              <option key={l} value={l}>
                {t(LAYOUT_LABELS[l])}
              </option>
            ))}
            {layout === OTHER && (
              <option value={OTHER} disabled>
                {t('forms.props.layout.other', { widths: tab.columns.map((c) => c.width).join(' / ') })}
              </option>
            )}
          </Select>
        </Field>
      )}
      {section && (
        <Field label={t('forms.props.columns')}>
          <Select
            value={String(section.columns)}
            disabled={readOnly}
            onChange={(_, d) => onChange(setSection(design, section!.key, { columns: Number(d.value) as 1 | 2 | 3 | 4 }))}
          >
            {[1, 2, 3, 4].map((n) => (
              <option key={n} value={String(n)}>
                {n}
              </option>
            ))}
          </Select>
        </Field>
      )}
      {section && (
        <Field label={t('forms.props.labelPosition')}>
          <Select
            value={section.labelPosition ?? 'left'}
            disabled={readOnly}
            onChange={(_, d) => onChange(setSection(design, section!.key, { labelPosition: d.value as 'left' | 'top' }))}
          >
            <option value="left">{t('forms.props.labelPosition.left')}</option>
            <option value="top">{t('forms.props.labelPosition.top')}</option>
          </Select>
        </Field>
      )}
      {cell?.control && (
        <ControlProperties
          key={cell.key}
          design={design}
          attributes={attributes}
          cell={{ ...cell, control: cell.control }}
          onChange={onChange}
          readOnly={readOnly}
        />
      )}
      {cell?.kind === 'field' && (
        <>
          <Switch
            label={t('forms.props.readOnly')}
            checked={cell.disabled}
            disabled={readOnly}
            onChange={(_, d) => onChange(setCell(design, cell!.key, { disabled: d.checked }))}
          />
          <p className={styles.hint}>{t('forms.props.requirement', { level: requirement(attr?.requiredLevel) })}</p>
          {/* A Quick view form has no events (Dataverse offers none there). */}
          {cell.attribute && design.type !== FORM_TYPE.quickView && (
            <>
              <h4 className={styles.heading}>{t('forms.events.onchange')}</h4>
              <HandlerList
                key={`onchange-${cell.key}`}
                design={design}
                onChange={onChange}
                readOnly={readOnly}
                event="onchange"
                attribute={cell.attribute}
                label={t('forms.events.onchange')}
              />
            </>
          )}
        </>
      )}
      {cell && canBindPcf(cell) && (
        <PcfPanel
          key={`pcf-${cell.key}`}
          design={design}
          attributes={attributes}
          cell={cell}
          onChange={onChange}
          readOnly={readOnly}
          controls={customControls}
        />
      )}
    </div>
  );
}
