/** Entity and attribute metadata as the Web API's `EntityDefinitions` returns it, reduced to what the generic UI uses. */

export type AttrKind =
  | 'string'
  | 'memo'
  | 'int'
  | 'bigint'
  | 'decimal'
  | 'double'
  | 'money'
  | 'bool'
  | 'datetime'
  | 'choice'
  | 'choices'
  | 'lookup'
  | 'file'
  | 'image'
  /** A party list of an activity (`to`, `from`, ...): not a property of the record; its rows are the party navigation. */
  | 'partylist'
  | 'unsupported';

export interface OptionMeta {
  value: number;
  label: string;
  /** The `statecode` a status reason (`statuscode` option) belongs to (`State` of a `StatusOptionMetadata`). */
  state?: number;
  /** The status reason a state starts with (`DefaultStatus` of a `StateOptionMetadata`, a `statecode` option). */
  defaultStatus?: number;
  /** The option's colour, `#RRGGBB`. */
  color?: string;
}

export interface AttributeMeta {
  logicalName: string;
  displayName: string;
  kind: AttrKind;
  /** The server's type name (`Image`, `Uniqueidentifier`, ...); named to the user when the kind is `unsupported`. */
  typeName: string;
  requiredLevel: string;
  /** `ApplicationRequired`: the client marker; the server remains the authority. */
  required: boolean;
  validForCreate: boolean;
  validForUpdate: boolean;
  validForRead: boolean;
  isPrimaryId: boolean;
  isPrimaryName: boolean;
  isCustom: boolean;
  /** Set on an attribute another one stores (a lookup's `...name` companion): not a Web API property of its own. */
  attributeOf?: string;
  targets: string[];
  options: OptionMeta[];
  /** The name of the global choice, when the attribute's option set is one (its options may need `GlobalOptionSetDefinitions`). */
  globalOptionSet?: string;
  maxLength?: number;
  /** `Format` as the server returns it (`Email`, `Url`, `DateOnly`, `Duration`...), for the Client API's `getFormat`. */
  format?: string;
  /** `MinValue`, `MaxValue` and `Precision` of a number column, when the server returned them. */
  minValue?: number;
  maxValue?: number;
  precision?: number;
  dateOnly?: boolean;
  /**
   * `DateTimeBehavior` of a date and time column (#131): UserLocal (the default) is converted to the user's time zone in the app,
   * DateOnly and TimeZoneIndependent are shown as stored.
   */
  behavior?: DateTimeBehavior;
  /** The largest file or image the column takes, in KB (file and image columns). */
  maxSizeInKB?: number;
  /** An image column that keeps the full-size image besides its thumbnail (`$value?size=full`). */
  canStoreFullImage?: boolean;
  /** `IsAuditEnabled`: absent when the server did not return it. */
  audit?: AuditSetting;
  /** `IsSecured`: field security protects the column (a record leaves it out when the caller cannot read it). */
  secured: boolean;
  /** `CanBeSecuredForRead`: the column can be secured; absent when the server did not return it. */
  canBeSecured?: boolean;
  /** Never read, shown or listed by the generic UI (a note's `documentbody`: the attached file, base64, up to the upload limit). */
  hidden?: boolean;
}

/** A managed boolean: `locked` when the server has fixed the value (`CanBeChanged` false). */
export interface AuditSetting {
  enabled: boolean;
  locked: boolean;
}

