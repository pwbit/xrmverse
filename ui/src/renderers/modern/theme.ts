import type { Theme } from '@fluentui/react-components';
import type { PresentationModel } from '../../parsers/presentationJson';
import { brandFontStack, createXrmverseDarkTheme, createXrmverseLightTheme, xrmverseDarkTheme, xrmverseTheme } from '../../shell/theme';

const COMPACT_SCALE = 0.75;

/** Scales every `spacingVertical*` / `spacingHorizontal*` token given in px, rounded to whole px; other tokens are untouched. */
function compact(theme: Theme): Theme {
  const out: Record<string, unknown> = { ...theme };
  for (const [key, value] of Object.entries(theme)) {
    if (!/^spacing(Vertical|Horizontal)/.test(key) || typeof value !== 'string') continue;
    const m = /^(-?\d+(?:\.\d+)?)px$/.exec(value);
    if (m) out[key] = `${Math.round(Number(m[1]) * COMPACT_SCALE)}px`;
  }
  return out as unknown as Theme;
}

/** Builds the Fluent theme for the modern renderer from the overlay; without an overlay it is the xrmverse theme. */
export function buildTheme(model: PresentationModel | undefined, dark: boolean): Theme {
  if (!model) return dark ? xrmverseDarkTheme : xrmverseTheme;
  const { brand, fontFamily, density } = model.theme;
  let theme = brand ? (dark ? createXrmverseDarkTheme(brand) : createXrmverseLightTheme(brand)) : dark ? xrmverseDarkTheme : xrmverseTheme;
  if (fontFamily) theme = { ...theme, fontFamilyBase: `"${fontFamily}", ${brandFontStack}` };
  return density === 'compact' ? compact(theme) : theme;
}
