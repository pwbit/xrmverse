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
