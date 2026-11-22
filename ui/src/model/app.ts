/** One function of the app OnLoad setting: a web resource library and the function it exports. */
export interface AppOnLoadHandler {
  library: string;
  function: string;
}

/** The timeline options of an app (the app settings `xrmverse_TimelineGroupByDate`, `xrmverse_TimelineRememberFilters` and `xrmverse_TimelinePosts`). */
export interface TimelineOptions {
  /** Group the timeline's records under date headings. */
  groupByDate: boolean;
  /** Remember the filters a user chose on the timeline. */
  rememberFilters: boolean;
  /** Show the posts of the record (and their filters and counts) in the timeline. */
  posts: boolean;
}

/** A model-driven app as the UI needs it (from `appmodule` and `appmodulecomponent` rows). */
export interface AppModel {
  id: string;
  uniqueName: string;
  name: string;
  description?: string;
  clientType: number;
  siteMapId?: string;
  components: { entities: string[]; views: string[]; forms: string[] };
  /** The app settings AsyncOnLoad and AsyncOnSave (`appsetting` rows of the app); off when the app has none. */
  asyncHandlers?: { onLoad: boolean; onSave: boolean };
  /** The timeline app settings (`appsetting` rows of the app); both off when the app has none. */
  timeline?: TimelineOptions;
  /**
   * The look settings `xrmverse_Renderer` and `xrmverse_Presentation`, already merged: the app's `appsetting` value, else the
   * environment's `organizationsetting`, else the definition's default. An empty string counts as not set (undefined here).
   * The user's own choice is applied on top by `resolveRenderer`.
   */
  presentation?: { renderer?: string; overlay?: string };
  /** The setting `xrmverse_LookupOpenMode` (app, else environment, else default): how forms open related records. */
  recordOpenMode?: 'sidepane' | 'page';
  /** The `xrmverse_AppOnLoad` setting, parsed; undefined when the setting is empty, `{ error }` when its value is malformed. */
  appOnLoad?: { handlers: AppOnLoadHandler[] } | { error: string };
}
