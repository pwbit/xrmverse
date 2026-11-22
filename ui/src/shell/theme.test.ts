import { createDarkTheme, createLightTheme } from '@fluentui/react-components';
import { describe, expect, it } from 'vitest';
import { brandFontStack, systemFontStack, xrmverseBrand, xrmverseDarkTheme, xrmverseTheme } from './theme';

describe('xrmverseTheme', () => {
  it('is the Fluent light theme on the brand ramp, with no colour overrides', () => {
    const fluent = createLightTheme(xrmverseBrand);
    const { fontFamilyBase, fontFamilyNumeric, fontFamilyMonospace, fontWeightRegular: _w, ...colours } = xrmverseTheme;
    const { fontFamilyBase: _b, fontFamilyNumeric: _n, fontFamilyMonospace: _m, fontWeightRegular: _fw, ...fluentColours } = fluent;
    expect(colours).toEqual(fluentColours);
    expect(xrmverseTheme.colorBrandBackground).toBe('#895B1F');
    for (const font of [fontFamilyBase, fontFamilyNumeric, fontFamilyMonospace]) expect(font).not.toMatch(/segoe/i);
  });

  it('uses the Sora brand font first and the system font stack as fallback', () => {
    expect(xrmverseTheme.fontFamilyBase).toBe(brandFontStack);
    expect(xrmverseTheme.fontFamilyBase.endsWith(systemFontStack)).toBe(true);
    expect(xrmverseTheme.fontFamilyMonospace.startsWith('ui-monospace')).toBe(true);
  });

  it('uses the light (300) weight for regular text, in light and dark, and keeps the other weights', () => {
    expect(xrmverseTheme.fontWeightRegular).toBe(300);
    expect(xrmverseDarkTheme.fontWeightRegular).toBe(300);
    expect(xrmverseTheme.fontWeightSemibold).toBe(createLightTheme(xrmverseBrand).fontWeightSemibold);
  });

  it('dark theme is Fluent dark with the lighter brand foregrounds', () => {
    const { colorBrandForeground1, colorBrandForeground2, fontFamilyBase, fontFamilyNumeric: _n, fontFamilyMonospace: _m, fontWeightRegular: _w, ...rest } = xrmverseDarkTheme;
    const { colorBrandForeground1: _1, colorBrandForeground2: _2, fontFamilyBase: _b, fontFamilyNumeric: _fn, fontFamilyMonospace: _fm, fontWeightRegular: _fw, ...fluent } = createDarkTheme(xrmverseBrand);
    expect(rest).toEqual(fluent);
    expect(colorBrandForeground1).toBe('#AE875C');
    expect(colorBrandForeground2).toBe('#BA9771');
    expect(fontFamilyBase).toBe(brandFontStack);
  });
});
