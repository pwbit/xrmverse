import { describe, expect, it } from 'vitest';
import { en } from '../../shell/i18n/en';
import { defaultPrimarySchemaName } from './CreateTableDialog';
import { deriveSchemaName, isPrefix, logicalNameOf, pascalCase, prefixOf, schemaNameError } from './schemaNames';

describe('schemaNames', () => {
  it('derives <prefix>_<PascalCase> from a display name', () => {
    expect(pascalCase('work task')).toBe('WorkTask');
    expect(pascalCase('  Due date (UTC)!')).toBe('DueDateUTC');
    expect(pascalCase('ToDo list')).toBe('ToDoList');
    expect(pascalCase('2nd phase')).toBe('2ndPhase');
    expect(pascalCase('Città')).toBe('Citt'); // a suggestion only: letters outside ASCII are dropped
    expect(deriveSchemaName('xv', 'Work task')).toBe('xv_WorkTask');
    expect(deriveSchemaName('new', 'Account')).toBe('new_Account');
  });

  it('derives nothing without a valid prefix or a usable display name', () => {
    expect(deriveSchemaName(undefined, 'Task')).toBe('');
    expect(deriveSchemaName('', 'Task')).toBe('');
    expect(deriveSchemaName('X', 'Task')).toBe('');
    expect(deriveSchemaName('xv', ' !? ')).toBe('');
  });

  it('takes the logical name as the schema name in lowercase', () => {
    expect(logicalNameOf('xv_WorkTask')).toBe('xv_worktask');
    expect(logicalNameOf(' xv_Name ')).toBe('xv_name');
  });

  it('accepts a prefixed schema name in any casing', () => {
    expect(schemaNameError('xv_Project')).toBeUndefined();
    expect(schemaNameError('new_account2')).toBeUndefined();
    expect(schemaNameError('abcdefgh_X')).toBeUndefined();
    expect(schemaNameError('xv_' + 'A'.repeat(61))).toBeUndefined(); // 64 characters
  });

  it('refuses what the server or the CLI would refuse', () => {
    // Not a valid logical name once lowercased: ^[a-z][a-z0-9_]{0,63}$ (LogicalNameValidator).
    expect(schemaNameError('xv_Wid-get')).toBe(en['schema.err.format']);
    expect(schemaNameError('xv Project')).toBe(en['schema.err.format']);
    expect(schemaNameError('1xv_Project')).toBe(en['schema.err.format']);
    expect(schemaNameError('xv_Città')).toBe(en['schema.err.format']);
    expect(schemaNameError('xv_' + 'A'.repeat(62))).toBe(en['schema.err.format']); // 65 characters
    // No publisher prefix of 2 to 8 lowercase letters or digits, then "_" and a name (SchemaYaml).
    expect(schemaNameError('Account')).toBe(en['schema.err.prefix']);
    expect(schemaNameError('x_Project')).toBe(en['schema.err.prefix']);
    expect(schemaNameError('abcdefghi_Project')).toBe(en['schema.err.prefix']);
    expect(schemaNameError('XV_Project')).toBe(en['schema.err.prefix']);
    expect(schemaNameError('xv_')).toBe(en['schema.err.prefix']);
  });

  it('reads the prefix of a schema name and the default primary name column', () => {
    expect(prefixOf('xv_Project')).toBe('xv');
    expect(prefixOf('Project')).toBeUndefined();
    expect(prefixOf('x_Project')).toBeUndefined();
    expect(isPrefix('new')).toBe(true);
    expect(isPrefix('9a')).toBe(false);
    expect(defaultPrimarySchemaName('xv_Project')).toBe('xv_Name');
    expect(defaultPrimarySchemaName('Project')).toBeUndefined();
  });
});
