import { collection } from './collection';
import { Stage, stub, stubProperty } from './notImplemented';

/**
 * `Xrm.Utility.getEntityMetadata` in Learn's shape (Learn, getEntityMetadata): labels are strings in the user's language,
 * managed properties are their values, `Privileges[].PrivilegeType` is a number, `Attributes` is a collection, and each column
 * carries Learn's members for its type. A member Learn lists that the server's definitions do not give in that type throws
 * "not available" with its path: a script never reads a value of another type.
 */

type Raw = Record<string, unknown>;
type Kind = 'string' | 'boolean' | 'number' | 'label';

const PATH = 'Xrm.Utility.getEntityMetadata()';

/** Learn's table properties and their types. */
const ENTITY_PROPERTIES: Record<string, Kind> = {
  ActivityTypeMask: 'number',
  AutoRouteToOwnerQueue: 'boolean',
  CanEnableSyncToExternalSearchIndex: 'boolean',
  CanTriggerWorkflow: 'boolean',
  Description: 'label',
  DisplayCollectionName: 'label',
  DisplayName: 'label',
  EnforceStateTransitions: 'boolean',
  EntityColor: 'string',
  EntitySetName: 'string',
  HasActivities: 'boolean',
  IsActivity: 'boolean',
  IsActivityParty: 'boolean',
  IsBusinessProcessEnabled: 'boolean',
  IsBPFEntity: 'boolean',
  IsChildEntity: 'boolean',
  IsConnectionsEnabled: 'boolean',
  IsCustomEntity: 'boolean',
  IsCustomizable: 'boolean',
  IsDocumentManagementEnabled: 'boolean',
  IsDocumentRecommendationsEnabled: 'boolean',
  IsDuplicateDetectionEnabled: 'boolean',
  IsEnabledForCharts: 'boolean',
  IsImportable: 'boolean',
  IsInteractionCentricEnabled: 'boolean',
  IsKnowledgeManagementEnabled: 'boolean',
  IsMailMergeEnabled: 'boolean',
  IsManaged: 'boolean',
  IsOneNoteIntegrationEnabled: 'boolean',
  IsOptimisticConcurrencyEnabled: 'boolean',
  IsQuickCreateEnabled: 'boolean',
  IsStateModelAware: 'boolean',
  IsValidForAdvancedFind: 'boolean',
  IsVisibleInMobileClient: 'boolean',
  IsEnabledInUnifiedInterface: 'boolean',
  LogicalCollectionName: 'string',
  LogicalName: 'string',
  ObjectTypeCode: 'number',
  OwnershipType: 'string',
  PrimaryIdAttribute: 'string',
  PrimaryImageAttribute: 'string',
  PrimaryNameAttribute: 'string',
};

/** The AttributeTypeCode numbers Learn's `AttributeType` gives (the Web API names them). */
const ATTRIBUTE_TYPE_CODES: Record<string, number> = {
  Boolean: 0,
  Customer: 1,
  DateTime: 2,
  Decimal: 3,
  Double: 4,
  Integer: 5,
  Lookup: 6,
  Memo: 7,
  Money: 8,
  Owner: 9,
  PartyList: 10,
  Picklist: 11,
  State: 12,
  Status: 13,
  String: 14,
  Uniqueidentifier: 15,
  CalendarRules: 16,
  Virtual: 17,
  BigInt: 18,
  ManagedProperty: 19,
  EntityName: 20,
};

/** Learn's Privilege object `PrivilegeType` numbers. */
const PRIVILEGE_TYPES: Record<string, number> = { None: 0, Create: 1, Read: 2, Write: 3, Delete: 4, Assign: 5, Share: 6, Append: 7, AppendTo: 8 };

const isObject = (v: unknown): v is Raw => typeof v === 'object' && v !== null && !Array.isArray(v);

/** A Web API Label in the user's language (then the user's label, then the first); undefined when `v` is no Label. */
function labelOf(v: unknown, lcid: number): string | undefined {
  if (v === null) return '';
  if (!isObject(v) || !('LocalizedLabels' in v || 'UserLocalizedLabel' in v)) return undefined;
  const labels = Array.isArray(v.LocalizedLabels) ? (v.LocalizedLabels as { Label?: string; LanguageCode?: number }[]) : [];
  return labels.find((l) => l.LanguageCode === lcid)?.Label ?? (v.UserLocalizedLabel as { Label?: string } | null)?.Label ?? labels[0]?.Label ?? '';
}

/** `raw` as a value of `kind`; undefined when it is not one (a managed property gives its `Value`). */
function valueOf(raw: unknown, kind: Kind, lcid: number): unknown {
  if (kind === 'label') return labelOf(raw, lcid);
  const v = isObject(raw) && 'Value' in raw ? raw.Value : raw;
  if (v === null) return null;
  return typeof v === kind ? v : undefined;
}

/** Puts `name` on `target` from `raw`, or a "not available" member when the server's definition has no value of Learn's type. */
function define(target: Raw, name: string, value: unknown, path: string): void {
  if (value === undefined) stubProperty(target, name, `${path}.${name}`, Stage.later);
  else Object.defineProperty(target, name, { value, enumerable: true });
}

