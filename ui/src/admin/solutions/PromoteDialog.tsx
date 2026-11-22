import { t } from '../../shell/i18n/t';
import { PlanDialog } from './PlanDialog';

/** Applies the staged upgrade (`{name}_Upgrade`) of a managed solution: dry-run plan first (`POST /admin/solutions/{name}/promote`). */
export function PromoteDialog({ uniqueName, onClose }: { uniqueName: string; onClose: (changed: boolean) => void }) {
  return (
    <PlanDialog
      action="promote"
      solutionName={uniqueName}
      onClose={onClose}
      labels={{
        title: t('solutions.promote.title'),
        intro: t('solutions.promote.intro', { name: uniqueName }),
        confirm: t('solutions.promote.confirm'),
        done: t('solutions.promote.done', { name: uniqueName }),
      }}
    />
  );
}
