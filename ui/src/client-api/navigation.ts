import type { FieldValue, FormValues, LookupValue } from '../admin/tables/formValues';
import { isNumeric, type AttributeMeta } from '../admin/tables/metadata';
import { mainAspxUrl, type MainAspxState } from '../apps/mainAspx';
import type { DialogHost, DialogLength } from './DialogHost';
import { notAvailable, Stage, stubMembers } from './notImplemented';
import { isWebResourceName, webResourceUrl } from './scriptHost';
import { plainId } from './values';

/** What `Xrm.Navigation` opens pages and dialogs with. */
export interface NavigationEnv {
  appId: string;
  /** Moves the app shell to a `/main.aspx` address (through the router: the unsaved-changes guard applies). */
  navigate(url: string): void;
  dialogs: DialogHost;
  /** `window.open`; replaced in tests. */
  open?: (url: string, target?: string, features?: string) => unknown;
}

const LOGICAL_NAME = /^[A-Za-z_][A-Za-z0-9_]*$/;
const VIEW_TYPES: Record<string, number> = { savedquery: 1039, userquery: 4230 };

const tableName = (member: string, value: unknown): string => {
  if (typeof value !== 'string' || !LOGICAL_NAME.test(value)) throw new Error(`${member}: entityName ${String(value)} is not a table name.`);
  return value.toLowerCase();
};

const webResourceName = (member: string, value: unknown): string => {
  if (!isWebResourceName(value)) {
    throw new Error(`${member}: ${String(value)} is not a web resource name.`);
  }
  return value;
};

/** The form parameters of `openForm` (and `data` of an entity record page) as the `extraqs` strings of the URL. */
function extraqsOf(member: string, parameters: unknown): Record<string, string> | undefined {
  if (parameters === undefined || parameters === null) return undefined;
  if (typeof parameters !== 'object' || Array.isArray(parameters)) throw new Error(`${member}: the form parameters must be an object.`);
  const out: Record<string, string> = {};
  for (const [key, value] of Object.entries(parameters as Record<string, unknown>)) {
    if (value === undefined) continue;
    out[key] = value === null ? '' : value instanceof Date ? value.toISOString() : String(value);
  }
  return out;
}

/** Window features of Learn's `{ width, height }` options. */
function features(options: unknown, base: string[] = []): string | undefined {
  const o = (options ?? {}) as { width?: unknown; height?: unknown };
  const parts = [...base];
  if (typeof o.width === 'number' && o.width > 0) parts.push(`width=${Math.round(o.width)}`);
  if (typeof o.height === 'number' && o.height > 0) parts.push(`height=${Math.round(o.height)}`);
  return parts.length ? parts.join(',') : undefined;
}

