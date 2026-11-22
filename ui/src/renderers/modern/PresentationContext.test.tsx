import { renderHook } from '@testing-library/react';
import type { ReactNode } from 'react';
import { describe, expect, it } from 'vitest';
import type { PresentationModel } from '../../parsers/presentationJson';
import { PresentationProvider, normaliseId, useFormPresentation, useViewPresentation } from './PresentationContext';

const ID = 'ab12cd34-0000-0000-0000-000000000001';
const model: PresentationModel = {
  theme: { density: 'comfortable' },
  whiteLabel: {},
  views: { [ID]: { default: 'kanban', kanban: { groupBy: 'statuscode' } } },
  forms: { [ID]: { layout: 'wizard', grid: {} } },
  choices: {},
};
const wrap = (m?: PresentationModel) => ({ children }: { children: ReactNode }) => <PresentationProvider model={m}>{children}</PresentationProvider>;

describe('PresentationContext', () => {
  it('normaliseId lowercases and strips braces', () => {
    expect(normaliseId('{AB12CD34-0000-0000-0000-000000000001}')).toBe(ID);
  });

  it('returns the entry for braced upper-case and plain lower-case ids', () => {
    const a = renderHook(() => useViewPresentation('{AB12CD34-0000-0000-0000-000000000001}'), { wrapper: wrap(model) });
    const b = renderHook(() => useViewPresentation(ID), { wrapper: wrap(model) });
    expect(a.result.current?.kanban?.groupBy).toBe('statuscode');
    expect(b.result.current?.default).toBe('kanban');
  });

  it('returns undefined for an unknown or missing id', () => {
    expect(renderHook(() => useViewPresentation('nope'), { wrapper: wrap(model) }).result.current).toBeUndefined();
    expect(renderHook(() => useViewPresentation(undefined), { wrapper: wrap(model) }).result.current).toBeUndefined();
  });

  it('returns undefined with no provider and with an undefined model (classic)', () => {
    expect(renderHook(() => useViewPresentation(ID)).result.current).toBeUndefined();
    expect(renderHook(() => useViewPresentation(ID), { wrapper: wrap(undefined) }).result.current).toBeUndefined();
  });

  it('useFormPresentation returns the entry for braced upper-case and plain ids', () => {
    const a = renderHook(() => useFormPresentation('{AB12CD34-0000-0000-0000-000000000001}'), { wrapper: wrap(model) });
    const b = renderHook(() => useFormPresentation(ID), { wrapper: wrap(model) });
    expect(a.result.current?.layout).toBe('wizard');
    expect(b.result.current?.layout).toBe('wizard');
  });

  it('useFormPresentation returns undefined for unknown, missing, no provider and no model', () => {
    expect(renderHook(() => useFormPresentation('nope'), { wrapper: wrap(model) }).result.current).toBeUndefined();
    expect(renderHook(() => useFormPresentation(undefined), { wrapper: wrap(model) }).result.current).toBeUndefined();
    expect(renderHook(() => useFormPresentation(ID)).result.current).toBeUndefined();
    expect(renderHook(() => useFormPresentation(ID), { wrapper: wrap(undefined) }).result.current).toBeUndefined();
  });
});
