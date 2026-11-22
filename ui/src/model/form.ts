/** Labels by LCID, from a `<labels>` element. */
export type Labels = Record<number, string>;

/** One `<parameters>` child of a FormXml `customControl`: a column name, or a literal when `static`. */
export interface CustomControlParameter {
  value: string;
  /** `static="true"`: `value` is a literal, not a column name. */
  static: boolean;
  /** The `type` attribute of a literal (`Whole.None`, `SingleLine.Text`, ...), when present. */
  type?: string;
}

/** The PCF control a FormXml `controlDescription` names for the web form factor. */
export interface CustomControlRef {
  /** The `customcontrol` row name (`{prefix}_{namespace}.{constructor}`). */
  name: string;
  /** By manifest property name. */
  parameters: Record<string, CustomControlParameter>;
}

/** The `<data-set name>` child of a dataset control's `<parameters>`: its own children as text, by tag name. */
export interface CustomControlDataSet {
  name: string;
  /** `ViewId`, `TargetEntityType`, `RelationshipName`, `RecordsPerPage`, ... */
  parameters: Record<string, string>;
}

/** A dataset PCF control on a subgrid: `parameters` holds the other `<parameters>` children. */
export type DataSetControlRef = CustomControlRef & { dataSet?: CustomControlDataSet };

export interface FieldControl {
  kind: 'field';
  id: string;
  attribute: string;
  disabled: boolean;
  /** The PCF custom control that replaces the standard one; absent when the standard control is used. */
  customControl?: CustomControlRef | undefined;
}

export interface SubgridControl {
  kind: 'subgrid';
  id: string;
  relationship: string;
  targetEntity: string;
  viewId: string;
  recordsPerPage: number;
  enableViewPicker: boolean;
  /** Dashboards: `ChartGridMode` (`Chart` shows `visualizationId`, `Grid` the view's grid); absent when not given. */
  chartGridMode?: 'Grid' | 'Chart';
  /** Dashboards: the `VisualizationId` parameter (a chart id as written). */
  visualizationId?: string;
  /** Dashboards: `EnableChartPicker` or `EnableViewPicker` is true (ignored, with a notice; ruling F4). */
  pickers?: boolean;
  /** The PCF dataset control that replaces the grid; absent when the standard grid is used. */
  customControl?: DataSetControlRef | undefined;
  /**
   * A related-tables item (#120), not a FormXml subgrid: `targetEntity` and `viewId` are empty; the table is the other side of
   * the relationship and the view its associated view.
   */
  related?: boolean;
}

export interface QuickViewControl {
  kind: 'quickview';
  id: string;
  lookupAttribute: string;
  /** Target table logical name to quick view form id. */
  forms: Record<string, string>;
}

/** A web resource control or an IFRAME control: both render as an IFRAME on the form. */
export interface WebResourceControl {
  kind: 'webresource';
  id: string;
  classId: string;
  /** The IFRAME control (its `Url` is an address); false for a web resource control (its `Url` is the web resource name). */
  iframe: boolean;
  /** `<Url>`: the web resource name, or the IFRAME's address. */
  url: string;
  /** `<Data>`: the custom parameter a web resource gets as `data`. */
  data: string;
  /** `<PassParameters>`: the record's id, typename, orgname and language codes go in the query string. */
  passParameters: boolean;
  /** `<Security>`: cross-frame scripting is restricted (the frame is sandboxed). */
  restrictScripting: boolean;
  /** `<Scrolling>`: as necessary, always or never. */
  scrolling: 'auto' | 'yes' | 'no';
  /** `<Border>`. */
  border: boolean;
}

/** The timeline (UI-1g): activities and notes regarding the record. */
export interface TimelineControl {
  kind: 'timeline';
  id: string;
  classId: string;
  /**
   * The element names under the control's `<parameters>`. xrmverse does not read them yet (spec §6.6): the timeline uses the
   * default settings and says so when this list is not empty.
   */
  parameterNames: string[];
}

/** A control class this version does not render. */
export interface PlaceholderControl {
  kind: 'unknown';
  id: string;
  classId: string;
}

export type Control = FieldControl | SubgridControl | QuickViewControl | WebResourceControl | TimelineControl | PlaceholderControl;

export interface Cell {
  id: string;
  labels: Labels;
  showLabel: boolean;
  visible: boolean;
  colSpan: number;
  rowSpan: number;
  /** Absent for a spacer cell. */
  control?: Control;
}

export interface Section {
  name: string;
  labels: Labels;
  showLabel: boolean;
  visible: boolean;
  columns: number;
  /**
   * The relative widths of the columns when the FormXml writes one digit per column (`columns="111"` is three equal
   * columns, `"12"` two of one third and two thirds); absent when it writes the count (`"2"`), for equal columns.
   */
  widths?: number[];
  labelPosition: 'left' | 'top';
  rows: Cell[][];
}

export interface TabColumn {
  width: string;
  sections: Section[];
}

export interface Tab {
  name: string;
  labels: Labels;
  visible: boolean;
  columns: TabColumn[];
}

/** One `<Handler>` of a FormXml event. */
export interface Handler {
  functionName: string;
  libraryName: string;
  enabled: boolean;
  /** The raw `parameters` attribute: a comma-separated list of JavaScript values. */
  parameters: string;
  passExecutionContext: boolean;
}

/** One `<event>` of a FormXml `<events>` list; only OnLoad, OnSave and OnChange are kept. */
export interface FormEvent {
  name: 'onload' | 'onsave' | 'onchange';
  /** The attribute an OnChange event belongs to. */
  attribute?: string;
  handlers: Handler[];
}

/**
 * One item of the form's related-tables menu (`<Navigation><NavBar>`): a `NavBarByRelationshipItem` lists the rows related
 * through `relationship`; a `NavBarItem` (a URL or a web resource) is kept to be shown as not supported.
 */
export type NavItem =
  | { kind: 'relationship'; id: string; relationship: string; labels: Labels }
  | { kind: 'unsupported'; id: string; labels: Labels };

export interface FormModel {
  id: string;
  name: string;
  entity: string;
  type: number;
  isDefault: boolean;
  order: number;
  /** Role ids that may see the form, normalised (no braces, lower case); empty means everyone. */
  roles: string[];
  /**
   * `DisplayConditions/@FallbackForm`: the form is also shown to a user none of whose roles has a form of the table assigned
   * (Learn, "Set the fallback form for a table").
   */
  fallback?: boolean;
  tabs: Tab[];
  header: Cell[];
  footer: Cell[];
  hasScripts: boolean;
  /** The related-tables menu (`<Navigation>`), in `Sequence` order, without the items marked `Show="false"`. */
  navigation?: NavItem[];
  /** `<formLibraries>` names, in load order. */
  libraries: string[];
  events: FormEvent[];
}
