import type { AppModel } from '../model/app';

/** The columns of a Web API `appmodule` row that the UI reads. */
export interface AppModuleRow {
  appmoduleid: string;
  uniquename: string;
  name: string;
  description?: string | null;
  clienttype: number;
}

export interface AppModuleComponentRow {
  componenttype: number;
  objectid: string;
}

const ENTITY = 1;
const VIEW = 26;
const FORM = 60;
const SITEMAP = 62;

export function parseAppModule(row: AppModuleRow, components: AppModuleComponentRow[]): AppModel {
  const ids = (type: number) => components.filter((c) => c.componenttype === type).map((c) => c.objectid);
  const model: AppModel = {
    id: row.appmoduleid,
    uniqueName: row.uniquename,
    name: row.name,
    clientType: row.clienttype,
    components: { entities: ids(ENTITY), views: ids(VIEW), forms: ids(FORM) },
  };
  if (row.description) model.description = row.description;
  const siteMapId = ids(SITEMAP)[0];
  if (siteMapId) model.siteMapId = siteMapId;
  return model;
}
