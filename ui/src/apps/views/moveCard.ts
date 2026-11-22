import type { ColumnKind } from './columnKinds';

/** The PATCH body of moving `row` to option `target` (B2); `null` target = "(none)". */
export function moveBody(
  groupBy: string,
  column: Extract<ColumnKind, { kind: 'choice' }>,
  row: Record<string, unknown>,
  target: number | boolean | null,
): Record<string, unknown> {
  const body: Record<string, unknown> = { [groupBy]: target };
  if (column.type === 'Status' && target !== null) {
    // A status of another state changes the state with it (Dataverse sets both on the same request).
    const state = column.options.find((o) => o.value === target)?.state;
    if (state !== undefined && state !== row.statecode) body.statecode = state;
  }
  return body;
}
