import { tokens } from '@fluentui/react-components';
import type { BackgroundColor, BackgroundName, FormBackground } from '../../parsers/presentationJson';
import type { FormBackgrounds } from '../classic/form/controlEnv';

/** The Fluent palette token of each named colour: a soft tint in the light theme, a deep shade in the dark one. */
export const BACKGROUND_TOKENS: Record<BackgroundName, string> = {
  yellow: tokens.colorPaletteYellowBackground2,
  orange: tokens.colorPalettePeachBackground2,
  red: tokens.colorPaletteRedBackground2,
  pink: tokens.colorPalettePinkBackground2,
  purple: tokens.colorPaletteLavenderBackground2,
  blue: tokens.colorPaletteBlueBackground2,
  teal: tokens.colorPaletteLightTealBackground2,
  green: tokens.colorPaletteGreenBackground2,
  gray: tokens.colorPalettePlatinumBackground2,
};

/** The CSS value of a stored colour: the token of a name, a free colour as it is. */
export const backgroundCss = (colour: BackgroundColor): string => (colour.startsWith('#') ? colour : BACKGROUND_TOKENS[colour as BackgroundName]);

/** What the form's sections and field cells read: the CSS background of a section (by name) and of a field cell (by column). */
export function formBackgrounds(background: FormBackground | undefined): FormBackgrounds | undefined {
  if (!background) return undefined;
  const { sections, fields } = background;
  if (Object.keys(sections).length === 0 && Object.keys(fields).length === 0) return undefined;
  return {
    section: (name) => (Object.hasOwn(sections, name) ? backgroundCss(sections[name]) : undefined),
    field: (attribute) => {
      const key = attribute.toLowerCase();
      return Object.hasOwn(fields, key) ? backgroundCss(fields[key]) : undefined;
    },
  };
}
