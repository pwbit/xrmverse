import type { RecordContext } from '../../../apps/record/loadRecord';
import type { RecordForm } from '../../../apps/record/useRecordForm';
import type { TimelineOptions } from '../../../model/app';
import type { UiOverlay } from '../../../client-api/uiOverlay';
import type { ViewPresentation } from '../../../parsers/presentationJson';
import type { SenderPolicy } from './PartyListControl';

/** The overlay's timeline restyle (ruling C4). */
export interface TimelineAppearance {
  appearance: 'list' | 'cards';
  density: 'comfortable' | 'compact';
}

/** The background colours of the modern form's overlay, as CSS values; undefined where none is set. */
export interface FormBackgrounds {
  section(name: string): string | undefined;
  /** A field cell's, by its column. */
  field(attribute: string): string | undefined;
}

/** The overlay's subgrid entries of one form. */
export interface SubgridModes {
  formId: string;
  entries: Record<string, ViewPresentation>;
}

/** What every control of a form needs besides its own cell. */
export interface ControlEnv {
  ctx: RecordContext;
  state: RecordForm;
  /** The user's language (`uilanguageid`). */
  lcid: number;
  /** The currency symbol of the record's currency, when known. */
  currencySymbol?: string;
  /** The form shown (`systemform` id); the timeline remembers its filters per form. */
  formId?: string;
  /** The model-driven app the form opens in; the URL of a record a subgrid row opens carries it. */
  appId?: string;
  /** The app's timeline options (UI-1g); the timeline control reads them. */
  timeline?: TimelineOptions;
  /** How the timeline draws its items (the modern form's overlay, UI-2c); absent, the classic look. */
  timelineAppearance?: TimelineAppearance;
  /** The section and field backgrounds (the modern form's overlay); absent, none. */
  backgrounds?: FormBackgrounds;
  /**
   * The modes of the form's subgrids (the modern form's overlay), by lowercase control id; a subgrid uses them only on the form
   * they belong to (`formId`), not on a quick view form or a dashboard rendered with another id. Absent, every subgrid is a grid.
   */
  subgridModes?: SubgridModes;
  /** Bumped to reload what a form reads on its own (subgrids); Task 7's Refresh bumps it. */
  refreshKey?: number;
  /** Metadata reads shared by the controls of one form (and dropped when `refreshKey` changes). */
  cache?: ReadCache;
  /** What the form's scripts changed; read before the FormXml. */
  overlay?: UiOverlay;
  /** The sender policy an email's `from` offers (plan 4.1b, `xrmverse_EmailSendFrom`); absent, the caller and the queues. */
  senderPolicy?: SenderPolicy;
}

/** A tab, section or cell is shown: as a script set it, else as the FormXml says. */
export const isShown = (env: Pick<ControlEnv, 'overlay'>, element: { visible: boolean }): boolean => env.overlay?.element(element).visible ?? element.visible;

/** Promises of reads by key, so that several controls of a form share one request. */
export type ReadCache = Map<string, Promise<unknown>>;

/** The read under `key`, shared through the form's cache; a failed read is forgotten so that a later one retries. */
export function cachedRead<T>(env: ControlEnv, key: string, load: () => Promise<T>): Promise<T> {
  const hit = env.cache?.get(key);
  if (hit) return hit as Promise<T>;
  const promise = load();
  env.cache?.set(key, promise);
  promise.catch(() => env.cache?.delete(key));
  return promise;
}

export type LabelPosition = 'left' | 'top' | 'bottom';
