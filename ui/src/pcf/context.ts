import { typographyStyles, type BrandVariants, type Theme } from '@fluentui/react-components';
import type { FieldValue } from '../admin/tables/formValues';
import { isUserLocal, type AttributeMeta } from '../admin/tables/metadata';
import { Stage, stub, stubProperty } from '../client-api/notImplemented';
import { xrmverseBrand, xrmverseDarkTheme, xrmverseTheme } from '../shell/theme';
import { toClientValue, type ClientLookup } from '../client-api/values';
import type { DateFormats } from '../apps/userSettings';
import { createFormatting, type PcfFormatting } from './formatting';
import type { PcfDataSet } from './dataset';
import { controlFileUrl } from './loader';
import type { PcfManifest } from './manifest';

/**
 * The `context` a PCF control gets in `init` and `updateView` (Learn, `ComponentFramework.Context`), ruling R8. One object per
 * control instance: `updateContext` replaces what changes (parameters, mode values, page, `updatedProperties`), so a control
 * that keeps the context from `init` reads current values too. Members outside ruling R8 throw the Client API's "not
 * available" error.
 */

/** One entry of `context.parameters`: a column (bound) or a literal (input). */
export interface PcfParameter {
  raw: unknown;
  formatted: string;
  type?: string;
  attributes?: Record<string, unknown>;
  error?: boolean;
  errorMessage?: string;
  security?: { editable: boolean; readable: boolean; secured: boolean };
}

/**
 * `mode.contextInfo`: the record the control sits on. Learn's `Mode` reference does not list it; Power Apps provides it and
 * published controls read the record id from it (observed behaviour, issue #181). `entityId` is empty on a new record.
 */
export interface PcfContextInfo {
  entityId: string;
  entityTypeName: string;
  entityRecordName: string;
}

/** What changes between two `updateView` calls. */
export interface PcfView {
  /** A dataset control's data-set is the {@link PcfDataSet} itself, the same object for the life of the control. */
  parameters: Record<string, PcfParameter | PcfDataSet>;
  mode: {
    isControlDisabled: boolean;
    isVisible: boolean;
    label: string;
    allocatedWidth: number;
    allocatedHeight: number;
    contextInfo: PcfContextInfo;
  };
  page: { entityId: string | null; entityTypeName: string; isPageReadOnly: boolean };
  updatedProperties: string[];
}

/** What stays for the life of the control instance. */
export interface PcfContextInput {
  manifest: PcfManifest;
  /** The `customcontrol` row name its files are served under. */
  controlName: string;
  /** The control's strings in the user's language (ruling R6). */
  strings: Record<string, string>;
  /** The shell's locale (`en-US`, `it-IT`), for `formatting`. */
  locale: string;
  /** The record's currency symbol, for `formatting.formatCurrency` without a symbol. */
  currencySymbol?: string;
  /** The user's time zone (IANA), for the date formats. */
  timeZone?: string;
  /** The user's date and time patterns (in the apps), for `formatting`. */
  dateFormats?: DateFormats;
  userSettings: Record<string, unknown>;
  /** The UI-1c `Xrm.WebApi`. */
  webAPI: unknown;
  /** The UI-1c `Xrm.Navigation`. */
  navigation: unknown;
  utils: {
    getEntityMetadata: (...args: unknown[]) => unknown;
    hasEntityPrivilege: (entityTypeName: string, privilegeType: number, privilegeDepth: number) => boolean;
    lookupObjects: (...args: unknown[]) => unknown;
  };
  /** `factory.requestRender()`: the host calls `updateView` again. */
  requestRender(): void;
  trackContainerResize(track: boolean): void;
  /** `mode.setControlState`: kept by the host and handed back to the next `init` as `state`. */
  setControlState(state: Record<string, unknown>): void;
  /** Fetches one file of the control (for `resources.getResource`); default `fetch` with the session cookie. */
  fetchFile?: (url: string) => Promise<Response>;
  /** Where the control files are served; default `/Controls`. */
  baseUrl?: string;
  /** The active Fluent theme where the control renders (`useActiveTheme()`); default the xrmverse theme of the OS scheme. */
  theme?: { theme: Theme; dark: boolean; brand?: BrandVariants };
}

export type PcfContext = Record<string, unknown> & { parameters: PcfView['parameters']; updatedProperties: string[] };

const path = (member: string) => `context.${member}`;

/** Bytes as base64, the shape `getResource` hands its success callback. */
function base64(bytes: Uint8Array): string {
  let text = '';
  for (let i = 0; i < bytes.length; i += 0x8000) text += String.fromCharCode(...bytes.subarray(i, i + 0x8000));
  return btoa(text);
}

