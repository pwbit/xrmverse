import { Button, makeStyles, mergeClasses, Tab, TabList, tokens } from '@fluentui/react-components';
import { useMemo, useState } from 'react';
import { t } from '../../shell/i18n/t';
import { DeleteIcon, NewIcon, NextPageIcon, PreviousPageIcon } from '../../shell/icons';
import { ConfirmDialog } from '../common/ConfirmDialog';
import { CanvasAddControls } from './CanvasAddControls';
import { dropOnCanvas } from './canvasDrop';
import { CanvasSection, type CellMove } from './CanvasSection';
import { bodyPickable, FieldPicker } from './FieldPicker';
import { bodySections } from './formCells';
import {
  addField, addSection, addTab, bodyAttributes, containerImpact, fieldImpact, moveCell, moveTab, removeField, removeSection, removeTab,
  sectionRows, type DesignCell, type DesignSection, type DesignTab, type FormDesign,
} from './formDesign';
import { cellLabel, labelOf, useRefocus, type FormDesignCardProps, type Selection } from './formDesignerSlots';
import { useDragMove, type DragKind, type DropAt } from './useDragMove';

const useStyles = makeStyles({
  toolbar: { display: 'flex', flexWrap: 'wrap', alignItems: 'center', gap: tokens.spacingHorizontalXS },
  columns: { display: 'grid', gap: tokens.spacingHorizontalM, alignItems: 'start' },
  column: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalS, minWidth: 0 },
  // The end of the tab list (H7): a drop zone of its own, beside the last tab and clear of the buttons.
  tabsEnd: { alignSelf: 'stretch', minWidth: tokens.spacingHorizontalXXL },
  // The end of a column (A1): a drop zone of its own, above the Add section button.
  columnEnd: { minHeight: tokens.spacingVerticalXXL, borderRadius: tokens.borderRadiusSmall },
  start: { alignSelf: 'flex-start' },
});

/** The drop target at the end of the tab list (H7). */
const TABS_END: DropAt = { where: 'end', key: 'tabs' };

/** A column width ("50%") as a grid track; anything unreadable shares the room equally. */
const track = (width: string) => {
  const n = parseFloat(width);
  return Number.isFinite(n) && n > 0 ? `minmax(0, ${n}fr)` : 'minmax(0, 1fr)';
};

export interface FormCanvasProps extends FormDesignCardProps {
  readOnly: boolean;
  selection?: Selection;
  onSelect: (selection: Selection) => void;
}

interface Confirming {
  title: string;
  body: string;
  confirmLabel: string;
  run: () => void;
}

/**
 * The form's body: its tabs as a tab list, the selected tab's columns, sections and cells. Every action is a button
 * (F3, F4, F6, F7); a move keeps the focus on the moved item's button.
 */
