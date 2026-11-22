import type { AppModel } from '../model/app';
import type { RendererId } from './contract';

export interface Resolved {
  renderer: RendererId;
  /** The look id that is neither `classic` nor `modern`; the classic look is used and the shell says so. */
  unknownRenderer?: string;
  /** The web resource of the presentation overlay (app, else environment); the classic look ignores it. */
  overlayName?: string;
}

const RENDERERS: readonly string[] = ['classic', 'modern'];

/** An empty or blank string counts as not set. */
const set = (value: string | undefined): string | undefined => (value?.trim() ? value.trim() : undefined);

/**
 * The look of `app` for the user: the user's choice, else the app's value, else the environment's, else the definition default
 * (the last three arrive merged in `app.presentation`), else classic. An id this version does not know gives classic.
 */
export function resolveRenderer(user: { renderer?: string }, app: AppModel): Resolved {
  const id = set(user.renderer) ?? set(app.presentation?.renderer) ?? 'classic';
  const overlayName = set(app.presentation?.overlay);
  const known = RENDERERS.includes(id);
  return {
    renderer: known ? (id as RendererId) : 'classic',
    ...(known ? {} : { unknownRenderer: id }),
    ...(overlayName ? { overlayName } : {}),
  };
}

/** The user's colour scheme (not System) beats the overlay's (not system), which beats the operating system's. */
export function resolveColorScheme(user: 0 | 1 | 2, overlay: 'system' | 'light' | 'dark' | undefined, osDark: boolean): 'light' | 'dark' {
  if (user === 1) return 'light';
  if (user === 2) return 'dark';
  if (overlay === 'light' || overlay === 'dark') return overlay;
  return osDark ? 'dark' : 'light';
}
