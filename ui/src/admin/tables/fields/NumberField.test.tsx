import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { useState } from 'react';
import { afterEach, describe, expect, it } from 'vitest';
import { setUiLanguage } from '../../../shell/i18n/t';
import type { FieldValue } from '../formValues';
import type { AttrKind } from '../metadata';
import { attr } from '../testAttrs';
import type { FieldContext } from './fieldProps';
import { NumberField } from './NumberField';
import { displayNumber, editNumber, parseNumberText } from './numberText';

const context: FieldContext = { table: 'xv_project', lookups: [], globalChoices: {} };
const money = attr('xv_budget', { kind: 'money', typeName: 'Money', displayName: 'Budget', precision: 2 });

function renderNumber(kind: AttrKind, value: string, precision?: number) {
  const changes: FieldValue[] = [];
  const column = attr('xv_n', { kind, displayName: 'Amount', precision });
  function Harness() {
    const [v, setV] = useState<FieldValue>(value);
    return <NumberField attr={column} value={v} disabled={false} context={context} onChange={(x) => { changes.push(x); setV(x); }} />;
  }
  render(<FluentProvider theme={webLightTheme}><Harness /><button>other</button></FluentProvider>);
  return { input: screen.getByRole('textbox', { name: 'Amount' }), changes };
}

afterEach(() => setUiLanguage(undefined));

describe('numberText', () => {
  it('groups digits and pads to the precision', () => {
    expect(displayNumber('1234567.5', money, 'en-US')).toBe('1,234,567.50');
    expect(displayNumber('1234567.5', money, 'it-IT')).toBe('1.234.567,50');
    expect(displayNumber('', money, 'it-IT')).toBe('');
    expect(displayNumber('12x', money, 'it-IT')).toBe('12x');
  });

  it('edits and reads back in the locale', () => {
    expect(editNumber('1234.5', 'it-IT')).toBe('1234,5');
    expect(parseNumberText('1.234,5', 'it-IT')).toBe('1234.5');
    expect(parseNumberText('1,234.5', 'en-US')).toBe('1234.5');
    expect(parseNumberText('abc', 'en-US')).toBe('abc');
  });
});

describe('NumberField', () => {
  it('shows a money value formatted and edits it in Italian', async () => {
    setUiLanguage(1040);
    const { input, changes } = renderNumber('money', '1234567.5', 2);
    expect(input).toHaveValue('1.234.567,50');
    await userEvent.click(input);
    expect(input).toHaveValue('1234567,5');
    await userEvent.clear(input);
    await userEvent.type(input, '12.500,75');
    expect(changes.at(-1)).toBe('12500.75');
    await userEvent.click(screen.getByRole('button', { name: 'other' }));
    expect(input).toHaveValue('12.500,75');
  });

  it('groups whole numbers without decimals', () => {
    const { input } = renderNumber('int', '1234567');
    expect(input).toHaveValue('1,234,567');
  });
});
