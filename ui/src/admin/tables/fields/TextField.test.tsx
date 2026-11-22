import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { attr } from '../testAttrs';
import { TextField } from './TextField';
import type { FieldContext } from './fieldProps';

const context: FieldContext = { table: 'xv_project', lookups: [], globalChoices: {} };

describe('TextField', () => {
  it('lets the user drag a multiline text field taller', () => {
    const column = attr('xv_notes', { kind: 'memo', typeName: 'Memo', displayName: 'Notes' });
    render(
      <FluentProvider theme={webLightTheme}>
        <TextField attr={column} value="a" disabled={false} context={context} onChange={() => {}} />
      </FluentProvider>,
    );
    expect(getComputedStyle(screen.getByRole('textbox', { name: 'Notes' })).resize).toBe('vertical');
  });
});