/** The context of one control instance, showing `view`. */
export function buildContext(input: PcfContextInput, view: PcfView): PcfContext {
  const fetchFile = input.fetchFile ?? ((url: string) => fetch(url, { credentials: 'same-origin' }));
  const mode: Record<string, unknown> = {
    trackContainerResize: (track: unknown) => input.trackContainerResize(track === true),
    setControlState: (state: unknown) => {
      input.setControlState(state && typeof state === 'object' ? { ...(state as Record<string, unknown>) } : {});
      return true;
    },
    setFullScreen: stub(path('mode.setFullScreen'), Stage.later),
    contextInfo: {},
  };
  const client = {
    // Learn, Client.getFormFactor: 1 is the web.
    getFormFactor: () => 1,
    getClient: () => 'Web',
    isOffline: () => false,
    isNetworkAvailable: () => true,
    disableScroll: false,
  };
  const resources = {
    // The key itself when no resx has it (ruling R6: shown, never silently replaced).
    getString: (id: unknown) => {
      const key = String(id);
      return Object.prototype.hasOwnProperty.call(input.strings, key) ? input.strings[key] : key;
    },
    getResource: (id: unknown, success: (data: string) => void, failure: () => void) => {
      const wanted = String(id);
      const img = input.manifest.img.find((p) => p === wanted || p.toLowerCase() === wanted.toLowerCase());
      if (!img) {
        failure();
        return;
      }
      void (async () => {
        try {
          const response = await fetchFile(controlFileUrl(input.controlName, img, input.baseUrl));
          if (!response.ok) throw new Error(String(response.status));
          success(base64(new Uint8Array(await response.arrayBuffer())));
        } catch {
          failure();
        }
      })();
    },
  };
  const factory = {
    requestRender: () => input.requestRender(),
    getPopupService: stub(path('factory.getPopupService'), Stage.later),
  };
  const context = {
    parameters: {},
    updatedProperties: [],
    mode,
    client,
    userSettings: input.userSettings,
    formatting: createFormatting(input.locale, { currencySymbol: input.currencySymbol, timeZone: input.timeZone, dateFormats: input.dateFormats }),
    resources,
    webAPI: input.webAPI,
    navigation: input.navigation,
    utils: { ...input.utils },
    factory,
    page: {},
    // Learn, Theming: the platform's Fluent v9 theme: the active one where the control renders (the modern overlay's ramp
    // included), else the shell's light or dark one for the operating system.
    fluentDesignLanguage: (() => {
      if (input.theme) {
        const { theme, dark, brand } = input.theme;
        return { tokenTheme: theme, typographyTokens: typographyStyles, brand: brand ?? xrmverseBrand, isDarkTheme: dark };
      }
      const dark = typeof window.matchMedia === 'function' && window.matchMedia('(prefers-color-scheme: dark)').matches;
      return { tokenTheme: dark ? xrmverseDarkTheme : xrmverseTheme, typographyTokens: typographyStyles, brand: xrmverseBrand, isDarkTheme: dark };
    })(),
  } as unknown as PcfContext;
  for (const member of ['device', 'events', 'copilot']) stubProperty(context, member, path(member), Stage.later);
  return updateContext(context, view);
}

/** Puts `view` into `context` (the same object, returned). */
export function updateContext(context: PcfContext, view: PcfView): PcfContext {
  context.parameters = view.parameters;
  context.updatedProperties = view.updatedProperties;
  const { contextInfo, ...mode } = view.mode;
  const target = context.mode as Record<string, unknown>;
  Object.assign(target, mode);
  // The same object for the life of the control, as for `mode` and `page`.
  Object.assign(target.contextInfo as Record<string, unknown>, contextInfo);
  Object.assign(context.page as Record<string, unknown>, view.page);
  return context;
}

/** The manifest `of-type` that matches a column (Learn, manifest property types). */
export function manifestTypeOf(a: AttributeMeta): string {
  const format = (a.format ?? '').toLowerCase();
  switch (a.kind) {
    case 'string':
      return (
        { email: 'SingleLine.Email', phone: 'SingleLine.Phone', url: 'SingleLine.URL', textarea: 'SingleLine.TextArea', tickersymbol: 'SingleLine.Ticker' }[format] ??
        'SingleLine.Text'
      );
    case 'memo':
      return 'Multiple';
    case 'int':
    case 'bigint':
      return { duration: 'Whole.Duration', timezone: 'Whole.TimeZone', language: 'Whole.Language' }[format] ?? 'Whole.None';
    case 'decimal':
      return 'Decimal';
    case 'double':
      return 'FP';
    case 'money':
      return 'Currency';
    case 'bool':
      return 'TwoOptions';
    case 'datetime':
      return a.dateOnly ? 'DateAndTime.DateOnly' : 'DateAndTime.DateAndTime';
    case 'choice':
      return 'OptionSet';
    case 'choices':
      return 'MultiSelectOptionSet';
    case 'lookup':
      return 'Lookup.Simple';
    case 'partylist':
      return 'Lookup.PartyList';
    default:
      return a.typeName;
  }
}

