import type { PresentationModel } from '../../parsers/presentationJson';
import { CHOICE_TEXT_DARK, CHOICE_TEXT_LIGHT } from '../../shell/theme';

const HEX6 = /^#[0-9a-fA-F]{6}$/;

/** The overlay key of a choice: the global choice's name when it has one, else `table.column`; lowercase. */
export function choiceKey(table: string, column: string, globalName?: string): string {
  return (globalName || `${table}.${column}`).toLowerCase();
}

/** WCAG relative luminance of `#RRGGBB`. */
function luminance(hex: string): number {
  const channel = (i: number) => {
    const c = parseInt(hex.slice(1 + i * 2, 3 + i * 2), 16) / 255;
    return c <= 0.03928 ? c / 12.92 : Math.pow((c + 0.055) / 1.055, 2.4);
  };
  return 0.2126 * channel(0) + 0.7152 * channel(1) + 0.0722 * channel(2);
}

const contrast = (a: number, b: number) => (Math.max(a, b) + 0.05) / (Math.min(a, b) + 0.05);

/** The text colour (dark or light) with the higher WCAG contrast on `background`; a tie gives the dark one. */
export function readableText(background: string): string {
  const l = luminance(background);
  return contrast(l, luminance(CHOICE_TEXT_DARK)) >= contrast(l, luminance(CHOICE_TEXT_LIGHT)) ? CHOICE_TEXT_DARK : CHOICE_TEXT_LIGHT;
}

/** Background and text of an option's badge; undefined when the option has no valid colour. The overlay may override the text. */
export function badgeColors(
  option: { value: number; color?: string },
  key: string,
  choices: PresentationModel['choices'],
): { background: string; text: string } | undefined {
  if (!option.color || !HEX6.test(option.color)) return undefined;
  return { background: option.color, text: choices[key]?.[option.value]?.text ?? readableText(option.color) };
}
