import { tokens } from '@fluentui/react-components';
import { BACKGROUND_TOKENS } from '../../renderers/modern/background';

/** The text and marker colour that goes with each named background, in the order of BACKGROUND_TOKENS. */
const INK: Record<keyof typeof BACKGROUND_TOKENS, string> = {
  yellow: tokens.colorPaletteYellowForeground2,
  orange: tokens.colorPalettePeachForeground2,
  red: tokens.colorPaletteRedForeground2,
  pink: tokens.colorPalettePinkForeground2,
  purple: tokens.colorPaletteLavenderForeground2,
  blue: tokens.colorPaletteBlueForeground2,
  teal: tokens.colorPaletteLightTealForeground2,
  green: tokens.colorPaletteGreenForeground2,
  gray: tokens.colorPalettePlatinumForeground2,
};

const PALETTE = (Object.keys(BACKGROUND_TOKENS) as (keyof typeof BACKGROUND_TOKENS)[]).map((name) => ({
  fill: BACKGROUND_TOKENS[name],
  ink: INK[name],
}));

/** The colours of the bar at `index` (the position of the record in the sorted bars), cycling through the 9 named colours. */
export function barColour(index: number): { fill: string; ink: string } {
  const n = PALETTE.length;
  return PALETTE[((Math.trunc(index) % n) + n) % n]!;
}
