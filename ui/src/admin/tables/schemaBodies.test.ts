import { describe, expect, it } from 'vitest';
import fixture from '../../../../fixtures/ui/schema-bodies.json';
import { attributeBody, customerBody, entityBody, lookupBody, type ColumnInput, type TableInput } from './schemaBodies';

type Case = { name: string; kind: 'entity' | 'attribute' | 'lookup' | 'customer'; input: unknown; expected: unknown };

function build(c: Case): unknown {
  if (c.kind === 'entity') return entityBody(c.input as TableInput);
  if (c.kind === 'attribute') return attributeBody(c.input as ColumnInput);
  if (c.kind === 'customer') return customerBody((c.input as { column: ColumnInput }).column, (c.input as { table: string }).table);
  const input = c.input as { column: ColumnInput; table: string; relationshipName: string; referencedAttribute: string };
  return lookupBody(input.column, input.table, input.relationshipName, input.referencedAttribute);
}

describe('schemaBodies', () => {
  // Every case's expected body was produced by src/Xrmd.Schema/SchemaWebApiBodies.cs (UiFixtureTests.cs asserts it still is).
  it('Create_table_and_add_column_bodies_match_cli_shape', () => {
    const cases = fixture.cases as Case[];
    expect(cases.map((c) => c.kind)).toEqual(expect.arrayContaining(['entity', 'attribute', 'lookup', 'customer']));
    for (const c of cases) expect(build(c), c.name).toEqual(c.expected);
  });

  it('covers every column type of the list', () => {
    const covered = new Set((fixture.cases as Case[]).map((c) => (c.input as { type?: string; column?: { type: string } }).type ?? (c.input as { column?: { type: string } }).column?.type));
    for (const type of ['string', 'memo', 'int', 'decimal', 'money', 'bool', 'datetime', 'choice', 'choices', 'lookup', 'customer', 'file', 'image']) expect(covered.has(type), type).toBe(true);
  });

  it('refuses a body the CLI would refuse instead of sending it', () => {
    expect(() => attributeBody({ name: 'xv_a', type: 'lookup', target: 'contact' })).toThrow(/lookup/i);
    expect(() => attributeBody({ name: 'xv_a', type: 'customer' })).toThrow(/customer/i);
    expect(() => customerBody({ name: 'xv_a', type: 'customer', target: 'account' }, 'xv_t')).toThrow(/target/i);
    expect(() => attributeBody({ name: 'xv_a', type: 'choice' })).toThrow(/choice/i);
    expect(() => attributeBody({ name: 'xv_a', type: 'bogus' as never })).toThrow(/bogus/);
  });
});
