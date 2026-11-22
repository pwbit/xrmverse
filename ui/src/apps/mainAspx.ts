/** The state a `/main.aspx` URL carries (the Dataverse parameters; names are read in any case). */
export interface MainAspxState {
  appId?: string;
  /** Lower-case `pagetype`; empty for the app home. */
  pageType: string;
  etn?: string;
  viewId?: string;
  viewType?: number;
  id?: string;
  /** `formid` of an entity record page. */
  formId?: string;
  /** `webresourceName` of a web resource page, `name` of a custom page. */
  name?: string;
  /** The `data` parameter of a web resource page. */
  data?: string;
  /** `extraqs` of a create form: the column values it starts with (`Xrm.Navigation.openForm` form parameters). */
  extraqs?: Record<string, string>;
}

/** A GUID as written in a URL (`{…}` or `%7b…%7d`, any case) to the bare lower-case form. */
const guid = (value: string) => value.replace(/^\{|\}$/g, '').toLowerCase();

export function parseMainAspx(search: string): MainAspxState {
  const params = new URLSearchParams(search.startsWith('?') ? search.slice(1) : search);
  const get = (name: string) => {
    for (const [key, value] of params) if (key.toLowerCase() === name && value !== '') return value;
    return undefined;
  };
  const state: MainAspxState = { pageType: (get('pagetype') ?? '').toLowerCase() };
  const appId = get('appid');
  if (appId) state.appId = guid(appId);
  const etn = get('etn');
  if (etn) state.etn = etn.toLowerCase();
  const viewId = get('viewid');
  if (viewId) state.viewId = guid(viewId);
  const viewType = Number.parseInt(get('viewtype') ?? '', 10);
  if (Number.isFinite(viewType)) state.viewType = viewType;
  const id = get('id');
  if (id) state.id = guid(id);
  const formId = get('formid');
  if (formId) state.formId = guid(formId);
  const name = get('webresourcename') ?? get('name');
  if (name) state.name = name;
  const data = get('data');
  if (data !== undefined) state.data = data;
  const extraqs = get('extraqs');
  if (extraqs) state.extraqs = Object.fromEntries(new URLSearchParams(extraqs));
  return state;
}

/** `/main.aspx?appid=…&pagetype=…&etn=…&viewid=…&viewType=…&id=…&formid=…&extraqs=…`, leaving out what is unset. */
export function mainAspxUrl(state: MainAspxState): string {
  const params = new URLSearchParams();
  if (state.appId) params.set('appid', state.appId);
  if (state.pageType) params.set('pagetype', state.pageType);
  if (state.etn) params.set('etn', state.etn);
  if (state.viewId) params.set('viewid', state.viewId);
  if (state.viewType !== undefined) params.set('viewType', String(state.viewType));
  if (state.id) params.set('id', state.id);
  if (state.formId) params.set('formid', state.formId);
  if (state.name) params.set(state.pageType === 'webresource' ? 'webresourceName' : 'name', state.name);
  if (state.data !== undefined) params.set('data', state.data);
  if (state.extraqs && Object.keys(state.extraqs).length > 0) params.set('extraqs', new URLSearchParams(state.extraqs).toString());
  const query = params.toString();
  return query ? `/main.aspx?${query}` : '/main.aspx';
}
