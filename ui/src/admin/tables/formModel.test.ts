import { describe, expect, it } from 'vitest';
import { buildFormModel } from './formModel';
import { attr, entity } from './testAttrs';

describe('buildFormModel', () => {
  it('buildFormModel_groups_by_type_and_excludes_system_columns', () => {
    const model = buildFormModel(
      entity([
        attr('xv_projectid', { isPrimaryId: true, kind: 'unsupported', typeName: 'Uniqueidentifier', validForUpdate: false }),
        attr('xv_notes', { kind: 'memo', typeName: 'Memo' }),
        attr('xv_name', { isPrimaryName: true, required: true, requiredLevel: 'ApplicationRequired' }),
        attr('xv_count', { kind: 'int', typeName: 'Integer' }),
        attr('xv_price', { kind: 'money', typeName: 'Money' }),
        attr('xv_due', { kind: 'datetime', typeName: 'DateTime' }),
        attr('xv_flag', { kind: 'bool', typeName: 'Boolean' }),
        attr('xv_prio', { kind: 'choice', typeName: 'Picklist' }),
        attr('xv_tags', { kind: 'choices', typeName: 'MultiSelectPicklist' }),
        attr('xv_parent', { kind: 'lookup', typeName: 'Lookup', targets: ['contact'] }),
        attr('xv_parentname', { attributeOf: 'xv_parent' }),
        attr('xv_scan', { kind: 'image', typeName: 'Image' }),
        attr('xv_scanid', { kind: 'unsupported', typeName: 'Uniqueidentifier', attributeOf: 'xv_scan', validForCreate: false, validForUpdate: false }),
        // A file column is valid for neither create nor update (only its upload writes it), yet the form shows it.
        attr('xv_doc', { kind: 'file', typeName: 'File', validForCreate: false, validForUpdate: false }),
        attr('xv_doc_name', { attributeOf: 'xv_doc', validForCreate: false, validForUpdate: false }),
        attr('xv_ref', { kind: 'unsupported', typeName: 'Uniqueidentifier' }),
        attr('createdon', { kind: 'datetime', validForCreate: false, validForUpdate: false }),
        attr('modifiedby', { kind: 'lookup', targets: ['systemuser'] }),
        attr('versionnumber', { kind: 'bigint', validForCreate: false, validForUpdate: false }),
        attr('xv_calc', { kind: 'int', validForCreate: false, validForUpdate: false }),
        attr('xv_createonly', { validForUpdate: false }),
      ]),
    );
    expect(model.map((g) => g.group)).toEqual(['Text', 'Numbers', 'Dates', 'Choices', 'Lookups', 'Files', 'Other']);
    const names = (group: string) => model.find((g) => g.group === group)!.fields.map((f) => f.logicalName);
    // Primary name first inside its group, then the metadata order.
    expect(names('Text')).toEqual(['xv_name', 'xv_notes', 'xv_createonly']);
    expect(names('Numbers')).toEqual(['xv_count', 'xv_price']);
    expect(names('Dates')).toEqual(['xv_due']);
    expect(names('Choices')).toEqual(['xv_flag', 'xv_prio', 'xv_tags']);
    expect(names('Lookups')).toEqual(['xv_parent']);
    expect(names('Files')).toEqual(['xv_scan', 'xv_doc']);
    expect(names('Other')).toEqual(['xv_ref']);
  });

  it('leaves out groups that have no field', () => {
    const model = buildFormModel(entity([attr('xv_name', { isPrimaryName: true })]));
    expect(model.map((g) => g.group)).toEqual(['Text']);
  });
});
