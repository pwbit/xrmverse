import { renderHook, waitFor } from '@testing-library/react';
import type { ReactNode } from 'react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { loadColumnKinds, type ColumnKind } from '../../apps/views/columnKinds';
import type { ViewColumn } from '../../model/view';
import { parsePresentation } from '../../parsers/presentationJson';
import { CHOICE_TEXT_DARK, CHOICE_TEXT_LIGHT } from '../../shell/theme';
import { readableText } from './choiceColors';
import { PresentationProvider } from './PresentationContext';
import { useChoicePalette } from './useChoicePalette';

const API = vi.hoisted(() => ({ get: () => Promise.reject(new Error('not used')) }));
vi.mock('../../shell/auth/AuthProvider', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../../shell/auth/AuthProvider')>()),
  useOptionalAuth: () => ({ api: API }),
}));
vi.mock('../../apps/views/columnKinds', () => ({ loadColumnKinds: vi.fn() }));

const GREEN = '#107C10';
const RED = '#D13438';
const KINDS: Record<string, ColumnKind> = {
  priority: {
    kind: 'choice',
    type: 'Picklist',
    options: [
      { value: 1, label: 'Low' },
      { value: 2, label: 'High', color: GREEN },
    ],
  },
  tags: {
    kind: 'choice',
    type: 'MultiSelectPicklist',
    globalName: 'xv_Tags',
    options: [
      { value: 1, label: 'Red', color: RED },
      { value: 3, label: 'Green', color: GREEN },
      { value: 4, label: 'Plain' },
    ],
  },
  name: { kind: 'other' },
  done: {
    kind: 'choice',
    type: 'Boolean',
    options: [
      { value: false, label: 'No' },
      { value: true, label: 'Yes', color: GREEN },
    ],
  },
};

const col = (attribute: string, alias?: string): ViewColumn => ({ name: alias ? `${alias}.${attribute}` : attribute, attribute, alias, width: 100, isHidden: false });
const COLUMNS = [col('name'), col('priority'), col('tags'), col('statuscode', 'pc')];

beforeEach(() => {
  vi.mocked(loadColumnKinds).mockReset();
  vi.mocked(loadColumnKinds).mockResolvedValue(KINDS);
});

async function palette(wrapper?: ({ children }: { children: ReactNode }) => ReactNode, table: string | null = 'xv_case') {
  const hook = renderHook(() => useChoicePalette(table ?? undefined, COLUMNS), { wrapper });
  if (table) await waitFor(() => expect(hook.result.current(COLUMNS[1]!, 2)).toBeDefined());
  return hook.result.current;
}

describe('useChoicePalette', () => {
  it('loads the kinds of the root columns once, skipping related-table columns', async () => {
    await palette();
    expect(loadColumnKinds).toHaveBeenCalledTimes(1);
    expect(loadColumnKinds).toHaveBeenCalledWith(API, 'xv_case', ['name', 'priority', 'tags'], 1033);
  });

  it('a coloured option is one badge with the automatic text colour; an uncoloured one, none', async () => {
    const p = await palette();
    expect(p(COLUMNS[1]!, 2)).toEqual([{ label: 'High', background: GREEN, text: readableText(GREEN) }]);
    expect(p(COLUMNS[1]!, 1)).toBeUndefined();
    expect(p(COLUMNS[1]!, null)).toBeUndefined();
    expect(p(COLUMNS[1]!, 99)).toBeUndefined();
    expect(p(COLUMNS[0]!, 'Contoso')).toBeUndefined();
  });

  it('a multi-select value is one badge per option in order, or none when one lacks a colour', async () => {
    const p = await palette();
    expect(p(COLUMNS[2]!, '3,1')).toEqual([
      { label: 'Green', background: GREEN, text: readableText(GREEN) },
      { label: 'Red', background: RED, text: readableText(RED) },
    ]);
    expect(p(COLUMNS[2]!, '1,4')).toBeUndefined();
  });

  it('a Boolean cell (true/false) matches its option: the coloured one is a badge', async () => {
    const p = await palette();
    expect(p(col('done'), true)).toEqual([{ label: 'Yes', background: GREEN, text: readableText(GREEN) }]);
    expect(p(col('done'), false)).toBeUndefined();
  });

  it('a related-table column has no badge', async () => {
    const p = await palette();
    expect(p(COLUMNS[3]!, 2)).toBeUndefined();
  });

  it("the overlay's choices override the text colour (global choice by its name, local by table.column)", async () => {
    const auto = readableText(GREEN);
    const other = auto === CHOICE_TEXT_DARK ? CHOICE_TEXT_LIGHT : CHOICE_TEXT_DARK;
    const { model } = parsePresentation(
      JSON.stringify({ version: 1, choices: { 'xv_case.priority': { 2: { text: other } }, xv_tags: { 3: { text: other } } } }),
    );
    const wrapper = ({ children }: { children: ReactNode }) => <PresentationProvider model={model}>{children}</PresentationProvider>;
    const p = await palette(wrapper);
    expect(p(COLUMNS[1]!, 2)?.[0]?.text).toBe(other);
    expect(p(COLUMNS[2]!, '3,1')?.map((b) => b.text)).toEqual([other, readableText(RED)]);
  });

  it('without a table nothing is loaded and nothing is a badge', async () => {
    const p = await palette(undefined, null);
    expect(p(COLUMNS[1]!, 2)).toBeUndefined();
    expect(loadColumnKinds).not.toHaveBeenCalled();
  });
});