export function FormCanvas({ design, attributes, onChange, readOnly, selection, onSelect }: FormCanvasProps) {
  const styles = useStyles();
  const refocus = useRefocus();
  const [tabKey, setTabKey] = useState<string>();
  const [confirming, setConfirming] = useState<Confirming>();
  const [picking, setPicking] = useState<string>();
  const tab = design.tabs.find((x) => x.key === tabKey) ?? design.tabs[0];
  const tabIndex = design.tabs.indexOf(tab);
  const lcid = design.lcid;
  const tabLabel = (x: DesignTab) => labelOf(x.labels, lcid, x.name);
  const sectionLabel = (s: DesignSection) => labelOf(s.labels, lcid, s.name);
  // The row of each cell as the write stores it, so a short row shows short (CSS auto-flow would pack the cells).
  const rowOf = useMemo(() => {
    const out = new Map<string, number>();
    for (const rows of sectionRows(design).values()) rows.forEach((r, i) => r.forEach((c) => out.set(c.key, i + 1)));
    return out;
  }, [design]);
  /** Every section in reading order, for the previous and next section of a cell. */
  const order = useMemo(() => bodySections(design.tabs).map((s) => s.key), [design.tabs]);

  /** A drop (ruling G1, {@link dropOnCanvas}): the moved item is selected and focused. */
  const drop = (kind: DragKind, key: string, at: DropAt) => {
    const next = dropOnCanvas(design, tab, kind, key, at);
    if (next === design) return;
    refocus(`${key}:${kind === 'tab' ? 'tab' : 'label'}`);
    if (kind === 'tab') setTabKey(key);
    onSelect({ kind: kind === 'cell' ? 'cell' : kind === 'section' ? 'section' : 'tab', key });
    onChange(next);
  };
  const drag = useDragMove(!readOnly, drop);

  const move = (id: string, next: FormDesign) => {
    refocus(id);
    onChange(next);
  };

  /** Moves a cell; when it lands in another tab (reading order crosses tabs), that tab is shown so the focus can follow. */
  const moveField = (cell: DesignCell, to: CellMove, id: string) => {
    const next = moveCell(design, cell.key, to);
    const holder = next.tabs.find((x) => x.columns.some((c) => c.sections.some((s) => s.cells.some((k) => k.key === cell.key))));
    if (holder && holder.key !== tab.key) setTabKey(holder.key);
    move(id, next);
  };

  const selectTab = (key: string) => {
    setTabKey(key);
    onSelect({ kind: 'tab', key });
  };

  /** Removes after a confirmation when something is lost (ruling F7, and the ledger ruling on lost handlers). */
  function deleteTab() {
    const impact = containerImpact(design, tab.key);
    const run = () => onChange(removeTab(design, tab.key));
    if (impact.cells === 0) {
      run();
      return;
    }
    const lines = [t('forms.deleteTab.body', { tab: tabLabel(tab), n: impact.cells })];
    if (impact.handlers > 0) lines.push(t('forms.delete.handlers', { n: impact.handlers }));
    setConfirming({ title: t('forms.deleteTab.title', { tab: tabLabel(tab) }), body: lines.join(' '), confirmLabel: t('forms.delete'), run });
  }

  function deleteSection(s: DesignSection) {
    const impact = containerImpact(design, s.key);
    const run = () => onChange(removeSection(design, s.key));
    if (impact.cells === 0) {
      run();
      return;
    }
    const lines = [t('forms.deleteSection.body', { section: sectionLabel(s), n: impact.cells })];
    if (impact.handlers > 0) lines.push(t('forms.delete.handlers', { n: impact.handlers }));
    setConfirming({ title: t('forms.deleteSection.title', { section: sectionLabel(s) }), body: lines.join(' '), confirmLabel: t('forms.delete'), run });
  }

  function remove(cell: DesignCell) {
    const label = cellLabel(cell, lcid, attributes);
    const run = () => onChange(removeField(design, cell.key));
    const lines: string[] = [];
    if (cell.kind === 'field') {
      const impact = fieldImpact(design, cell.key, attributes);
      if (impact.handlers > 0) lines.push(t('forms.removeField.handlers', { label, n: impact.handlers }));
      if (impact.required) lines.push(t('forms.removeField.required', { label }));
    } else {
      lines.push(t('forms.removeField.control', { label }));
    }
    if (lines.length === 0) {
      run();
      return;
    }
    setConfirming({ title: t('forms.removeField.title', { field: label }), body: lines.join(' '), confirmLabel: t('forms.removeField.confirm'), run });
  }

  /** The section the Add subgrid, quick view and web resource buttons add to: the selected one, or that of the selected cell. */
  const shownSections = bodySections([tab]);
  const target = selection?.kind === 'section'
    ? shownSections.find((s) => s.key === selection.key)
    : selection?.kind === 'cell' ? shownSections.find((s) => s.cells.some((c) => c.key === selection.key)) : undefined;

  /** Applies an added control and selects its cell. */
  const added = (next: FormDesign, cellKey: string | undefined) => {
    onChange(next);
    if (cellKey) {
      refocus(`${cellKey}:label`);
      onSelect({ kind: 'cell', key: cellKey });
    }
  };

  const inBody = bodyAttributes(design);
  const sectionCount = tab.columns.reduce((n, c) => n + c.sections.length, 0);

  return (
    <>
      <div className={styles.toolbar}>
        <TabList aria-label={t('forms.canvas.tabs')} selectedValue={tab.key} onTabSelect={(_, d) => selectTab(String(d.value))}>
          {design.tabs.map((x) => (
            <Tab
              key={x.key}
              value={x.key}
              data-refocus={`${x.key}:tab`}
              className={drag.line({ where: 'before', key: x.key }, 'before')}
              onClick={() => selectTab(x.key)}
              {...drag.source('tab', x.key)}
              {...drag.target('tab', { where: 'before', key: x.key })}
            >
              {tabLabel(x)}
            </Tab>
          ))}
        </TabList>
        {!readOnly && (
          <>
            {/* Pointer only: the move buttons are the accessible way to move a tab (G1). */}
            <div
              aria-hidden="true"
              className={mergeClasses(styles.tabsEnd, drag.line(TABS_END, 'before'))}
              {...drag.target('tab', TABS_END)}
            />
            <Button
              icon={<NewIcon />}
              onClick={() => {
                const next = addTab(design);
                onChange(next);
                selectTab(next.tabs[next.tabs.length - 1].key);
              }}
            >
              {t('forms.canvas.addTab')}
            </Button>
            <Button
              appearance="subtle"
              icon={<PreviousPageIcon />}
              aria-label={t('forms.canvas.moveTabLeft', { tab: tabLabel(tab) })}
              disabledFocusable={tabIndex === 0}
              onClick={() => onChange(moveTab(design, tab.key, -1))}
            />
            <Button
              appearance="subtle"
              icon={<NextPageIcon />}
              aria-label={t('forms.canvas.moveTabRight', { tab: tabLabel(tab) })}
              disabledFocusable={tabIndex === design.tabs.length - 1}
              onClick={() => onChange(moveTab(design, tab.key, 1))}
            />
            <Button
              appearance="subtle"
              icon={<DeleteIcon />}
              aria-label={t('forms.canvas.deleteTab', { tab: tabLabel(tab) })}
              disabled={design.tabs.length <= 1}
              onClick={deleteTab}
            />
            <CanvasAddControls
              design={design}
              attributes={attributes}
              target={target}
              targetLabel={target ? sectionLabel(target) : ''}
              onAdded={added}
            />
          </>
        )}
      </div>
      <div className={styles.columns} style={{ gridTemplateColumns: tab.columns.map((c) => track(c.width)).join(' ') }}>
        {tab.columns.map((column, ci) => (
          <div key={column.key} className={styles.column} role="group" aria-label={t('forms.canvas.column', { n: ci + 1 })}>
            {column.sections.map((s, si) => (
              <CanvasSection
                key={s.key}
                section={s}
                column={{ index: ci, count: tab.columns.length, sections: column.sections.length }}
                index={si}
                design={design}
                attributes={attributes}
                readOnly={readOnly}
                selection={selection}
                onSelect={onSelect}
                drag={drag}
                order={order}
                rowOf={rowOf}
                deletable={sectionCount > 1}
                onMove={move}
                onMoveCell={moveField}
                onRemoveCell={remove}
                onDelete={() => deleteSection(s)}
                onAddField={() => setPicking(s.key)}
              />
            ))}
            {!readOnly && (
              <>
                {/* Pointer only (A1): the move buttons are the accessible way to move a section (G1). */}
                <div
                  aria-hidden="true"
                  className={mergeClasses(styles.columnEnd, drag.line({ where: 'end', key: column.key }, 'above'))}
                  {...drag.target('section', { where: 'end', key: column.key })}
                />
                <Button
                  appearance="subtle"
                  icon={<NewIcon />}
                  className={styles.start}
                  onClick={() => onChange(addSection(design, tab.key, ci))}
                >
                  {t('forms.canvas.addSection', { n: ci + 1 })}
                </Button>
              </>
            )}
          </div>
        ))}
      </div>
      {picking !== undefined && (
        <FieldPicker
          attributes={attributes.filter((a) => bodyPickable(a) && !inBody.has(a.logicalName.toLowerCase()))}
          onClose={() => setPicking(undefined)}
          onPick={(a) => {
            onChange(addField(design, picking, a));
            setPicking(undefined);
          }}
        />
      )}
      <ConfirmDialog
        open={confirming !== undefined}
        title={confirming?.title ?? ''}
        body={confirming?.body ?? ''}
        confirmLabel={confirming?.confirmLabel ?? ''}
        cancelLabel={t('common.cancel')}
        onConfirm={() => {
          confirming?.run();
          setConfirming(undefined);
        }}
        onCancel={() => setConfirming(undefined)}
      />
    </>
  );
}