interface RawOption {
  Value?: number;
  Label?: unknown;
  State?: number | null;
  DefaultStatus?: number | null;
}

/** Learn's OptionSet: each option as a `value: label` pair. */
function optionSetOf(options: RawOption[] | undefined, lcid: number): Record<number, string> | undefined {
  if (!options) return undefined;
  return Object.fromEntries(options.filter((o) => typeof o.Value === 'number').map((o) => [o.Value!, labelOf(o.Label, lcid) ?? '']));
}

const optionsOf = (a: Raw): RawOption[] | undefined => {
  const set = a.OptionSet;
  if (!isObject(set)) return undefined;
  if (Array.isArray(set.Options)) return set.Options as RawOption[];
  // A Yes/No column: its false and true options.
  const pair = [set.FalseOption, set.TrueOption].filter(isObject) as RawOption[];
  return pair.length ? pair : undefined;
};

/** One column in Learn's shape; `all` is every column of the table (a State column reads the Status column's options). */
function attributeOf(a: Raw, entity: string, all: Raw[], lcid: number): Raw {
  const name = String(a.LogicalName);
  const path = `${PATH}.Attributes.get("${name}")`;
  const out: Raw = {};
  const type = typeof a.AttributeType === 'string' ? a.AttributeType : '';
  const code = ATTRIBUTE_TYPE_CODES[type];
  define(out, 'AttributeType', code, path);
  define(out, 'DisplayName', labelOf(a.DisplayName, lcid), path);
  define(out, 'EntityLogicalName', typeof a.EntityLogicalName === 'string' ? a.EntityLogicalName : entity, path);
  define(out, 'LogicalName', name, path);
  const options = optionsOf(a);
  const isMulti = type === 'Virtual' && String(a.AttributeTypeName && (a.AttributeTypeName as Raw).Value) === 'MultiSelectPicklistType';
  if (type === 'Boolean' || type === 'Picklist' || type === 'State' || type === 'Status' || isMulti) {
    define(out, 'OptionSet', optionSetOf(options, lcid), path);
  }
  if (type === 'Boolean') define(out, 'DefaultFormValue', valueOf(a.DefaultValue, 'boolean', lcid), path);
  if (isMulti) define(out, 'DefaultFormValue', valueOf(a.DefaultFormValue, 'number', lcid), path);
  if (type === 'State') {
    const hasDefaults = !!options && options.every((o) => typeof o.DefaultStatus === 'number');
    const statusOptions = optionsOf(all.find((x) => x.AttributeType === 'Status') ?? {});
    const hasStates = !!statusOptions && statusOptions.every((o) => typeof o.State === 'number');
    defineMethod(out, 'getDefaultStatus', hasDefaults ? (state: unknown) => options!.find((o) => o.Value === state)?.DefaultStatus ?? null : undefined, path);
    defineMethod(
      out,
      'getStatusValuesForState',
      hasStates ? (state: unknown) => statusOptions!.filter((o) => o.State === state).map((o) => o.Value!) : undefined,
      path,
    );
  }
  if (type === 'Status') {
    const hasStates = !!options && options.every((o) => typeof o.State === 'number');
    defineMethod(out, 'getState', hasStates ? (status: unknown) => options!.find((o) => o.Value === status)?.State ?? null : undefined, path);
  }
  return out;
}

function defineMethod(target: Raw, name: string, fn: ((arg: unknown) => unknown) | undefined, path: string): void {
  if (fn) Object.defineProperty(target, name, { value: fn, enumerable: false });
  else Object.defineProperty(target, name, { value: stub(`${path}.${name}`, Stage.later), enumerable: false });
}

/** Learn's Privilege objects; undefined when a privilege type has no Learn number. */
function privilegesOf(raw: unknown): unknown[] | undefined {
  if (!Array.isArray(raw)) return undefined;
  const out = (raw as Raw[]).map((p) => {
    const t = typeof p.PrivilegeType === 'number' ? p.PrivilegeType : PRIVILEGE_TYPES[String(p.PrivilegeType)];
    return t === undefined ? undefined : { ...p, PrivilegeType: t };
  });
  return out.every(Boolean) ? out : undefined;
}

/** The table definitions `raw` (EntityDefinitions with its Attributes) in Learn's shape, with the columns `wanted` only. */
export function entityMetadataOf(raw: Raw, wanted: Set<string>, lcid: number): Raw {
  const out: Raw = {};
  for (const [name, kind] of Object.entries(ENTITY_PROPERTIES)) define(out, name, valueOf(raw[name], kind, lcid), PATH);
  define(out, 'Privileges', privilegesOf(raw.Privileges), PATH);
  const all = Array.isArray(raw.Attributes) ? (raw.Attributes as Raw[]) : [];
  const entity = String(raw.LogicalName ?? '');
  const attributes = all.filter((a) => wanted.has(String(a.LogicalName).toLowerCase())).map((a) => attributeOf(a, entity, all, lcid));
  // Learn: a collection of column definitions, found by logical name.
  out.Attributes = collection<Raw & { getName?: () => string }>(() => attributes, (a) => a.LogicalName as string);
  return out;
}
