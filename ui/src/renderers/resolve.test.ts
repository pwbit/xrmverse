import { describe, expect, it } from 'vitest';
import type { AppModel } from '../model/app';
import { resolveColorScheme, resolveRenderer } from './resolve';

const app = (presentation?: AppModel['presentation']): AppModel => ({
  id: 'a',
  uniqueName: 'a',
  name: 'A',
  clientType: 4,
  components: { entities: [], views: [], forms: [] },
  presentation,
});

describe('resolveRenderer', () => {
  it('user_beats_app', () => {
    expect(resolveRenderer({ renderer: 'modern' }, app({ renderer: 'classic' })).renderer).toBe('modern');
  });

  it('app_value_applies_without_a_user_value', () => {
    expect(resolveRenderer({}, app({ renderer: 'modern' })).renderer).toBe('modern');
    expect(resolveRenderer({ renderer: undefined }, app({ renderer: 'modern' })).renderer).toBe('modern');
  });

  it('defaults_to_classic', () => {
    expect(resolveRenderer({}, app())).toEqual({ renderer: 'classic' });
    expect(resolveRenderer({}, app({}))).toEqual({ renderer: 'classic' });
  });

  it('an_empty_string_is_not_set_at_any_level', () => {
    expect(resolveRenderer({ renderer: '' }, app({ renderer: 'modern' })).renderer).toBe('modern');
    expect(resolveRenderer({ renderer: '' }, app({ renderer: '' })).renderer).toBe('classic');
    expect(resolveRenderer({}, app({ overlay: '' })).overlayName).toBeUndefined();
  });

  it('an_unknown_id_falls_back_to_classic_and_is_reported', () => {
    expect(resolveRenderer({ renderer: 'fancy' }, app())).toEqual({ renderer: 'classic', unknownRenderer: 'fancy' });
    expect(resolveRenderer({}, app({ renderer: 'fancy' }))).toEqual({ renderer: 'classic', unknownRenderer: 'fancy' });
  });

  it('carries_the_overlay_name', () => {
    expect(resolveRenderer({}, app({ renderer: 'modern', overlay: 'brand.json' }))).toEqual({ renderer: 'modern', overlayName: 'brand.json' });
  });
});

describe('resolveColorScheme', () => {
  it('user_light_or_dark_beats_overlay_and_os', () => {
    expect(resolveColorScheme(1, 'dark', true)).toBe('light');
    expect(resolveColorScheme(2, 'light', false)).toBe('dark');
  });

  it('user_system_defers_to_the_overlay', () => {
    expect(resolveColorScheme(0, 'dark', false)).toBe('dark');
    expect(resolveColorScheme(0, 'light', true)).toBe('light');
  });

  it('system_or_unset_follows_the_os', () => {
    expect(resolveColorScheme(0, undefined, true)).toBe('dark');
    expect(resolveColorScheme(0, 'system', false)).toBe('light');
    expect(resolveColorScheme(0, 'system', true)).toBe('dark');
  });
});
