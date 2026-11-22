import type { AppOnLoadHandler } from '../../model/app';
import { isWebResourceName } from '../scriptHost';

export type { AppOnLoadHandler };

/**
 * Parses the value of the app setting `xrmverse_AppOnLoad`: a JSON array of `{ "library": string, "function": string }`.
 * Empty (or blank) means no handlers. A malformed value gives `{ error }` naming the problem, so the shell can show it once.
 */
export function parseAppOnLoad(value: string | undefined): { handlers: AppOnLoadHandler[] } | { error: string } {
  if (value === undefined || value.trim() === '') return { handlers: [] };
  let parsed: unknown;
  try {
    parsed = JSON.parse(value);
  } catch {
    return { error: 'xrmverse_AppOnLoad is not valid JSON' };
  }
  if (!Array.isArray(parsed)) return { error: 'xrmverse_AppOnLoad must be a JSON array' };
  const handlers: AppOnLoadHandler[] = [];
  for (const [i, item] of parsed.entries()) {
    const entry = item as { library?: unknown; function?: unknown } | null;
    if (!entry || typeof entry !== 'object' || typeof entry.library !== 'string' || typeof entry.function !== 'string') {
      return { error: `xrmverse_AppOnLoad entry ${i + 1} must have a string "library" and a string "function"` };
    }
    if (entry.function.trim() === '') return { error: `xrmverse_AppOnLoad entry ${i + 1} has an empty "function"` };
    if (!isWebResourceName(entry.library)) return { error: `xrmverse_AppOnLoad entry ${i + 1} has an invalid "library" web resource name` };
    handlers.push({ library: entry.library, function: entry.function });
  }
  return { handlers };
}
