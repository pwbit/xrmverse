/**
 * The column types the server's schema supports, in the order of `SchemaColumnTypes.Names`
 * (src/Xrmd.Schema/SchemaColumnTypes.cs). A copy: `columnTypes.test.ts` compares it with the committed
 * fixtures/ui/column-types.json, which a .NET test compares with the C# list, so a drift fails one of the two suites.
 */
export const columnTypes = ['string', 'memo', 'int', 'decimal', 'money', 'bool', 'datetime', 'choice', 'choices', 'lookup', 'customer', 'file', 'image'] as const;

export type ColumnType = (typeof columnTypes)[number];

export const isColumnType = (type: string): type is ColumnType => (columnTypes as readonly string[]).includes(type);

/**
 * `MaxSizeInKB` of a file or image column: Dataverse's default and range (Xrmd.Metadata.FileColumns). A copy, pinned like
 * `columnTypes` by fixtures/ui/column-types.json. A blank size in the Add column dialog is left out of the body, so the server
 * applies the default.
 */
export const maxSizeInKB = {
  file: { default: 32768, min: 1, max: 10485760 },
  image: { default: 10240, min: 1, max: 30720 },
} as const;
