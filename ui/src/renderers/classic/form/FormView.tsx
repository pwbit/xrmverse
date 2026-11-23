import { Button, Link, makeStyles, mergeClasses, Popover, PopoverSurface, PopoverTrigger, Tab, TabList, Text, Title2, tokens } from '@fluentui/react-components';
import { Fragment, useId, useMemo, useState, type ReactNode } from 'react';
import type { RecordContext } from '../../../apps/record/loadRecord';
import type { RecordForm } from '../../../apps/record/useRecordForm';
import type { Cell, FormModel, Tab as FormTabModel } from '../../../model/form';
import type { TimelineOptions } from '../../../model/app';
import type { SaveStatus } from '../../../apps/record/useAutoSave';
import { createUiOverlay, useUiOverlay, type UiOverlay } from '../../../client-api/uiOverlay';
import { labelFor } from '../../../parsers/formXml';
import { t } from '../../../shell/i18n/t';
import { isShown, type ControlEnv, type FormBackgrounds, type SubgridModes, type TimelineAppearance } from './controlEnv';
import { FormCell } from './FormCell';
import { FormNotifications, type FormNotification } from './FormNotifications';
import { FormSelector } from './FormSelector';
import { FormTab } from './FormTab';
import { RelatedTab } from './RelatedTab';
import { CollapseItemIcon } from '../../../shell/icons';
import type { SenderPolicy } from './PartyListControl';

/** The header or footer: its visible cells in a strip, label under value. */
function CellStrip({ cells, env, label, className, cellClass, readOnly, labelPosition = 'bottom' }: { cells: Cell[]; env: ControlEnv; label: string; className: string; cellClass: string; readOnly?: boolean; labelPosition?: 'bottom' | 'top' }) {
  const shown = cells.filter((c) => isShown(env, c));
  if (shown.length === 0) return null;
  return (
    <div className={className} role="group" aria-label={label}>
      {shown.map((cell) => (
        <div key={cell.id} className={cellClass}>
          <FormCell cell={cell} env={env} labelPosition={labelPosition} readOnly={readOnly} />
        </div>
      ))}
    </div>
  );
}

const useStyles = makeStyles({
  // The form fills the main area: the command bar and the header card stay, only the form's content scrolls.
  form: { display: 'flex', flexDirection: 'column', height: '100%', minHeight: 0, '& > :not([data-form-body])': { flexShrink: 0 } },
  body: { flex: '1 1 auto', minHeight: 0, overflow: 'auto' },
  // The header card (border and shadow, as the views'): the title and the header fields on top, the tabs at its foot.
  card: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
    padding: `${tokens.spacingVerticalM} ${tokens.spacingHorizontalL} 0`,
    marginBlock: tokens.spacingVerticalS,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusLarge,
    boxShadow: tokens.shadow4,
    backgroundColor: tokens.colorNeutralBackground1,
  },
  // Without tabs the card closes under the title.
  cardAlone: { paddingBottom: tokens.spacingVerticalM },
  bar: { display: 'flex', alignItems: 'flex-start', justifyContent: 'space-between', gap: tokens.spacingHorizontalL, flexWrap: 'wrap' },
  title: { fontSize: tokens.fontSizeBase600, lineHeight: tokens.lineHeightBase600 },
  titles: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalXXS, minWidth: 0 },
  titleRow: { display: 'flex', alignItems: 'baseline', gap: tokens.spacingHorizontalS, flexWrap: 'wrap', '& h1': { margin: 0 } },
  subtitle: { display: 'flex', alignItems: 'center', gap: tokens.spacingHorizontalM, flexWrap: 'wrap', color: tokens.colorNeutralForeground2 },
  // The header fields, compact and read-only (value over label, a line between them); the chevron opens them to edit.
  headerArea: { display: 'flex', alignItems: 'flex-start', gap: tokens.spacingHorizontalXS, marginInlineStart: 'auto', maxWidth: '100%' },
  header: {
    display: 'flex',
    flexWrap: 'wrap',
    rowGap: tokens.spacingVerticalXS,
    '& > *': { paddingInline: tokens.spacingHorizontalM, borderLeftWidth: '1px', borderLeftStyle: 'solid', borderLeftColor: tokens.colorNeutralStroke2 },
    '& > :first-child': { borderLeftWidth: 0 },
    // The controls read as text: no box, the value in semibold, the label small under it.
    '& input, & textarea, & button, & .fui-Input, & .fui-Textarea, & .fui-Dropdown, & .fui-Combobox, & .fui-Select, & select, & .fui-SpinButton': {
      border: 'none',
      backgroundColor: 'transparent',
      boxShadow: 'none',
      minHeight: 'auto',
      paddingInline: 0,
      color: tokens.colorNeutralForeground1,
      fontWeight: tokens.fontWeightSemibold,
      '::after': { display: 'none' },
    },
    // A value fills its cell and ends with an ellipsis when it is longer.
    '& input': { width: '100%', textOverflow: 'ellipsis' },
    '& .fui-Input, & .fui-Field, & .fui-Dropdown, & .fui-Combobox, & .fui-Select': { width: '100%', minWidth: 0, maxWidth: '100%' },
    '& .fui-Input__contentAfter, & .fui-Dropdown__expandIcon, & .fui-Combobox__expandIcon, & .fui-Select__icon, & .fui-SpinButton__incrementButton, & .fui-SpinButton__decrementButton': { display: 'none' },
    '& label': { fontSize: tokens.fontSizeBase200, color: tokens.colorNeutralForeground3, fontWeight: tokens.fontWeightRegular },
  },
  headerCell: { width: '13rem', maxWidth: '100%', boxSizing: 'border-box' },
  editCell: { minWidth: '18rem' },
  edit: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalS, minWidth: '20rem' },
  tabs: { marginInlineStart: `calc(-1 * ${tokens.spacingHorizontalS})` },
  footer: {
    display: 'flex',
    gap: tokens.spacingHorizontalXXL,
    flexWrap: 'wrap',
    marginBlockStart: tokens.spacingVerticalL,
    paddingBlockStart: tokens.spacingVerticalS,
    borderTop: `1px solid ${tokens.colorNeutralStroke2}`,
  },
  panel: { paddingBlock: tokens.spacingVerticalS },
  saveStatus: { color: tokens.colorNeutralForeground3, fontSize: tokens.fontSizeBase200 },
  // The body and its aside side by side; one column on a narrow window.
  withAside: {
    display: 'grid',
    gridTemplateColumns: 'minmax(0, 1fr) minmax(16rem, 24rem)',
    alignItems: 'start',
    columnGap: tokens.spacingHorizontalL,
    '@media (max-width: 900px)': { gridTemplateColumns: 'minmax(0, 1fr)' },
  },
  main: { minWidth: 0 },
  aside: { paddingBlock: tokens.spacingVerticalS, minWidth: 0 },
});

