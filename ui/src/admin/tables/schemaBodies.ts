import { isColumnType, type ColumnType } from './columnTypes';

/**
 * The Web API bodies the CLI sends (src/Xrmd.Schema/SchemaWebApiBodies.cs), built here without a second mapping of
 * their own: `schemaBodies.test.ts` checks every body against fixtures/ui/schema-bodies.json, whose expected values come
 * from the C# code (and are asserted against it by tests/Xrmd.Schema.Tests/UiFixtureTests.cs).
 */

export interface TableInput {
  /** The LogicalName. */
  name: string;
  /** The SchemaName (e.g. `xv_Project`); default: the logical name, as in the CLI. */
  schemaName?: string;
  label: string;
  pluralLabel?: string;
  ownership: 'user' | 'organization';
  /** A custom activity: its primary name column is `subject` (SchemaName `Subject`). */
  isActivity?: boolean;
  /** Notes on the records (one-way). */
  hasNotes?: boolean;
  /** Activities regarding the records (one-way). */
  hasActivities?: boolean;
  /** The records can be put in queues (one-way; on by default for an activity). */
  isValidForQueue?: boolean;
  primaryName: { name: string; schemaName?: string; label?: string; maxLength?: number };
}

/** The primary name column of an activity (SchemaTable.ActivityPrimaryName and ActivityPrimarySchemaName). */
export const ACTIVITY_PRIMARY_NAME = { name: 'subject', schemaName: 'Subject' } as const;

export interface ColumnInput {
  /** The LogicalName. */
  name: string;
  /** The SchemaName (e.g. `xv_DueDate`); default: the logical name, as in the CLI. */
  schemaName?: string;
  type: ColumnType;
  label?: string;
  maxLength?: number;
  precision?: number;
  /** The global choice a choice or choices column uses. */
  choice?: string;
  /** The table a lookup refers to (a customer column takes none: it always refers to account and contact). */
  target?: string;
  /** The largest file or image the column takes, in KB (file, image). */
  maxSizeInKB?: number;
  /** Whether an image column keeps the full-size image besides the thumbnail. */
  canStoreFullImage?: boolean;
  /** True to make an image column the primary image of its table (it is never set to false). */
  primary?: boolean;
}

const LANGUAGE_CODE = 1033;
const NAMESPACE = 'Microsoft.Dynamics.CRM.';

type Json = Record<string, unknown>;

const label = (text: string): Json => ({
  LocalizedLabels: [{ Label: text, LanguageCode: LANGUAGE_CODE }],
  UserLocalizedLabel: { Label: text, LanguageCode: LANGUAGE_CODE },
});

const yesNo = (text: string, value: number): Json => ({ Value: value, Label: label(text), IsHidden: false });

/** What each type adds to the common attribute: its metadata class, `AttributeType` and `AttributeTypeName`. */
const SHAPES: Record<ColumnType, { odata: string; attributeType: string; typeName: string }> = {
  string: { odata: 'StringAttributeMetadata', attributeType: 'String', typeName: 'StringType' },
  memo: { odata: 'MemoAttributeMetadata', attributeType: 'Memo', typeName: 'MemoType' },
  int: { odata: 'IntegerAttributeMetadata', attributeType: 'Integer', typeName: 'IntegerType' },
  decimal: { odata: 'DecimalAttributeMetadata', attributeType: 'Decimal', typeName: 'DecimalType' },
  money: { odata: 'MoneyAttributeMetadata', attributeType: 'Money', typeName: 'MoneyType' },
  bool: { odata: 'BooleanAttributeMetadata', attributeType: 'Boolean', typeName: 'BooleanType' },
  datetime: { odata: 'DateTimeAttributeMetadata', attributeType: 'DateTime', typeName: 'DateTimeType' },
  choice: { odata: 'PicklistAttributeMetadata', attributeType: 'Picklist', typeName: 'PicklistType' },
  choices: { odata: 'MultiSelectPicklistAttributeMetadata', attributeType: 'Virtual', typeName: 'MultiSelectPicklistType' },
  lookup: { odata: 'LookupAttributeMetadata', attributeType: 'Lookup', typeName: 'LookupType' },
  // As Dataverse's CreateCustomerRelationships example states it: the server makes the column a Customer (account and contact).
  customer: { odata: 'LookupAttributeMetadata', attributeType: 'Lookup', typeName: 'LookupType' },
  file: { odata: 'FileAttributeMetadata', attributeType: 'Virtual', typeName: 'FileType' },
  image: { odata: 'ImageAttributeMetadata', attributeType: 'Virtual', typeName: 'ImageType' },
};