/** Learn, `RequiredLevel`: 0 none, 1 system required, 2 application required, 3 recommended. */
function requiredLevelOf(level: string): number {
  if (level === 'SystemRequired') return 1;
  if (level === 'ApplicationRequired') return 2;
  if (level === 'Recommended') return 3;
  return 0;
}

export interface BoundOptions {
  formatting: PcfFormatting;
  /** The locale `formatting` was made for; default `en-US`. */
  locale?: string;
  /** The user's date and time patterns `formatting` was made with (in the apps). */
  dateFormats?: DateFormats;
  /** The record's currency symbol. */
  currencySymbol?: string;
  /** The user may change the value through this control. */
  editable: boolean;
  /** The user's time zone (IANA): User Local dates are read and shown in it. */
  zone?: string;
  /** The required level as the form has it now (a script may change it); default the column's. */
  requiredLevel?: string;
  /** The field's error message, if any. */
  error?: string;
}

/** A bound parameter (ruling R8) for the form value `value` of column `a`. */
export function boundParameter(a: AttributeMeta, value: FieldValue | undefined, options: BoundOptions): PcfParameter {
  const zone = options.zone ?? 'Etc/UTC';
  const raw = toClientValue(a, value, zone);
  const type = manifestTypeOf(a);
  const attributes: Record<string, unknown> = {
    DisplayName: a.displayName,
    LogicalName: a.logicalName,
    RequiredLevel: requiredLevelOf(options.requiredLevel ?? a.requiredLevel),
    Type: type,
  };
  if (a.precision !== undefined) attributes.Precision = a.precision;
  if (a.minValue !== undefined) attributes.MinValue = a.minValue;
  if (a.maxValue !== undefined) attributes.MaxValue = a.maxValue;
  if (a.maxLength !== undefined) attributes.MaxLength = a.maxLength;
  if (a.options.length > 0) attributes.Options = a.options.map((o) => ({ Label: o.label, Value: o.value }));
  return {
    raw,
    formatted: formattedOf(a, raw, options, zone),
    type,
    attributes,
    error: !!options.error,
    errorMessage: options.error ?? '',
    security: { editable: options.editable, readable: true, secured: a.secured },
  };
}

function formattedOf(a: AttributeMeta, raw: unknown, options: BoundOptions, zone: string): string {
  if (raw === null || raw === undefined) return '';
  const f = options.formatting;
  const label = (v: number) => a.options.find((o) => o.value === v)?.label ?? String(v);
  switch (a.kind) {
    case 'money':
      return f.formatCurrency(raw as number, a.precision, options.currencySymbol);
    case 'decimal':
    case 'double':
      return f.formatDecimal(raw as number, a.precision);
    case 'int':
    case 'bigint':
      return f.formatInteger(raw as number);
    case 'bool':
      return a.options.find((o) => o.value === (raw ? 1 : 0))?.label ?? String(raw);
    case 'choice':
      return label(raw as number);
    case 'choices':
      return (raw as number[]).map(label).join('; ');
    case 'datetime': {
      // A User Local value is an instant shown in the user's zone; the others carry their clock in the browser's fields.
      const formatting = isUserLocal(a) ? createFormatting(options.locale ?? 'en-US', { timeZone: zone, dateFormats: options.dateFormats }) : f;
      return formatting.formatDateShort(raw as Date, !a.dateOnly);
    }
    case 'lookup':
    case 'partylist':
      return (raw as ClientLookup[])[0]?.name ?? '';
    default:
      return String(raw);
  }
}

const NUMBER_TYPES = /^(Whole\.|Decimal$|FP$|Currency$)/;

/** An input parameter given as a literal (`static="true"`) or a `default-value`: numbers and two options take their type. */
export function literalParameter(value: string, type: string | undefined): PcfParameter {
  let raw: unknown = value;
  if (type && NUMBER_TYPES.test(type)) {
    const n = Number(value);
    raw = value.trim() !== '' && Number.isFinite(n) ? n : null;
  } else if (type === 'TwoOptions') {
    raw = value.trim().toLowerCase() === 'true' || value.trim() === '1';
  }
  return type ? { raw, formatted: value, type } : { raw, formatted: value };
}
