import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { useState } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { XrmApi } from '../../../../shell/api/client';
import { MentionInput } from './MentionInput';

const ME = 'aaaaaaaa-0000-0000-0000-000000000001';
const MARIO = 'BBBBBBBB-0000-0000-0000-000000000002';
const MARCO = 'cccccccc-0000-0000-0000-000000000003';
const USERS = [
  { systemuserid: MARIO, fullname: 'Mario Rossi', internalemailaddress: 'mario@x.test' },
  { systemuserid: MARCO, fullname: 'Marco Bianchi', internalemailaddress: 'marco@x.test' },
];

let get: ReturnType<typeof vi.fn>;
const api = () => ({ get }) as unknown as XrmApi;

function Harness({ initial = '', maxLength = 2000, onSubmit }: { initial?: string; maxLength?: number; onSubmit?: () => void }) {
  const [value, setValue] = useState(initial);
  return (
    <FluentProvider theme={webLightTheme}>
      <MentionInput api={api()} userId={ME} value={value} onChange={setValue} maxLength={maxLength} label="Post" onSubmit={onSubmit} />
    </FluentProvider>
  );
}

beforeEach(() => {
  localStorage.clear();
  get = vi.fn(async () => ({ value: USERS }));
});
afterEach(cleanup);

describe('MentionInput', () => {
  it('picker_inserts_mention', async () => {
    const user = userEvent.setup();
    render(<Harness />);
    const box = screen.getByRole('textbox', { name: 'Post' });
    await user.type(box, 'Hi @ma');
    await screen.findByRole('listbox');
    expect(await screen.findAllByRole('option')).toHaveLength(2);
    expect(get.mock.calls.at(-1)?.[0]).toContain("startswith(fullname,'ma')");
    await user.keyboard('{Enter}');
    expect(box).toHaveValue(`Hi @[8,{${MARIO.toLowerCase()}},"Mario Rossi"] `);
    expect(screen.queryByRole('listbox')).toBeNull();
    expect(JSON.parse(localStorage.getItem(`xrmverse.mentions.recent.${ME}`) ?? '[]')).toEqual([{ id: MARIO.toLowerCase(), display: 'Mario Rossi' }]);
  });

  it('arrow_down_then_enter_picks_the_second_user', async () => {
    const user = userEvent.setup();
    render(<Harness />);
    const box = screen.getByRole('textbox', { name: 'Post' });
    await user.type(box, '@ma');
    await screen.findAllByRole('option');
    await user.keyboard('{ArrowDown}{Enter}');
    expect(box).toHaveValue(`@[8,{${MARCO}},"Marco Bianchi"] `);
  });

  it('recent_picks_come_first_and_keep_five', async () => {
    localStorage.setItem(`xrmverse.mentions.recent.${ME}`, JSON.stringify([{ id: MARCO, display: 'Marco Bianchi' }]));
    const user = userEvent.setup();
    render(<Harness />);
    await user.type(screen.getByRole('textbox', { name: 'Post' }), '@ma');
    const options = await screen.findAllByRole('option');
    expect(options.map((o) => o.textContent)).toEqual([expect.stringContaining('Marco Bianchi'), expect.stringContaining('Mario Rossi')]);
  });

  it('search_doubles_single_quotes', async () => {
    const user = userEvent.setup();
    render(<Harness />);
    await user.type(screen.getByRole('textbox', { name: 'Post' }), "@O'Brien");
    await screen.findAllByRole('option');
    const url = String(get.mock.calls.at(-1)?.[0]);
    expect(url).toContain("startswith(fullname,'O''Brien')");
    expect(url).not.toContain("'O'Brien'");
  });

  it('search_uses_columns_systemuser_has', async () => {
    // xrmverse's systemuser has fullname and internalemailaddress, no firstname or lastname (a filter on them is a 400):
    // a first name is the start of fullname, a last name the start of a later word of it.
    const user = userEvent.setup();
    render(<Harness />);
    await user.type(screen.getByRole('textbox', { name: 'Post' }), '@ro');
    await screen.findAllByRole('option');
    const url = String(get.mock.calls.at(-1)?.[0]);
    expect(url).toContain("$filter=(startswith(fullname,'ro') or contains(fullname,'%20ro') or startswith(internalemailaddress,'ro')) and isdisabled eq false");
    expect(url).not.toMatch(/firstname|lastname/);
  });

  it('recent_cap_keeps_five_most_recent_first', async () => {
    const seeded = Array.from({ length: 6 }, (_, i) => ({ id: `dddddddd-0000-0000-0000-00000000010${i}`, display: `Seed ${i}` }));
    localStorage.setItem(`xrmverse.mentions.recent.${ME}`, JSON.stringify(seeded));
    const user = userEvent.setup();
    render(<Harness />);
    await user.type(screen.getByRole('textbox', { name: 'Post' }), '@ma');
    await screen.findAllByRole('option');
    await user.keyboard('{Enter}');
    const stored = JSON.parse(localStorage.getItem(`xrmverse.mentions.recent.${ME}`) ?? '[]') as { display: string }[];
    expect(stored).toHaveLength(5);
    expect(stored[0].display).toBe('Mario Rossi');
    expect(stored.slice(1).map((r) => r.display)).toEqual(['Seed 0', 'Seed 1', 'Seed 2', 'Seed 3']);
  });

  it('counter_counts_stored_text', () => {
    render(<Harness initial={`@[8,{${MARIO}},"Mario Rossi"] x`} maxLength={2000} />);
    const n = `@[8,{${MARIO}},"Mario Rossi"] x`.length;
    expect(screen.getByText(`${n} / 2000`)).toBeInTheDocument();
  });

  it('escape_closes_list', async () => {
    const user = userEvent.setup();
    render(<Harness />);
    await user.type(screen.getByRole('textbox', { name: 'Post' }), '@ma');
    await screen.findByRole('listbox');
    await user.keyboard('{Escape}');
    await waitFor(() => expect(screen.queryByRole('listbox')).toBeNull());
  });

  it('ctrl_enter_is_forwarded_to_onSubmit', async () => {
    const onSubmit = vi.fn();
    const user = userEvent.setup();
    render(<Harness onSubmit={onSubmit} />);
    await user.type(screen.getByRole('textbox', { name: 'Post' }), 'hello{Control>}{Enter}{/Control}');
    expect(onSubmit).toHaveBeenCalledTimes(1);
  });

  it('survives_unavailable_storage', async () => {
    const spy = vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => {
      throw new Error('blocked');
    });
    const user = userEvent.setup();
    render(<Harness />);
    await user.type(screen.getByRole('textbox', { name: 'Post' }), '@ma');
    expect(await screen.findAllByRole('option')).toHaveLength(2);
    spy.mockRestore();
  });
});
