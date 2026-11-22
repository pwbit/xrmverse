import { t } from '../../shell/i18n/t';
import { PlanDialog } from './PlanDialog';

/** Uninstalls a managed solution: shows the dry-run plan first (`POST /admin/solutions/{name}/uninstall`). */
export function UninstallDialog({ uniqueName, onClose }: { uniqueName: string; onClose: (changed: boolean) => void }) {
  return (
    <PlanDialog
      action="uninstall"
      solutionName={uniqueName}
      onClose={onClose}
      labels={{
        title: t('solutions.uninstall.title'),
        intro: t('solutions.uninstall.intro', { name: uniqueName }),
        confirm: t('solutions.uninstall.confirm'),
        done: t('solutions.uninstall.done', { name: uniqueName }),
      }}
    />
  );
}
