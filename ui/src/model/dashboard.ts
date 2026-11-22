import type { FormModel } from './form';

/** A dashboard: a `systemform` of type 0, laid out by the form parser's tabs and sections. */
export type DashboardModel = FormModel & {
  /** FormPresentation 1 (interaction-centric) or `stream` cells: shown with a notice (ruling F3). */
  interactive: boolean;
};