const SAVE_STATUS = { unsaved: 'record.autoSave.unsaved', saving: 'record.autoSave.saving', saved: 'record.autoSave.saved' } as const;

export interface FormViewProps {
  form: FormModel;
  state: RecordForm;
  ctx: RecordContext;
  /** The user's language (`uilanguageid`). */
  lcid: number;
  onSelectForm: (form: FormModel) => void;
  /** The forms the selector lists (with `form` among them); without it no selector is drawn. */
  forms?: FormModel[];
  /** The requested form was not listed and the default opened instead. */
  notice?: 'formNotAvailable';
  /** The command bar (Task 7). */
  commandBar?: ReactNode;
  /** The symbol of the record's currency, appended to the label of money fields. */
  currencySymbol?: string;
  /** Extra warning notifications. */
  warnings?: string[];
  /** Notifications raised by the form's scripts. */
  scriptNotifications?: FormNotification[];
  /** The app id for the URLs of the records a subgrid opens. */
  appId?: string;
  /** The app's timeline options (the app settings); the timeline control reads them from the env. */
  timeline?: TimelineOptions;
  /** The timeline's restyle from the modern form's overlay (UI-2c); absent, the classic timeline. */
  timelineAppearance?: TimelineAppearance;
  /** Section and field backgrounds from the modern form's overlay; absent, none. */
  backgrounds?: FormBackgrounds;
  /** The modes of the form's subgrids from the modern form's overlay; absent, every subgrid is a grid. */
  subgridModes?: SubgridModes;
  /** Bump to reload the subgrids (the Refresh command). */
  refreshKey?: number;
  /** What the form's scripts changed; without it the form renders its FormXml as is. */
  overlay?: UiOverlay;
  /** The auto-save status in the footer (#117); `none` or absent shows nothing. */
  saveStatus?: SaveStatus;
  /**
   * Draws the body of the form's own tabs in place of the tab list (UI-2c: the modern look's wizard and free grid). It returns
   * undefined to keep the tab list. A node of its own replaces the tab list and its panel, and the Related tab follows it as a
   * link; `tabList(renderTab)` is the tab list itself with the panel's tab drawn by `renderTab`. Absent, the form is classic.
   */
  renderTabBody?: (args: TabBodyArgs) => ReactNode | undefined;
  /**
   * The form's save, as the command bar's Save runs it (OnSave, the required check, the errors): what a layout's own Save
   * button runs (UI-2c, the wizard's last step). The classic form draws no such button.
   */
  onSave?: () => void;
  /** A banner under the title card, above the notifications (an email's send status, plan 4.1b). */
  banner?: ReactNode;
  /** A column beside the form's body, under it on a narrow window (an email's attachments, plan 4.1b). */
  aside?: ReactNode;
  /** The sender policy an email's `from` offers (plan 4.1b). */
  senderPolicy?: SenderPolicy;
}

