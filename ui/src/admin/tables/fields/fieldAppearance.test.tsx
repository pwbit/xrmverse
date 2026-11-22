import { renderHook } from '@testing-library/react';
import type { ReactNode } from 'react';
import { describe, expect, it } from 'vitest';
import { FieldAppearanceProvider, useFieldAppearance } from './fieldAppearance';

const filled = ({ children }: { children: ReactNode }) => <FieldAppearanceProvider value="filled-darker">{children}</FieldAppearanceProvider>;

describe('useFieldAppearance', () => {
  it('is the outline without a provider (the web admin)', () => {
    expect(renderHook(() => useFieldAppearance(false)).result.current).toBe('outline');
  });

  it('is the provided fill for an editable field (the app forms)', () => {
    expect(renderHook(() => useFieldAppearance(false), { wrapper: filled }).result.current).toBe('filled-darker');
  });

  it('keeps the outline for a disabled field, so read-only fields stand apart', () => {
    expect(renderHook(() => useFieldAppearance(true), { wrapper: filled }).result.current).toBe('outline');
  });
});