/** `Xrm.Navigation` (Learn). Pages open inside the app shell; dialogs come from the shell's dialog host. */
export function createNavigation(env: NavigationEnv): Record<string, unknown> {
  const openWindow = env.open ?? ((url: string, target?: string, f?: string) => window.open(url, target, f));

  /** The entity record page of `openForm` and of `navigateTo` with `entityrecord`. */
  const recordUrl = (member: string, o: Record<string, unknown>, parameters: unknown): string => {
    const etn = tableName(member, o.entityName);
    if (o.useQuickCreateForm === true) throw notAvailable(`${member} with useQuickCreateForm`, Stage.later);
    // Mapping from a parent record needs InitializeFrom, which xrmverse does not offer yet.
    if (o.createFromEntity !== undefined && o.createFromEntity !== null) throw notAvailable(`${member} with createFromEntity`, Stage.later);
    for (const key of ['relationship', 'processId', 'processInstanceId', 'selectedStageId', 'isCrossEntityNavigate', 'isOfflineSyncError', 'tabName']) {
      if (o[key] !== undefined && o[key] !== null) throw notAvailable(`${member} with ${key}`, Stage.later);
    }
    const state: MainAspxState = { appId: env.appId, pageType: 'entityrecord', etn };
    if (o.entityId !== undefined && o.entityId !== null && o.entityId !== '') state.id = plainId(String(o.entityId));
    if (typeof o.formId === 'string' && o.formId) state.formId = plainId(o.formId);
    const extraqs = extraqsOf(member, parameters);
    if (extraqs && Object.keys(extraqs).length) state.extraqs = extraqs;
    return mainAspxUrl(state);
  };

  const openForm = async (entityFormOptions: unknown, formParameters?: unknown): Promise<void> => {
    const member = 'Xrm.Navigation.openForm';
    if (!entityFormOptions || typeof entityFormOptions !== 'object') throw new Error(`${member}: entityFormOptions with an entityName is required.`);
    const o = entityFormOptions as Record<string, unknown>;
    const url = recordUrl(member, o, formParameters);
    if (o.openInNewWindow === true) openWindow(url, '_blank', features(o));
    else env.navigate(url);
  };

  const navigateTo = async (pageInput: unknown, navigationOptions?: unknown): Promise<unknown> => {
    const member = 'Xrm.Navigation.navigateTo';
    if (!pageInput || typeof pageInput !== 'object') throw new Error(`${member}: pageInput is required.`);
    const p = pageInput as Record<string, unknown>;
    const o = (navigationOptions ?? {}) as { target?: unknown; width?: DialogLength; height?: DialogLength; position?: unknown; title?: unknown };
    const dialog = o.target === 2;
    if (o.target !== undefined && o.target !== 1 && o.target !== 2) throw new Error(`${member}: target ${String(o.target)} is not 1 (inline) or 2 (dialog).`);
    switch (p.pageType) {
      case 'entitylist': {
        // Learn: only inline.
        if (dialog) throw new Error(`${member}: an entity list opens inline only.`);
        const etn = tableName(member, p.entityName);
        const state: MainAspxState = { appId: env.appId, pageType: 'entitylist', etn };
        if (typeof p.viewId === 'string' && p.viewId) {
          state.viewId = plainId(p.viewId);
          state.viewType = VIEW_TYPES[String(p.viewType ?? 'savedquery')] ?? VIEW_TYPES.savedquery;
        }
        env.navigate(mainAspxUrl(state));
        return undefined;
      }
      case 'entityrecord': {
        const url = recordUrl(member, p, p.data);
        if (!dialog) {
          env.navigate(url);
          return undefined;
        }
        const saved = await env.dialogs.record({
          url,
          title: typeof o.title === 'string' ? o.title : undefined,
          width: o.width,
          height: o.height,
          position: o.position === 2 ? 2 : 1,
        });
        // Learn: a record opened in create mode resolves with the records saved (savedEntityReference); others with nothing.
        const creating = p.entityId === undefined || p.entityId === null || p.entityId === '';
        return creating ? { savedEntityReference: saved ? [saved] : [] } : undefined;
      }
      case 'webresource': {
        const name = webResourceName(member, p.webresourceName);
        const data = p.data === undefined || p.data === null ? undefined : String(p.data);
        if (dialog) {
          await env.dialogs.webResource({
            name,
            data,
            title: typeof o.title === 'string' ? o.title : undefined,
            width: o.width,
            height: o.height,
            position: o.position === 2 ? 2 : 1,
          });
          // Learn: a web resource dialog returns no value when it closes.
          return undefined;
        }
        env.navigate(mainAspxUrl({ appId: env.appId, pageType: 'webresource', name, data }));
        return undefined;
      }
      case 'dashboard':
      case 'custom':
      case 'generative':
        throw notAvailable(`${member} with pageType "${p.pageType}"`, Stage.later);
      default:
        throw new Error(`${member}: ${String(p.pageType)} is not a page type.`);
    }
  };

  const openUrl = (url: unknown, openUrlOptions?: unknown): void => {
    let target: URL | undefined;
    try {
      target = typeof url === 'string' ? new URL(url.trim(), location.href) : undefined;
    } catch {
      target = undefined;
    }
    // Only web addresses: a script scheme (javascript:, data:, vbscript:) or a file is refused, never opened.
    if (!target || (target.protocol !== 'http:' && target.protocol !== 'https:')) {
      throw new Error(`Xrm.Navigation.openUrl: only http and https addresses can be opened (${String(url)}).`);
    }
    openWindow(target.href, '_blank', features(openUrlOptions, ['noopener', 'noreferrer']));
  };

  const openWebResource = (webResource: unknown, windowOptions?: unknown, data?: unknown): void => {
    // Learn: other parameters than `data` come appended to the name (`page.htm?typename=account`); the query is not part of it.
    const [base, ...rest] = typeof webResource === 'string' ? webResource.split('?') : [webResource];
    const name = webResourceName('Xrm.Navigation.openWebResource', base);
    const params = new URLSearchParams(rest.join('?'));
    if (data !== undefined && data !== null && data !== '') params.set('data', String(data));
    const query = params.toString() ? `?${params.toString()}` : '';
    // Same origin and an opener: the page reaches the shell's Xrm through window.opener (ClientGlobalContext.js.aspx).
    openWindow(`${webResourceUrl(name)}${query}`, '_blank', features(windowOptions));
  };

  const text = (v: unknown) => (typeof v === 'string' ? v : v === undefined || v === null ? '' : String(v));
  const navigation = {
    openForm,
    navigateTo,
    openAlertDialog: async (alertStrings: unknown, alertOptions?: unknown): Promise<void> => {
      const s = (alertStrings ?? {}) as Record<string, unknown>;
      const o = (alertOptions ?? {}) as { width?: DialogLength; height?: DialogLength };
      await env.dialogs.alert(
        { text: text(s.text), title: s.title ? text(s.title) : undefined, confirmButtonLabel: s.confirmButtonLabel ? text(s.confirmButtonLabel) : undefined },
        { width: o.width, height: o.height },
      );
    },
    openConfirmDialog: async (confirmStrings: unknown, confirmOptions?: unknown): Promise<{ confirmed: boolean }> => {
      const s = (confirmStrings ?? {}) as Record<string, unknown>;
      const o = (confirmOptions ?? {}) as { width?: DialogLength; height?: DialogLength };
      const confirmed = await env.dialogs.confirm(
        {
          text: text(s.text),
          title: s.title ? text(s.title) : undefined,
          subtitle: s.subtitle ? text(s.subtitle) : undefined,
          confirmButtonLabel: s.confirmButtonLabel ? text(s.confirmButtonLabel) : undefined,
          cancelButtonLabel: s.cancelButtonLabel ? text(s.cancelButtonLabel) : undefined,
        },
        { width: o.width, height: o.height },
      );
      return { confirmed };
    },
    openErrorDialog: async (errorOptions: unknown): Promise<void> => {
      const e = (errorOptions ?? {}) as Record<string, unknown>;
      await env.dialogs.error({
        message: e.message === undefined ? undefined : text(e.message),
        details: e.details === undefined ? undefined : text(e.details),
        errorCode: typeof e.errorCode === 'number' ? e.errorCode : undefined,
      });
    },
    openUrl,
    openWebResource,
  };
  stubMembers(navigation, 'Xrm.Navigation');
  return navigation;
}

