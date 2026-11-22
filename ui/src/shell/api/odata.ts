/** Quotes a string as an OData string literal (single quotes doubled). */
export function literal(value: string): string {
  return `'${value.replace(/'/g, "''")}'`;
}

/** A record key segment: the bare GUID in parentheses, e.g. `(0000…)`. */
export function key(id: string): string {
  return `(${id})`;
}
