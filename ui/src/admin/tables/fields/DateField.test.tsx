import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { useState } from 'react';
import { afterEach, describe, expect, it } from 'vitest';
import { setUiLanguage } from '../../../shell/i18n/t';
import { DisplayZoneContext } from '../../../apps/displayZone';
import type { DateTimeBehavior } from '../metadata';
import { attr } from '../testAttrs';
import { DateField } from './DateField';
import type { FieldContext } from './fieldProps';

const context: FieldContext = { table: 'xv_project', lookups: [], globalChoices: {} };

/** Renders a controlled DateField like the forms do and records every value it reports. */
function renderDate(options: { dateOnly: boolean; value?: string; disabled?: boolean; behavior?: DateTimeBehavior; zone?: string }) {
  const changes: string[] = [];
  const column = attr('xv_due', { kind: 'datetime', typeName: 'DateTime', displayName: 'Due', dateOnly: options.dateOnly, behavior: options.behavior });
  function Harness() {
    const [value, setValue] = useState(options.value ?? '');
    return (
      <DateField
        attr={column}
        value={value}
        disabled={options.disabled ?? false}
        context={context}
        onChange={(v) => {
          changes.push(v as string);
          setValue(v as string);
        }}
      />
    );
  }
  render(
    <FluentProvider theme={webLightTheme}>
      <DisplayZoneContext.Provider value={options.zone}>
        <Harness />
      </DisplayZoneContext.Provider>
    </FluentProvider>,
  );
  return changes;
}

const dateInput = () => screen.getByRole('combobox', { name: 'Due' });
/** The calendar icon of the date input (a `role="button"` span after the input), named apart from the input. */
const calendarButton = (name = 'Open calendar') => screen.getByRole('button', { name });

