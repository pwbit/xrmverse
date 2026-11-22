import { en } from './en';
import { it } from './it';

/** LCID of Italian; any other `usersettings.uilanguageid` uses the English strings. */
export const ITALIAN_LCID = 1040;

let catalogue: Partial<Record<keyof typeof en, string>> | undefined;

/**
 * Selects the shell language from `usersettings.uilanguageid`: 1040 is it-IT, anything else (or none) en-US.
 * Components read `t` while rendering, so the caller re-renders after changing it.
 */
export function setUiLanguage(lcid: number | null | undefined): void {
  catalogue = lcid === ITALIAN_LCID ? it : undefined;
}

/** The BCP 47 locale of the shell language, for `Intl` formatting (month and day names in the date picker). */
export function uiLocale(): 'it-IT' | 'en-US' {
  return catalogue === it ? 'it-IT' : 'en-US';
}

/** Looks up a UI string and substitutes `{name}` placeholders from `args`; unknown placeholders are left as written. */
export function t(key: keyof typeof en, args?: Record<string, string | number>): string {
  const text: string = catalogue?.[key] ?? en[key];
  if (!args) return text;
  return text.replace(/\{(\w+)\}/g, (match, name: string) =>
    Object.prototype.hasOwnProperty.call(args, name) ? String(args[name]) : match,
  );
}