export interface EntityMeta {
  logicalName: string;
  /** `SchemaName` (`prvCreate{SchemaName}`): absent when the server did not return it. */
  schemaName?: string;
  entitySetName?: string;
  displayName: string;
  primaryIdAttribute: string;
  primaryNameAttribute?: string;
  ownership?: string;
  isCustom: boolean;
  attributes: AttributeMeta[];
  audit?: AuditSetting;
  /** `AutoCreateAccessTeams`: absent when the server did not return it. */
  autoCreateAccessTeams?: boolean;
  /** Change tracking (plan 2.13): absent when the server did not return `ChangeTrackingEnabled`. */
  changeTracking?: { enabled: boolean; canEnable: boolean };
  /** `IsActivity` (plan 2.11): absent when the server did not return it. */
  isActivity?: boolean;
  /** `HasNotes` (one-way): absent when the server did not return it. */
  hasNotes?: boolean;
  /** `HasActivities` (one-way): absent when the server did not return it. */
  hasActivities?: boolean;
  /** `IsValidForQueue` (plan 4.1a, one-way): absent when the server did not return it. */
  isValidForQueue?: boolean;
  /** `IsDuplicateDetectionEnabled` (plan 2.15): absent when the server did not return it. */
  duplicateDetection?: AuditSetting;
}

/** Columns the server maintains (audit, versioning, import): never edited or counted as "used" in the generic UI. */
const SYSTEM_COLUMNS = new Set([
  'createdon',
  'createdby',
  'createdonbehalfby',
  'modifiedon',
  'modifiedby',
  'modifiedonbehalfby',
  'owninguser',
  'owningteam',
  'owningbusinessunit',
  'organizationid',
  'versionnumber',
  'importsequencenumber',
  'overriddencreatedon',
  'timezoneruleversionnumber',
  'utcconversiontimezonecode',
]);

/**
 * File and image columns: written by their own upload requests (`PATCH {set}({id})/{column}`), never in the record body,
 * and only on a record that exists.
 */
export const isFileKind = (kind: AttrKind): boolean => kind === 'file' || kind === 'image';

/** Numeric kinds: edited as text and sent as a JSON number. */
export const isNumeric = (kind: AttrKind): boolean =>
  kind === 'int' || kind === 'bigint' || kind === 'decimal' || kind === 'double' || kind === 'money';

/** Numeric kinds that take whole numbers only. */
export const isWholeNumber = (kind: AttrKind): boolean => kind === 'int' || kind === 'bigint';

export const isSystemColumn = (logicalName: string): boolean => SYSTEM_COLUMNS.has(logicalName);

/** The property a record carries for the attribute: a lookup's id is `_name_value`. */
export const recordKey = (a: Pick<AttributeMeta, 'logicalName' | 'kind'>): string =>
  a.kind === 'lookup' ? `_${a.logicalName}_value` : a.logicalName;

// ---- parsing the raw Web API JSON ----------------------------------------------------------------------------------

type RawLabel = { UserLocalizedLabel?: { Label?: string } | null; LocalizedLabels?: { Label?: string }[] | null } | null | undefined;
type RawOption = { Value?: number; Label?: RawLabel; State?: number | null; DefaultStatus?: number | null; Color?: string | null };
export type RawOptionSet = { Options?: RawOption[] | null; IsGlobal?: boolean; Name?: string; TrueOption?: RawOption; FalseOption?: RawOption } | null | undefined;

export interface RawAttribute {
  '@odata.type'?: string;
  LogicalName: string;
  AttributeType?: string;
  DisplayName?: RawLabel;
  RequiredLevel?: { Value?: string } | null;
  IsValidForCreate?: boolean;
  IsValidForUpdate?: boolean;
  IsValidForRead?: boolean;
  IsPrimaryId?: boolean;
  IsPrimaryName?: boolean;
  IsCustomAttribute?: boolean;
  AttributeOf?: string | null;
  Targets?: string[] | null;
  OptionSet?: RawOptionSet;
  MaxLength?: number;
  Format?: string;
  /** `{ Value: 'UserLocal' }` as the server writes it (a plain string is read too). */
  DateTimeBehavior?: { Value?: string | null } | string | null;
  MinValue?: number | null;
  MaxValue?: number | null;
  Precision?: number | null;
  MaxSizeInKB?: number | null;
  CanStoreFullImage?: boolean | null;
  IsAuditEnabled?: { Value?: boolean; CanBeChanged?: boolean } | null;
  IsSecured?: boolean | null;
  CanBeSecuredForRead?: boolean | null;
}