function attribute(column: ColumnInput): Json {
  if (!isColumnType(column.type)) throw new Error(`Unknown column type '${String(column.type)}'.`);
  const shape = SHAPES[column.type];
  const body: Json = {
    '@odata.type': NAMESPACE + shape.odata,
    AttributeType: shape.attributeType,
    AttributeTypeName: { Value: shape.typeName },
    DisplayName: label(column.label ?? column.name),
    IsValidODataAttribute: false,
    LogicalName: column.name,
    SchemaName: column.schemaName ?? column.name,
  };
  if ((column.type === 'string' || column.type === 'memo') && column.maxLength !== undefined) body.MaxLength = column.maxLength;
  if (column.type === 'decimal' && column.precision !== undefined) body.Precision = column.precision;
  if (column.type === 'bool') body.OptionSet = { TrueOption: yesNo('Yes', 1), FalseOption: yesNo('No', 0) };
  if (column.type === 'choice' || column.type === 'choices') {
    if (!column.choice) throw new Error(`A ${column.type} column needs a global choice.`);
    body.OptionSet = { Options: [], IsGlobal: true, Name: column.choice, OptionSetType: 'Picklist' };
  }
  if (column.type === 'lookup') {
    if (!column.target) throw new Error('A lookup column needs a target table.');
    body.Targets = [column.target];
  }
  if ((column.type === 'file' || column.type === 'image') && column.maxSizeInKB !== undefined) body.MaxSizeInKB = column.maxSizeInKB;
  if (column.type === 'file') {
    // What the SDK's FileAttributeMetadata constructor sets, as the C# body carries it.
    body.IsValidForCreate = false;
    body.IsValidForUpdate = false;
    body.RequiredLevel = { IsValueModified: true, IsCanBeChangedPropertyModified: true, Value: 'None', CanBeChanged: true };
  }
  if (column.type === 'image') {
    if (column.primary === false) throw new Error('An image column can be made primary, never not primary.');
    if (column.canStoreFullImage !== undefined) body.CanStoreFullImage = column.canStoreFullImage;
    if (column.primary !== undefined) body.IsPrimaryImage = column.primary;
  }
  return body;
}

/** `POST EntityDefinitions`: a table with its primary name column, and `IsActivity`, `HasNotes`, `HasActivities`, `IsValidForQueue` when true. */
export function entityBody(table: TableInput): Json {
  const { name, label: primaryLabel, maxLength } = table.primaryName;
  // SchemaTable.PrimarySchemaName: an activity's `subject` is `Subject` unless stated.
  const schemaName = table.primaryName.schemaName ?? (table.isActivity && name === ACTIVITY_PRIMARY_NAME.name ? ACTIVITY_PRIMARY_NAME.schemaName : undefined);
  const primary = attribute({ name, schemaName, type: 'string', label: primaryLabel, maxLength });
  const body: Json = {
    '@odata.type': NAMESPACE + 'EntityMetadata',
    DisplayName: label(table.label),
    LogicalName: table.name,
    OwnershipType: table.ownership === 'organization' ? 'OrganizationOwned' : 'UserOwned',
    SchemaName: table.schemaName ?? table.name,
    Attributes: [{ ...primary, IsPrimaryName: true }],
  };
  if (table.pluralLabel !== undefined) body.DisplayCollectionName = label(table.pluralLabel);
  // Only true is stated (false is a new table's default).
  if (table.isActivity) body.IsActivity = true;
  if (table.hasNotes) body.HasNotes = true;
  if (table.hasActivities) body.HasActivities = true;
  if (table.isValidForQueue) body.IsValidForQueue = { Value: true, CanBeChanged: true };
  return body;
}

/** `POST EntityDefinitions(...)/Attributes`: a column that is neither a lookup nor a customer (those go through {@link lookupBody} and {@link customerBody}). */
export function attributeBody(column: ColumnInput): Json {
  if (column.type === 'lookup') throw new Error('A lookup is created with its relationship (see lookupBody).');
  if (column.type === 'customer') throw new Error('A customer column is created with its two relationships (see customerBody).');
  return attribute(column);
}

/** The SchemaName of the relationship of a customer column to `target` (SchemaWebApiBodies.CustomerRelationshipName). */
export const customerRelationshipName = (table: string, column: string, target: string): string => `${table}_${column}_${target}`;

/** The tables a customer column refers to (CustomerColumns.Targets), with their primary id column. */
const CUSTOMER_TARGETS = [
  { table: 'account', primaryId: 'accountid' },
  { table: 'contact', primaryId: 'contactid' },
] as const;

/** `POST CreateCustomerRelationships`: a customer column with its two one-to-many relationships, to account and to contact. */
export function customerBody(column: ColumnInput, referencingTable: string): Json {
  if (column.type !== 'customer') throw new Error('The column is not a customer column.');
  if (column.target !== undefined) throw new Error('A customer column has no target.');
  return {
    Lookup: attribute(column),
    OneToManyRelationships: CUSTOMER_TARGETS.map(({ table, primaryId }) => ({
      '@odata.type': NAMESPACE + 'OneToManyRelationshipMetadata',
      ReferencedAttribute: primaryId,
      ReferencedEntity: table,
      ReferencingEntity: referencingTable,
      SchemaName: customerRelationshipName(referencingTable, column.name, table),
      RelationshipType: 'OneToManyRelationship',
    })),
  };
}

/** `POST RelationshipDefinitions`: a lookup column with its one-to-many relationship. */
export function lookupBody(column: ColumnInput, referencingTable: string, relationshipName: string, referencedAttribute: string): Json {
  if (column.type !== 'lookup') throw new Error('The column is not a lookup.');
  return {
    '@odata.type': NAMESPACE + 'OneToManyRelationshipMetadata',
    ReferencedAttribute: referencedAttribute,
    ReferencedEntity: column.target,
    ReferencingEntity: referencingTable,
    SchemaName: relationshipName,
    RelationshipType: 'OneToManyRelationship',
    Lookup: attribute(column),
  };
}
