import { describe, expect, it } from 'vitest';
import { xrmverseDarkTheme, xrmverseTheme, brandFontStack, systemFontStack } from '../../shell/theme';
import type { PresentationModel } from '../../parsers/presentationJson';
import { buildTheme } from './theme';

const ramp = Object.fromEntries(
  [10, 20, 30, 40, 50, 60, 70, 80, 90, 100, 110, 120, 130, 140, 150, 160].map((k, i) => [k, '#' + (i + 16).toString(16).padStart(2, '0').repeat(3)]),
) as unknown as NonNullable<PresentationModel['theme']['brand']>;

const model = (theme: Partial<PresentationModel['theme']> = {}): PresentationModel => ({
  theme: { density: 'comfortable', ...theme },
  whiteLabel: {},
  views: {},
  forms: {},
  choices: {},
});

describe('buildTheme', () => {
  it('returns the xrmverse themes without a model', () => {
    expect(buildTheme(undefined, false)).toBe(xrmverseTheme);
    expect(buildTheme(undefined, true)).toBe(xrmverseDarkTheme);
  });

  it('scales spacing tokens by 0.75 for compact density', () => {
    expect(xrmverseTheme.spacingVerticalM).toBe('12px');
    const t = buildTheme(model({ density: 'compact' }), false);
    expect(t.spacingVerticalM).toBe('9px');
    expect(t.spacingHorizontalM).toBe('9px');
    expect(t.fontSizeBase300).toBe(xrmverseTheme.fontSizeBase300);
  });

  it('keeps comfortable spacing unchanged', () => {
    expect(buildTheme(model(), false).spacingVerticalM).toBe('12px');
  });

  it('uses the custom ramp in light and dark, with the dark foreground tweaks', () => {
    const light = buildTheme(model({ brand: ramp }), false);
    expect(light.colorBrandBackground).not.toBe(xrmverseTheme.colorBrandBackground);
    const dark = buildTheme(model({ brand: ramp }), true);
    expect(dark.colorBrandForeground1).toBe(ramp[110]);
    expect(dark.colorBrandForeground2).toBe(ramp[120]);
  });

  it('puts a custom family first in fontFamilyBase, quoted, ahead of the xrmverse fallback', () => {
    const t = buildTheme(model({ fontFamily: 'Inter' }), false);
    expect(t.fontFamilyBase).toBe(`"Inter", ${brandFontStack}`);
    expect(t.fontFamilyBase.endsWith(systemFontStack)).toBe(true);
  });
});