export interface RawEntity {
  LogicalName: string;
  SchemaName?: string | null;
  EntitySetName?: string | null;
  DisplayName?: RawLabel;
  PrimaryIdAttribute?: string;
  PrimaryNameAttribute?: string | null;
  OwnershipType?: string;
  IsCustomEntity?: boolean;
  Attributes?: RawAttribute[] | null;
  IsAuditEnabled?: { Value?: boolean; CanBeChanged?: boolean } | null;
  AutoCreateAccessTeams?: boolean | null;
  ChangeTrackingEnabled?: boolean | null;
  CanChangeTrackingBeEnabled?: { Value?: boolean } | null;
  IsActivity?: boolean | null;
  HasNotes?: boolean | null;
  HasActivities?: boolean | null;
  IsValidForQueue?: { Value?: boolean } | null;
  IsDuplicateDetectionEnabled?: { Value?: boolean; CanBeChanged?: boolean } | null;
}

export function labelText(label: RawLabel, fallback: string): string {
  return label?.UserLocalizedLabel?.Label || label?.LocalizedLabels?.[0]?.Label || fallback;
}

export function parseOptions(optionSet: RawOptionSet): OptionMeta[] {
  if (!optionSet) return [];
  const raw = optionSet.Options ?? [optionSet.FalseOption, optionSet.TrueOption].filter((o): o is RawOption => !!o);
  return raw
    .filter((o): o is RawOption & { Value: number } => typeof o.Value === 'number')
    .map((o) => ({
      value: o.Value,
      label: labelText(o.Label, String(o.Value)),
      ...(typeof o.State === 'number' ? { state: o.State } : {}),
      ...(typeof o.DefaultStatus === 'number' ? { defaultStatus: o.DefaultStatus } : {}),
      ...(typeof o.Color === 'string' && o.Color ? { color: o.Color } : {}),
    }));
}

const KINDS: Record<string, AttrKind> = {
  String: 'string',
  Memo: 'memo',
  Integer: 'int',
  BigInt: 'bigint',
  Decimal: 'decimal',
  Double: 'double',
  Money: 'money',
  Boolean: 'bool',
  DateTime: 'datetime',
  Picklist: 'choice',
  State: 'choice',
  Status: 'choice',
  MultiSelectPicklist: 'choices',
  Lookup: 'lookup',
  PartyList: 'partylist',
  // Their companions (the file name, the image id, timestamp and url) carry AttributeOf and are left out of the form and list.
  File: 'file',
  Image: 'image',
};

/** The metadata class of an attribute without `#Microsoft.Dynamics.CRM.` and `AttributeMetadata`, e.g. `Picklist`. */
function typeNameOf(raw: RawAttribute): string {
  // A party list is a LookupAttributeMetadata told apart by its AttributeType only.
  if (raw.AttributeType === 'PartyList') return 'PartyList';
  const fromOData = /([A-Za-z]+?)AttributeMetadata$/.exec(raw['@odata.type'] ?? '')?.[1];
  return fromOData ?? raw.AttributeType ?? 'Unknown';
}

export function parseAudit(raw: { Value?: boolean; CanBeChanged?: boolean } | null | undefined): AuditSetting | undefined {
  return raw ? { enabled: raw.Value ?? false, locked: raw.CanBeChanged === false } : undefined;
}

/** The behaviours of a date and time column (Learn, `DateTimeBehavior`). */
export type DateTimeBehavior = 'UserLocal' | 'DateOnly' | 'TimeZoneIndependent';

function behaviorOf(raw: RawAttribute, kind: AttrKind): DateTimeBehavior | undefined {
  if (kind !== 'datetime') return undefined;
  const value = typeof raw.DateTimeBehavior === 'string' ? raw.DateTimeBehavior : raw.DateTimeBehavior?.Value;
  return value === 'DateOnly' || value === 'TimeZoneIndependent' ? value : 'UserLocal';
}

/** A date and time column whose values the app converts to and from the user's time zone (#131, ruling Z6). */
export const isUserLocal = (a: AttributeMeta): boolean => a.kind === 'datetime' && (a.behavior ?? 'UserLocal') === 'UserLocal';

