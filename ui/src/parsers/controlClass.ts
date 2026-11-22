import type { Control } from '../model/form';

/*
 * Control classids of FormXml. Learn (power-apps/developer/model-driven-apps/clientapi/reference/
 * controls/getcontroltype and the form-designer pages) names the control types but does not list
 * the GUIDs, so these values are FROM MEMORY and confirmed against the OpenProject365 export, which
 * uses the subgrid and timeline classids. Quick view, web resource and IFrame are from memory only.
 * Each has a row in the manual checklist.
 */
export const CLASS_ID = {
  subgrid: '{E7A81278-8635-4D9E-8D4D-59480B391C5B}',
  quickView: '{5C5600E0-1D6E-4205-A272-BE80DA87FD42}',
  timeline: '{06375649-C143-495E-A496-C962E5B4488E}',
  webResource: '{9FDF5F91-88B1-47F4-AD53-C11EFC01A01D}',
  iframe: '{FD2A7985-3187-444E-908D-6624B21F69C0}',
  /*
   * Field controls, written by the form designer for a new field cell (as Dataverse writes them, in mixed case).
   * Confirmed against the fixtures: text, memo, choice, lookup, dateTime, money, decimal, twoOptions (radio).
   * From memory only: integer, float, multiSelect, partyList.
   */
  text: '{4273EDBD-AC1D-40d3-9FB2-095C621B552D}',
  memo: '{E0DECE4B-6FC8-4a8f-A065-082708572369}',
  choice: '{3EF39988-22BB-4f0b-BBBE-64B5A3748AEE}',
  multiSelect: '{4AA28AB7-9C13-4F57-A73D-AD894D048B5F}',
  lookup: '{270BD3DB-D9AF-4782-9025-509E298DEC0A}',
  partyList: '{CBFB742C-14E7-4a17-96BB-1A13F7F64AA2}',
  dateTime: '{5B773807-9FB2-42db-97C3-7A91EFF8ADFF}',
  money: '{533B9E00-756B-4312-95A0-DC888637AC78}',
  decimal: '{C3EFE0C3-0EC6-42be-8349-CBD9079DFD8E}',
  integer: '{C6D124CA-7EDA-4a60-AEA9-7FB8D318B68F}',
  float: '{0D2C745A-E5A8-4c8f-BA63-C6D3BB604660}',
  twoOptions: '{67FAC785-CD58-4f9f-ABB3-4B7DDC6ED5ED}',
} as const;

const KINDS = new Map<string, Control['kind']>([
  [CLASS_ID.subgrid, 'subgrid'],
  [CLASS_ID.quickView, 'quickview'],
  [CLASS_ID.timeline, 'timeline'],
  [CLASS_ID.webResource, 'webresource'],
  [CLASS_ID.iframe, 'webresource'],
]);

/**
 * The control kind for a classid (case-insensitive). Any other classid is `field`; the parser
 * turns a classid-unknown control without an attribute into `unknown`.
 */
export function controlKind(classId: string): Control['kind'] | 'field' {
  return KINDS.get(classId.trim().toUpperCase()) ?? 'field';
}
