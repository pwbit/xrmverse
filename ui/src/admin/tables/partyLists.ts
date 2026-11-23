/**
 * Party lists of activities (plan 2.11, A8). A party list column (`to`, `from`, ...) is not a property of the record on the
 * Web API: its rows are the `activityparty` records of the collection-valued navigation property `{activity}_activity_parties`,
 * each with its `participationtypemask`. The participation type of each column mirrors `ActivitySchema.PartyLists` on the
 * server (src/Xrmd.Metadata/ActivitySchema.cs); a column missing here is shown as unsupported, never guessed.
 */
export const PARTY_TYPE_OF: Readonly<Record<string, number>> = {
  from: 1,
  to: 2,
  cc: 3,
  bcc: 4,
  requiredattendees: 5,
  optionalattendees: 6,
  organizer: 7,
  resources: 10,
  customers: 11,
  related: 13,
};

/** The party columns the form reads inside the `$expand` of the party navigation. */
export const PARTY_SELECT = 'participationtypemask,_partyid_value,addressused';

const FORMATTED = '@OData.Community.Display.V1.FormattedValue';
const LOOKUP_LOGICAL_NAME = '@Microsoft.Dynamics.CRM.lookuplogicalname';

export interface PartyRow {
  participationtypemask?: number;
  _partyid_value?: string | null;
  addressused?: string | null;
  [annotation: string]: unknown;
}

/**
 * What a party row shows. A row with a party: its name and table (`Ada Lovelace (contact)`), from the lookup's annotations;
 * without a name, the table and the id. A row without a party (an unresolved address): `addressused`. Never empty:
 * `unknown` when the row carries neither.
 */
export function partyText(row: PartyRow, unknown: string): string {
  const id = row._partyid_value;
  if (id) {
    const table = row[`_partyid_value${LOOKUP_LOGICAL_NAME}`];
    const name = row[`_partyid_value${FORMATTED}`];
    const type = typeof table === 'string' && table ? table : undefined;
    if (typeof name === 'string' && name) return type ? `${name} (${type})` : name;
    return type ? `${type} ${id}` : id;
  }
  return row.addressused || unknown;
}

/** The rows of the party list `column` among the record's party rows (in the server's order). */
export function partiesOf(column: string, rows: PartyRow[]): PartyRow[] {
  const type = PARTY_TYPE_OF[column];
  return type === undefined ? [] : rows.filter((r) => r.participationtypemask === type);
}

/**
 * One party of a party list column on a model-driven form (plan 4.1b, task 9): a resolved record (`id` and `logicalName`, with
 * its name and the address the server used) or an address alone that matches no record (`address`, an unresolved party).
 */
export interface Party {
  id?: string;
  logicalName?: string;
  name?: string;
  address?: string;
}

const bare = (id: string) => id.replace(/[{}]/g, '').toLowerCase();

/** The parties of the party list `column` among the record's party rows, in the server's order. */
export function partyValuesOf(column: string, rows: PartyRow[]): Party[] {
  return partiesOf(column, rows).map((row) => {
    const id = row._partyid_value;
    const address = row.addressused || undefined;
    if (!id) return { address };
    const table = row[`_partyid_value${LOOKUP_LOGICAL_NAME}`];
    const name = row[`_partyid_value${FORMATTED}`];
    return {
      id,
      ...(typeof table === 'string' && table ? { logicalName: table } : {}),
      ...(typeof name === 'string' && name ? { name } : {}),
      ...(address ? { address } : {}),
    };
  });
}

/** What makes two parties the same: the record (table and id), else the address, ignoring case. */
export const partyKey = (p: Party): string => (p.id ? `${p.logicalName ?? ''}:${bare(p.id)}` : `@${(p.address ?? '').trim().toLowerCase()}`);

/** Two lists hold the same parties in the same order. */
export const sameParties = (a: readonly Party[], b: readonly Party[]): boolean => a.length === b.length && a.every((p, i) => partyKey(p) === partyKey(b[i]!));

/** `list` with the parties of `added` it does not hold yet (a record or an address is listed once). */
export function addParties(list: readonly Party[], added: readonly Party[]): Party[] {
  const seen = new Set(list.map(partyKey));
  const result = [...list];
  for (const p of added) {
    const key = partyKey(p);
    if (seen.has(key)) continue;
    seen.add(key);
    result.push(p);
  }
  return result;
}

/** The addresses of a pasted or typed text, separated by `;` or `,`. */
export const splitAddresses = (text: string): string[] =>
  text
    .split(/[;,]/)
    .map((s) => s.trim())
    .filter(Boolean);

/** A text shaped like an email address (a name, `@`, a domain with a dot); the server stays the authority. */
export const isEmailAddress = (text: string): boolean => /^[^\s@;,]+@[^\s@;,]+\.[^\s@;,]+$/.test(text.trim());

/**
 * The items of the party navigation (`{activity}_activity_parties`) that write `parties` into `column`: a record is bound
 * (`partyid_{table}@odata.bind`, with the entity set `setOf` gives), an address alone is sent as `addressused`.
 */
export function partyItems(column: string, parties: readonly Party[], setOf: (table: string) => string | undefined): Record<string, unknown>[] {
  const mask = PARTY_TYPE_OF[column];
  if (mask === undefined) throw new Error(`'${column}' is not a party list.`);
  return parties.map((p) => {
    if (!p.id) return { participationtypemask: mask, addressused: p.address ?? '' };
    const table = p.logicalName ?? '';
    const set = setOf(table);
    if (!set) throw new Error(`The table '${table}' of a party has no entity set.`);
    return { participationtypemask: mask, [`partyid_${table}@odata.bind`]: `/${set}(${bare(p.id)})` };
  });
}