export function parseAttribute(raw: RawAttribute): AttributeMeta {
  const typeName = typeNameOf(raw);
  const options = parseOptions(raw.OptionSet);
  let kind: AttrKind = KINDS[typeName] ?? 'unsupported';
  // A choice the server reports without any option cannot be rendered without guessing: it stays visible and read-only.
  const globalOptionSet = raw.OptionSet?.IsGlobal ? raw.OptionSet.Name : undefined;
  if ((kind === 'choice' || kind === 'choices') && options.length === 0 && !globalOptionSet) kind = 'unsupported';
  const requiredLevel = raw.RequiredLevel?.Value ?? 'None';
  return {
    logicalName: raw.LogicalName,
    displayName: labelText(raw.DisplayName, raw.LogicalName),
    kind,
    typeName,
    requiredLevel,
    required: requiredLevel === 'ApplicationRequired',
    validForCreate: raw.IsValidForCreate ?? true,
    validForUpdate: raw.IsValidForUpdate ?? true,
    validForRead: raw.IsValidForRead ?? true,
    isPrimaryId: raw.IsPrimaryId ?? false,
    isPrimaryName: raw.IsPrimaryName ?? false,
    isCustom: raw.IsCustomAttribute ?? false,
    attributeOf: raw.AttributeOf || undefined,
    targets: raw.Targets ?? [],
    options,
    globalOptionSet,
    maxLength: raw.MaxLength,
    format: raw.Format || undefined,
    minValue: raw.MinValue ?? undefined,
    maxValue: raw.MaxValue ?? undefined,
    precision: raw.Precision ?? undefined,
    behavior: behaviorOf(raw, kind),
    dateOnly: raw.Format === 'DateOnly' || behaviorOf(raw, kind) === 'DateOnly',
    maxSizeInKB: raw.MaxSizeInKB ?? undefined,
    canStoreFullImage: raw.CanStoreFullImage ?? undefined,
    audit: parseAudit(raw.IsAuditEnabled),
    secured: raw.IsSecured ?? false,
    canBeSecured: raw.CanBeSecuredForRead ?? undefined,
  };
}

/** The note body (plan 2.11): `annotation.documentbody`, the attached file as base64. */
const isNoteBody = (table: string, attribute: string): boolean => table === 'annotation' && attribute === 'documentbody';

export function parseEntity(raw: RawEntity): EntityMeta {
  return {
    logicalName: raw.LogicalName,
    schemaName: raw.SchemaName || undefined,
    entitySetName: raw.EntitySetName || undefined,
    displayName: labelText(raw.DisplayName, raw.LogicalName),
    primaryIdAttribute: raw.PrimaryIdAttribute ?? `${raw.LogicalName}id`,
    primaryNameAttribute: raw.PrimaryNameAttribute || undefined,
    ownership: raw.OwnershipType,
    isCustom: raw.IsCustomEntity ?? false,
    attributes: (raw.Attributes ?? []).map((a) => {
      const parsed = parseAttribute(a);
      return isNoteBody(raw.LogicalName, a.LogicalName) ? { ...parsed, hidden: true } : parsed;
    }),
    audit: parseAudit(raw.IsAuditEnabled),
    autoCreateAccessTeams: raw.AutoCreateAccessTeams ?? undefined,
    duplicateDetection: parseAudit(raw.IsDuplicateDetectionEnabled),
    changeTracking:
      raw.ChangeTrackingEnabled == null
        ? undefined
        : { enabled: raw.ChangeTrackingEnabled, canEnable: raw.CanChangeTrackingBeEnabled?.Value ?? true },
    isActivity: raw.IsActivity ?? undefined,
    hasNotes: raw.HasNotes ?? undefined,
    hasActivities: raw.HasActivities ?? undefined,
    isValidForQueue: raw.IsValidForQueue == null ? undefined : raw.IsValidForQueue.Value === true,
  };
}
