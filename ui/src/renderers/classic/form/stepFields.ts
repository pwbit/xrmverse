import type { Tab } from '../../../model/form';
import { isShown, type ControlEnv } from './controlEnv';

/**
 * The attributes of the visible, enabled field cells in the visible sections of `tab` (script visibility and `setDisabled` from
 * `env.overlay`, else the FormXml), each once, in FormXml order: what a wizard step checks before Next (UI-2c, ruling C2).
 */
export function stepAttributes(tab: Tab, env: ControlEnv): string[] {
  const found = new Set<string>();
  for (const column of tab.columns)
    for (const section of column.sections) {
      if (!isShown(env, section)) continue;
      for (const row of section.rows)
        for (const cell of row) {
          const control = cell.control;
          if (control?.kind !== 'field' || !isShown(env, cell)) continue;
          if (env.overlay?.element(cell).disabled ?? control.disabled) continue;
          found.add(control.attribute);
        }
    }
  return [...found];
}
