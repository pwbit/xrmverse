import { en } from '../../shell/i18n/en';
import { t } from '../../shell/i18n/t';

/** The name of a `solutioncomponent.componenttype`, or a generic label for a type the page has no name for. */
export function componentTypeLabel(type: number): string {
  const key = `solutions.componentType.${type}`;
  return key in en ? t(key as keyof typeof en) : t('solutions.cascade.type', { type });
}