const TRUE = new Set(['1', 'true']);
const FALSE = new Set(['0', 'false']);

/** One `extraqs` value as the editor value of its column; undefined when it does not fit the column. */
function parameterValue(a: AttributeMeta, raw: string, params: Record<string, string>, navFor: (attribute: string, target: string) => string | undefined): FieldValue | undefined {
  if (raw === '') return a.kind === 'choices' ? [] : null;
  switch (a.kind) {
    case 'string':
    case 'memo':
      return raw;
    case 'bool': {
      const v = raw.trim().toLowerCase();
      return TRUE.has(v) ? true : FALSE.has(v) ? false : undefined;
    }
    case 'choice': {
      const n = Number(raw);
      return Number.isInteger(n) ? n : undefined;
    }
    case 'choices': {
      const values = raw.split(',').map((x) => Number(x.trim()));
      return values.every(Number.isInteger) ? values : undefined;
    }
    case 'datetime': {
      const m = /^(\d{4}-\d{2}-\d{2})(?:[T ](\d{2}:\d{2}))?/.exec(raw.trim());
      if (!m) return undefined;
      return a.dateOnly || !m[2] ? m[1]! : `${m[1]}T${m[2]}`;
    }
    case 'lookup': {
      const id = plainId(raw);
      if (!/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/.test(id)) return undefined;
      const target = params[`${a.logicalName}type`] ?? (a.targets.length === 1 ? a.targets[0] : undefined);
      if (!target || (a.targets.length > 0 && !a.targets.includes(target))) return undefined;
      const lookup: LookupValue = { id, name: params[`${a.logicalName}name`], target, nav: navFor(a.logicalName, target) };
      return lookup;
    }
    default:
      if (isNumeric(a.kind)) return raw.trim() !== '' && Number.isFinite(Number(raw)) ? raw.trim() : undefined;
      return undefined;
  }
}

/**
 * The create form's starting values from the URL's `extraqs` (Learn, set column values using parameters passed to a form): each
 * parameter names a column the form shows; a lookup takes its name and table from `{column}name` and `{column}type`. A parameter
 * that names no such column, or whose value does not fit it, is listed in `ignored` (the page says so).
 */
export function formParameterValues(
  attributes: AttributeMeta[],
  formColumns: string[],
  params: Record<string, string>,
  navFor: (attribute: string, target: string) => string | undefined,
): { values: FormValues; ignored: string[] } {
  const byName = new Map(attributes.filter((a) => formColumns.includes(a.logicalName)).map((a) => [a.logicalName, a]));
  const lookupParts = new Set([...byName.values()].filter((a) => a.kind === 'lookup').flatMap((a) => [`${a.logicalName}name`, `${a.logicalName}type`]));
  const values: FormValues = {};
  const ignored: string[] = [];
  // Parameter names are matched without case, as the column names are (`op365_ParentIdName` is `op365_parentidname`).
  const lower = Object.fromEntries(Object.entries(params).map(([k, v]) => [k.toLowerCase(), v]));
  for (const [key, raw] of Object.entries(params)) {
    const name = key.toLowerCase();
    if (lookupParts.has(name) && !byName.has(name)) continue;
    const a = byName.get(name);
    const value = a ? parameterValue(a, raw, lower, navFor) : undefined;
    if (value === undefined) ignored.push(key);
    else values[name] = value;
  }
  return { values, ignored };
}
