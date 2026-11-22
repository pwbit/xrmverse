import { tokens } from '@fluentui/react-components';
import { describe, expect, it } from 'vitest';
import { barColour } from './barColours';

describe('barColour', () => {
  it('follows the named colours and cycles after 9', () => {
    expect(barColour(0)).toEqual({ fill: tokens.colorPaletteYellowBackground2, ink: tokens.colorPaletteYellowForeground2 });
    expect(barColour(5)).toEqual({ fill: tokens.colorPaletteBlueBackground2, ink: tokens.colorPaletteBlueForeground2 });
    expect(barColour(8)).toEqual({ fill: tokens.colorPalettePlatinumBackground2, ink: tokens.colorPalettePlatinumForeground2 });
    expect(barColour(9)).toEqual(barColour(0));
  });
});