describe('DateField', () => {
  afterEach(() => setUiLanguage(undefined));

  it('typed ISO date is stored as typed', async () => {
    const changes = renderDate({ dateOnly: true });
    await userEvent.type(dateInput(), '2026-11-04');
    await userEvent.tab();
    expect(changes.at(-1)).toBe('2026-11-04');
    expect(dateInput()).toHaveValue('2026-11-04');
    expect(document.querySelector('label')).toHaveAttribute('for', dateInput().id);
  });

  it('picking a day stores ISO', async () => {
    const changes = renderDate({ dateOnly: true, value: '2026-11-01' });
    await userEvent.click(calendarButton());
    const dialog = await screen.findByRole('dialog');
    await userEvent.click(within(dialog).getByRole('button', { name: '4, November, 2026' }));
    expect(changes.at(-1)).toBe('2026-11-04');
  });

  it('date-time keeps UTC string', async () => {
    const changes = renderDate({ dateOnly: false, value: '2026-11-04T09:30' });
    expect(dateInput()).toHaveValue('2026-11-04');
    const time = screen.getByRole('combobox', { name: 'Due time' });
    expect(time).toHaveValue('09:30');
    expect(screen.getByText('Date and time in UTC')).toBeInTheDocument();
    await userEvent.click(time);
    await userEvent.click(await screen.findByRole('option', { name: '10:00' }));
    expect(changes.at(-1)).toBe('2026-11-04T10:00');
  });

  it('names the user\'s time zone of a user local date and time (#131)', () => {
    renderDate({ dateOnly: false, value: '2026-11-04T09:30', zone: 'Europe/Rome' });
    expect(screen.getByText('Date and time in Central European Time')).toBeInTheDocument();
  });

  it('gives a time zone independent date and time no zone (#131)', () => {
    renderDate({ dateOnly: false, value: '2026-11-04T09:30', behavior: 'TimeZoneIndependent', zone: 'Europe/Rome' });
    expect(screen.queryByText(/Date and time in/)).not.toBeInTheDocument();
  });

  it('typed time off the 30-minute steps is stored', async () => {
    const changes = renderDate({ dateOnly: false, value: '2026-11-04T09:30' });
    const time = screen.getByRole('combobox', { name: 'Due time' });
    await userEvent.clear(time);
    await userEvent.type(time, '17:45');
    await userEvent.tab();
    expect(changes.at(-1)).toBe('2026-11-04T17:45');
    expect(time).toHaveValue('17:45');
  });

  it('a time missing from the local clock on a DST day is stored as typed', async () => {
    // 2026-03-29 02:30 does not exist in Rome (clocks jump 02:00 -> 03:00), but the value is UTC, where it does.
    const tz = process.env.TZ;
    process.env.TZ = 'Europe/Rome';
    try {
      const changes = renderDate({ dateOnly: false, value: '2026-03-29T09:30' });
      const time = screen.getByRole('combobox', { name: 'Due time' });
      await userEvent.clear(time);
      await userEvent.type(time, '02:30');
      await userEvent.tab();
      expect(changes.at(-1)).toBe('2026-03-29T02:30');
      await userEvent.click(time);
      expect(await screen.findByRole('option', { name: '02:30' })).toBeInTheDocument();
      expect(screen.getAllByRole('option')).toHaveLength(48);
    } finally {
      // Assigning undefined would store the string "undefined" and leak into later tests of the worker.
      if (tz === undefined) delete process.env.TZ;
      else process.env.TZ = tz;
    }
  });

  it('date without time stores 00:00 UTC', async () => {
    const changes = renderDate({ dateOnly: false });
    await userEvent.type(dateInput(), '2026-11-04');
    await userEvent.tab();
    expect(changes.at(-1)).toBe('2026-11-04T00:00');
  });

  it('clearing stores empty', async () => {
    const changes = renderDate({ dateOnly: true, value: '2026-11-04' });
    await userEvent.clear(dateInput());
    await userEvent.tab();
    expect(changes.at(-1)).toBe('');
  });

  it('a typed date that is not ISO is reported and not stored', async () => {
    const changes = renderDate({ dateOnly: true, value: '2026-11-04' });
    await userEvent.clear(dateInput());
    await userEvent.type(dateInput(), '04/11/2026');
    await userEvent.tab();
    expect(await screen.findByText('Enter the date as YYYY-MM-DD')).toBeInTheDocument();
    expect(changes.filter((c) => c !== '')).toEqual([]);
    await userEvent.clear(dateInput());
    await userEvent.type(dateInput(), '2026-11-05');
    await userEvent.tab();
    expect(changes.at(-1)).toBe('2026-11-05');
    expect(screen.queryByText('Enter the date as YYYY-MM-DD')).toBeNull();
  });

  it('a typed time that is not a time is reported and reset', async () => {
    setUiLanguage(1040);
    const changes = renderDate({ dateOnly: false, value: '2026-11-04T09:30' });
    const time = screen.getByRole('combobox', { name: 'Ora (Due)' });
    await userEvent.clear(time);
    await userEvent.type(time, '25:00');
    await userEvent.tab();
    expect(await screen.findByText("Inserisci l'ora come HH:mm (24 ore)")).toBeInTheDocument();
    expect(time).toHaveValue('09:30');
    expect(changes).toEqual([]);
  });

  it('clearing the time of a midnight value shows 00:00 again', async () => {
    renderDate({ dateOnly: false, value: '2026-11-04T00:00' });
    const time = screen.getByRole('combobox', { name: 'Due time' });
    await userEvent.clear(time);
    await userEvent.tab();
    expect(time).toHaveValue('00:00');
  });

  it('a new value from outside resets a time typed without a date', async () => {
    const column = attr('xv_due', { kind: 'datetime', typeName: 'DateTime', displayName: 'Due', dateOnly: false });
    const field = (value: string) => (
      <FluentProvider theme={webLightTheme}>
        <DateField attr={column} value={value} disabled={false} context={context} onChange={() => {}} />
      </FluentProvider>
    );
    const { rerender } = render(field(''));
    const time = screen.getByRole('combobox', { name: 'Due time' });
    await userEvent.type(time, '17:45');
    await userEvent.tab();
    expect(time).toHaveValue('17:45');
    rerender(field('2026-11-04T08:00'));
    expect(time).toHaveValue('08:00');
  });

  it('disabled date field has no calendar', async () => {
    renderDate({ dateOnly: true, value: '2026-11-04', disabled: true });
    expect(dateInput()).toBeDisabled();
    const button = calendarButton();
    expect(button).toHaveAttribute('aria-disabled', 'true');
    await userEvent.click(button);
    await userEvent.click(dateInput());
    expect(screen.queryByRole('dialog')).toBeNull();
  });

  it('Italian calendar strings', async () => {
    setUiLanguage(1040);
    renderDate({ dateOnly: true, value: '2026-11-04' });
    await userEvent.click(calendarButton('Apri calendario'));
    const dialog = await screen.findByRole('dialog');
    expect(within(dialog).getAllByText(/novembre/).length).toBeGreaterThan(0);
    // 2026-11-02 is a Monday; the header row shows narrow names (like Fluent's own `M`) titled with the full name.
    const monday = new Date(2026, 10, 2);
    const headers = within(dialog).getAllByRole('columnheader');
    expect(headers[0]).toHaveTextContent(new Intl.DateTimeFormat('it-IT', { weekday: 'narrow' }).format(monday));
    expect(headers[0]).toHaveAttribute('title', new Intl.DateTimeFormat('it-IT', { weekday: 'long' }).format(monday));
  });
});
