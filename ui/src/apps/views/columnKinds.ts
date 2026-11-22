import type { XrmApi } from '../../shell/api/client';
import { displayLabel, quote } from '../loadViews';

export interface ChoiceOption {
  value: number | boolean;
  label: string;
  /** statuscode only: its statecode. */
  state?: number;
  /** The option's colour, `#RRGGBB`. */
  color?: string;
}

export type ColumnKind =
  | {
      kind: 'choice';
      type: 'Picklist' | 'MultiSelectPicklist' | 'Status' | 'State' | 'Boolean';
      options: ChoiceOption[];
      /** The name of the global choice, when the column uses one. */
      globalName?: string;
    }
  | { kind: 'date'; dateOnly: boolean; zoneIndependent: boolean }
  | { kind: 'other' };

type Name = Parameters<typeof displayLabel>[0];
interface RawOption {
  Value: number;
  Label?: Name;
  State?: number | null;
  Color?: string | null;
}
interface RawOptionSet {
  Name?: string | null;
  Options?: RawOption[];
  TrueOption?: RawOption;
  FalseOption?: RawOption;
}
interface TypedRow {
  OptionSet?: RawOptionSet | null;
  GlobalOptionSet?: RawOptionSet | null;
  DateTimeBehavior?: { Value?: string | null } | null;
}

const CAST = 'Microsoft.Dynamics.CRM.';
const CHOICE_TYPES = ['Picklist', 'MultiSelectPicklist', 'Status', 'State', 'Boolean'] as const;
type ChoiceType = (typeof CHOICE_TYPES)[number];

/** Kinds of `names` on `table` (labels in `lcid`, else 1033); a name the table lacks is absent from the result. */
export async function loadColumnKinds(
  api: XrmApi,
  table: string,
  names: string[],
  lcid: number,
): Promise<Record<string, ColumnKind>> {
  const kinds: Record<string, ColumnKind> = {};
  if (names.length === 0) return kinds;
  const attributes = `EntityDefinitions(LogicalName='${quote(table)}')/Attributes`;
  const filter = names
    .map((n) => `LogicalName eq '${quote(n)}'`)
    .join(' or ');
  const list = await api.get<{
    value: { LogicalName: string; AttributeType?: string; AttributeTypeName?: { Value?: string } | null }[];
  }>(
    `${attributes}?$filter=${filter}&$select=LogicalName,AttributeType,AttributeTypeName`,
  );
  const typed = (name: string, cast: string, query: string) =>
    api.get<TypedRow>(
      `${attributes}(LogicalName='${quote(name)}')/${CAST}${cast}?${query}`,
    );
  const option = (o: RawOption, value: number | boolean): ChoiceOption => ({
    value,
    label: displayLabel(o.Label, lcid) ?? String(o.Value),
    ...(typeof o.State === 'number' ? { state: o.State } : {}),
    ...(typeof o.Color === 'string' && o.Color ? { color: o.Color } : {}),
  });

  // The server's attribute list ignores $filter (Dataverse honours it): keep only the requested names.
  const wanted = new Set(names);
  await Promise.all(
    list.value
      .filter((a) => wanted.has(a.LogicalName))
      .map(async (a) => {
        try {
          await classify(a);
        } catch (e) {
          console.warn(
            '[xrmverse presentation]',
            `Column '${a.LogicalName}' of '${table}' could not be classified: ${e instanceof Error ? e.message : String(e)}`,
          );
          kinds[a.LogicalName] = { kind: 'other' };
        }
      }),
  );
  return kinds;

  async function classify(a: {
    LogicalName: string;
    AttributeType?: string;
    AttributeTypeName?: { Value?: string } | null;
  }) {
    // A multi-select column is a Virtual attribute told apart by its AttributeTypeName.
    const type = a.AttributeTypeName?.Value === 'MultiSelectPicklistType' ? 'MultiSelectPicklist' : (a.AttributeType ?? '');
    const name = a.LogicalName;
    if (type === 'DateTime') {
      const row = await typed(
        name,
        'DateTimeAttributeMetadata',
        '$select=DateTimeBehavior,Format',
      );
      kinds[name] = {
        kind: 'date',
        dateOnly: row.DateTimeBehavior?.Value === 'DateOnly',
        zoneIndependent: row.DateTimeBehavior?.Value === 'TimeZoneIndependent',
      };
    } else if (type === 'Boolean') {
      const set = (
        await typed(
          name,
          'BooleanAttributeMetadata',
          '$select=LogicalName&$expand=OptionSet',
        )
      ).OptionSet;
      const options = [
        ...(set?.FalseOption ? [option(set.FalseOption, false)] : []),
        ...(set?.TrueOption ? [option(set.TrueOption, true)] : []),
      ];
      kinds[name] = { kind: 'choice', type: 'Boolean', options };
    } else if ((CHOICE_TYPES as readonly string[]).includes(type)) {
      // A plain $expand returns every property of the option set, Color and (global) Name included.
      const expand =
        type === 'Picklist' || type === 'MultiSelectPicklist'
          ? 'OptionSet,GlobalOptionSet'
          : 'OptionSet';
      const row = await typed(
        name,
        `${type}AttributeMetadata`,
        `$select=LogicalName&$expand=${expand}`,
      );
      // A global choice is returned under GlobalOptionSet, a local one under OptionSet.
      const raw =
        row.GlobalOptionSet?.Options ?? row.OptionSet?.Options ?? [];
      const globalName = row.GlobalOptionSet?.Name;
      kinds[name] = {
        kind: 'choice',
        type: type as ChoiceType,
        options: raw.map((o) => option(o, o.Value)),
        ...(globalName ? { globalName } : {}),
      };
    } else {
      kinds[name] = { kind: 'other' };
    }
  }
}
