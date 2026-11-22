// Helpers shared by formXml.test.ts and formXml.op365.test.ts (the tests on the OpenProject365 forms, kept out of the
// public copy of the repository, see eng/public-export/manifest.txt).
import type { Cell, FormModel } from '../model/form';
import { parseFormXml } from './formXml';

/** A systemform row with the form XML <paramref formxml>: an OpenProject365 project main form unless overridden. */
export function parseForm(name: string, formxml: string, over: Record<string, unknown> = {}): FormModel {
  return parseFormXml({
    systemformid: '{00000000-0000-0000-0000-000000000001}',
    name: name,
    objecttypecode: 'op365_project',
    type: 2,
    isdefault: false,
    formxml,
    ...over,
  });
}

export const allCells = (f: FormModel): Cell[] =>
  f.tabs.flatMap((t) =>
    t.columns.flatMap((c) => c.sections.flatMap((s) => s.rows.flatMap((r) => r))),
  );

export const wrap = (cell: string, extra = '') =>
  `<form><tabs><tab name="t"><labels/><columns><column width="100%"><sections><section name="s" columns="1"><labels/><rows><row>${cell}</row></rows></section></sections></column></columns></tab></tabs>${extra}</form>`;

export const rowOf = (formxml: string) =>
  parseFormXml({
    systemformid: 'x',
    name: 'x',
    objecttypecode: 'account',
    type: 2,
    isdefault: undefined,
    formxml,
  });
