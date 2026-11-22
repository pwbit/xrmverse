/**
 * Client API enum values. Each one was checked on Microsoft Learn on 2026-11-14 (plan 3.5 UI-1c, Task 4 step 1); only the
 * identifiers and values are kept here.
 */

/** https://learn.microsoft.com/power-apps/developer/model-driven-apps/clientapi/reference/formcontext-ui/getformtype */
export const FormType = { Undefined: 0, Create: 1, Update: 2, ReadOnly: 3, Disabled: 4, BulkEdit: 6 } as const;

/**
 * https://learn.microsoft.com/power-apps/developer/model-driven-apps/clientapi/reference/save-event-arguments/getsavemode
 * (UI-1c raises Save and SaveAndClose only).
 */
export const SaveMode = { Save: 1, SaveAndClose: 2, SaveAndNew: 59, AutoSave: 70 } as const;

/** https://learn.microsoft.com/power-apps/developer/model-driven-apps/clientapi/reference/formcontext-data-entity/save */
export const EntitySaveOption = { SaveAndClose: 'saveandclose', SaveAndNew: 'saveandnew' } as const;

/** https://learn.microsoft.com/power-apps/developer/model-driven-apps/clientapi/reference/attributes/setrequiredlevel */
export const REQUIRED_LEVELS = ['none', 'required', 'recommended'] as const;
export type RequiredLevel = (typeof REQUIRED_LEVELS)[number];

/** https://learn.microsoft.com/power-apps/developer/model-driven-apps/clientapi/reference/attributes/setsubmitmode */
export const SUBMIT_MODES = ['always', 'never', 'dirty'] as const;
export type SubmitMode = (typeof SUBMIT_MODES)[number];

/** https://learn.microsoft.com/power-apps/developer/model-driven-apps/clientapi/reference/formcontext-ui/setformnotification */
export const FORM_NOTIFICATION_LEVELS = ['ERROR', 'WARNING', 'INFO'] as const;
export type FormNotificationLevel = (typeof FORM_NOTIFICATION_LEVELS)[number];

/** https://learn.microsoft.com/power-apps/developer/model-driven-apps/clientapi/reference/controls/addnotification */
export const CONTROL_NOTIFICATION_LEVELS = ['ERROR', 'RECOMMENDATION'] as const;
export type ControlNotificationLevel = (typeof CONTROL_NOTIFICATION_LEVELS)[number];

/** https://learn.microsoft.com/power-apps/developer/model-driven-apps/clientapi/reference/formcontext-ui-tabs/setdisplaystate */
export const TAB_DISPLAY_STATES = ['expanded', 'collapsed'] as const;
export type TabDisplayState = (typeof TAB_DISPLAY_STATES)[number];

/**
 * https://learn.microsoft.com/power-apps/developer/model-driven-apps/clientapi/reference/attributes/getattributetype
 * Learn lists: boolean, datetime, decimal, double, file, image, integer, lookup, memo, money, multiselectoptionset,
 * optionset, string.
 */
export const AttributeType = {
  boolean: 'boolean',
  datetime: 'datetime',
  decimal: 'decimal',
  double: 'double',
  file: 'file',
  image: 'image',
  integer: 'integer',
  lookup: 'lookup',
  memo: 'memo',
  money: 'money',
  multiselectoptionset: 'multiselectoptionset',
  optionset: 'optionset',
  string: 'string',
} as const;

/**
 * https://learn.microsoft.com/power-apps/developer/model-driven-apps/clientapi/reference/controls/getcontroltype
 * (ruling R6: a choice control is `choice`, a multi-select choice control `choices`, as Learn lists them).
 */
export const ControlType = {
  standard: 'standard',
  lookup: 'lookup',
  choice: 'choice',
  choices: 'choices',
  subgrid: 'subgrid',
  quickform: 'quickform',
  iframe: 'iframe',
  webresource: 'webresource',
  timelinewall: 'timelinewall',
  /** A field control a PCF control renders (UI-1d, ruling R10). */
  customcontrol: 'customcontrol',
} as const;

/** https://learn.microsoft.com/power-apps/developer/model-driven-apps/clientapi/reference/attributes/getformat */
export const AttributeFormat = {
  date: 'date',
  datetime: 'datetime',
  duration: 'duration',
  email: 'email',
  language: 'language',
  none: 'none',
  phone: 'phone',
  text: 'text',
  textarea: 'textarea',
  tickersymbol: 'tickersymbol',
  timezone: 'timezone',
  url: 'url',
} as const;

/**
 * Control names: https://learn.microsoft.com/power-apps/developer/model-driven-apps/clientapi/reference/attributes/controls-collection
 * (a second control of a column is `{column}1`, then `{column}2`). The `header_` and `footer_` prefixes are not on that page
 * (it only names `header_process_`): taken from the Global Constraints, flagged in the Task 4 report.
 */
export const HEADER_PREFIX = 'header_';
export const FOOTER_PREFIX = 'footer_';