/** A form tab with its FormXml index (its value in the tab list). */
export interface IndexedTab {
  tab: FormTabModel;
  index: number;
}

/** What {@link FormViewProps.renderTabBody} draws the tabs from. */
export interface TabBodyArgs {
  /** The tabs shown (as a script set them, else as the FormXml says), in FormXml order. */
  tabs: IndexedTab[];
  /** The tab the user or a script selected while it is shown, else the first one shown; undefined when none is shown. */
  current?: IndexedTab;
  env: ControlEnv;
  /** Selects a tab (as a click on it, or a script's `setFocus()`); closes the Related tab. */
  selectTab(tab: FormTabModel): void;
  /**
   * The classic tab list and panel, the Related tab included; `renderTab` draws the selected tab (default: the classic tab).
   * It must be called synchronously inside `renderTabBody` (FormView reads that it was called right after).
   */
  tabList(renderTab?: (tab: FormTabModel) => ReactNode): ReactNode;
}

/** The value of the Related tab, apart from the FormXml indexes of the form's own tabs. */
const RELATED = 'related';

const INTENTS = { ERROR: 'error', WARNING: 'warning', INFO: 'info' } as const;

/** The title, form bar, header strip, tab list and footer of one form. */
export function FormView({ form, state, ctx, lcid, onSelectForm, forms, notice, commandBar, currencySymbol, warnings = [], scriptNotifications = [], appId, timeline, timelineAppearance, backgrounds, subgridModes, refreshKey = 0, overlay, saveStatus = 'none', renderTabBody, banner, aside, senderPolicy }: FormViewProps) {
  const styles = useStyles();
  const uid = useId();
  // Without the page's overlay the form keeps its own, for the selected tab.
  // eslint-disable-next-line react-hooks/exhaustive-deps
  const own = useMemo(createUiOverlay, [form]);
  const ui = overlay ?? own;
  useUiOverlay(ui);
  // The shared reads start over with a refresh.
  // eslint-disable-next-line react-hooks/exhaustive-deps
  const cache = useMemo(() => new Map<string, Promise<unknown>>(), [refreshKey, form]);
  const env: ControlEnv = { ctx, state, lcid, currencySymbol, formId: form.id, appId, timeline, ...(timelineAppearance ? { timelineAppearance } : {}), ...(backgrounds ? { backgrounds } : {}), ...(subgridModes ? { subgridModes } : {}), refreshKey, cache, overlay: ui, ...(senderPolicy ? { senderPolicy } : {}) };
  // Tabs keep their FormXml index as their value, so that hiding one does not move the selection.
  const tabs = form.tabs.map((tab, index) => ({ tab, index })).filter(({ tab }) => isShown(env, tab));
  // The tab the user or a script selected while it is shown, else the first one shown.
  const current = tabs.find(({ tab }) => tab === ui.selectedTab()) ?? tabs[0];
  // The Related tab (#120) follows the form's own tabs when the form names related tables; scripts do not select it.
  const navigation = form.navigation ?? [];
  const [relatedOpen, setRelatedOpen] = useState(false);
  const showRelated = navigation.length > 0 && (relatedOpen || !current);
  const selected = showRelated ? RELATED : String(current?.index ?? 0);
  const selectTab = (tab: FormTabModel) => {
    setRelatedOpen(false);
    ui.selectTab(tab);
  };

  // A body of the hook's own (UI-2c) replaces the tab list; `tabList` draws the tab list itself.
  let ownTabList = false;
  // The tab list sits at the foot of the header card; `tabList` returns the panel, which scrolls under it.
  let strip: ReactNode = null;
  const tabList = (renderTab: (tab: FormTabModel) => ReactNode = (tab) => <FormTab tab={tab} env={env} />): ReactNode => {
    ownTabList = true;
    strip = (
        <TabList
          className={styles.tabs}
          aria-label={form.name}
          selectedValue={selected}
          onTabSelect={(_, d) => {
            if (d.value === RELATED) return setRelatedOpen(true);
            selectTab(form.tabs[Number(d.value)]!);
          }}
        >
          {tabs.map(({ tab, index }) => (
            <Tab key={`${tab.name}-${index}`} value={String(index)} id={`${uid}-tab-${index}`} aria-controls={`${uid}-panel-${index}`}>
              {ui.element(tab).label ?? labelFor(tab.labels, lcid) ?? tab.name}
            </Tab>
          ))}
          {navigation.length > 0 && (
            <Tab value={RELATED} id={`${uid}-tab-${RELATED}`} aria-controls={`${uid}-panel-${RELATED}`}>
              {t('record.related.tab')}
            </Tab>
          )}
        </TabList>
    );
    return (
        <div className={styles.panel} role="tabpanel" id={`${uid}-panel-${selected}`} aria-labelledby={`${uid}-tab-${selected}`}>
          {showRelated ? <RelatedTab key={form.id} items={navigation} env={env} /> : current && <Fragment key={selected}>{renderTab(current.tab)}</Fragment>}
        </div>
    );
  };
  const custom = renderTabBody?.({ tabs, current, env, selectTab, tabList });
  const ownBody = custom !== undefined && !ownTabList;

  const primaryName = ctx.entity.primaryNameAttribute;
  const named = primaryName ? (state.record ?? ctx.record)?.[primaryName] : undefined;
  const tableName = ui.formEntityName() ?? ctx.entity.displayName;
  const title = ctx.record
    ? typeof named === 'string' && named
      ? named
      : tableName
    : t('form.title.new', { table: tableName });

  const notifications: FormNotification[] = [
    ...(notice === 'formNotAvailable' ? [{ key: 'notice', intent: 'warning' as const, text: t('record.formNotAvailable') }] : []),
    ...warnings.map((text, i) => ({ key: `warning-${i}`, intent: 'warning' as const, text })),
    // A script's form notifications, the newest on top (Learn, setFormNotification).
    ...[...ui.formNotifications()].reverse().map((n) => ({ key: `form-${n.uniqueId}`, intent: INTENTS[n.level], text: n.message })),
    ...scriptNotifications,
    ...(state.inactiveLabel
      ? [{ key: 'readOnly', intent: 'info' as const, text: t('record.inactive', { state: state.inactiveLabel }) }]
      : state.readOnly
        ? [{ key: 'readOnly', intent: 'info' as const, text: t('record.readOnly') }]
        : []),
    ...(state.notification ? [{ key: 'error', intent: 'error' as const, text: state.notification }] : []),
  ];

  const body = ownBody ? (
    relatedOpen && navigation.length > 0 ? (
      <div className={styles.panel}>
        <Link as="button" onClick={() => setRelatedOpen(false)}>
          {t('record.related.back')}
        </Link>
        <RelatedTab key={form.id} items={navigation} env={env} />
      </div>
    ) : (
      <>
        {custom}
        {navigation.length > 0 && (
          <Link as="button" onClick={() => setRelatedOpen(true)}>
            {t('record.related.tab')}
          </Link>
        )}
      </>
    )
  ) : custom !== undefined ? (
    custom
  ) : (
    (tabs.length > 0 || navigation.length > 0) && tabList()
  );
  const headerCells = form.header.filter((c) => isShown(env, c));

  return (
    <section aria-label={title} data-xrmverse-form="" className={styles.form}>
      {commandBar}
      <div className={mergeClasses(styles.card, !strip && styles.cardAlone)}>
        <div className={styles.bar}>
          <div className={styles.titles}>
            <div className={styles.titleRow}>
              <Title2 as="h1" className={styles.title}>{title}</Title2>
              {saveStatus !== 'none' && (
                <Text role="status" className={styles.saveStatus}>
                  {t(SAVE_STATUS[saveStatus])}
                </Text>
              )}
            </div>
            <div className={styles.subtitle}>
              {ctx.record && <Text>{tableName}</Text>}
              {forms && <FormSelector forms={forms.filter((f) => f === form || (ui.element(f).visible ?? true))} current={form} onSelect={onSelectForm} />}
            </div>
          </div>
          {headerCells.length > 0 && (
            <div className={styles.headerArea}>
              <CellStrip cells={form.header} env={env} label={t('record.header')} className={styles.header} cellClass={styles.headerCell} readOnly />
              <Popover positioning="below-end" withArrow={false}>
                <PopoverTrigger disableButtonEnhancement>
                  <Button appearance="subtle" icon={<CollapseItemIcon />} aria-label={t('record.header.edit')} title={t('record.header.edit')} />
                </PopoverTrigger>
                <PopoverSurface>
                  <CellStrip cells={form.header} env={env} label={t('record.header.edit')} className={styles.edit} cellClass={styles.editCell} labelPosition="top" />
                </PopoverSurface>
              </Popover>
            </div>
          )}
        </div>
        {strip}
      </div>
      {banner}
      <FormNotifications items={notifications} />
      <div className={mergeClasses(styles.body, aside !== undefined && styles.withAside)} data-form-body="">
        {aside !== undefined ? (
          <>
            <div className={styles.main}>
              {body}
              <CellStrip cells={form.footer} env={env} label={t('record.footer')} className={styles.footer} cellClass={styles.headerCell} readOnly />
            </div>
            <div className={styles.aside}>{aside}</div>
          </>
        ) : (
          <>
            {body}
            <CellStrip cells={form.footer} env={env} label={t('record.footer')} className={styles.footer} cellClass={styles.headerCell} readOnly />
          </>
        )}
      </div>
    </section>
  );
}
