import { t } from '../../shell/i18n/t';
import type { Words } from './types';

/** The text of a model phrase in the UI language: names, values and YAML literals stay as written. */
export function phraseText(words: Words): string {
  if (typeof words !== 'object') return String(words);
  if (!words.args) return t(words.key);
  const args: Record<string, string> = {};
  for (const [name, value] of Object.entries(words.args)) args[name] = phraseText(value);
  return t(words.key, args);
}
