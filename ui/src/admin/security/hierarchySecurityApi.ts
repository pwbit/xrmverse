import type { XrmApi } from '../../shell/api/client';

/** `GET|PUT /admin/hierarchy-security`. */
export interface HierarchySecurity {
  enabled: boolean;
  depth: number;
  excludedTables: string[];
}

export const loadHierarchySecurity = (api: XrmApi): Promise<HierarchySecurity> =>
  api.admin<HierarchySecurity>('GET', 'hierarchy-security');

/** Replaces the three settings; answers the settings as saved. A refusal (unknown table, depth out of range) is an XrmError. */
export const saveHierarchySecurity = (api: XrmApi, settings: HierarchySecurity): Promise<HierarchySecurity> =>
  api.admin<HierarchySecurity>('PUT', 'hierarchy-security', settings);
