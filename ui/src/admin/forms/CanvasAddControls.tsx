import { Button, makeStyles, tokens, useId } from '@fluentui/react-components';
import { useState } from 'react';
import { t } from '../../shell/i18n/t';
import { NewIcon } from '../../shell/icons';
import type { AttributeMeta } from '../tables/metadata';
import { AddControlDialog, type AddControlKind } from './AddControlDialog';
import { bodySections } from './formCells';
import { FORM_TYPE, type DesignSection, type FormDesign } from './formDesign';

const useStyles = makeStyles({
  hint: { color: tokens.colorNeutralForeground3 },
});

/**
 * The controls the canvas toolbar adds to the selected section (G2–G4). Per form type: a Quick create form adds none, and
 * a Quick view form adds no quick view.
 */
const ADDS: [AddControlKind, Parameters<typeof t>[0]][] = [
  ['subgrid', 'forms.controls.addSubgrid'],
  ['quickview', 'forms.controls.addQuickView'],
  ['webresource', 'forms.controls.addWebResource'],
];

export interface CanvasAddControlsProps {
  design: FormDesign;
  attributes: AttributeMeta[];
  /** The section a control is added to; undefined disables the buttons. */
  target?: DesignSection;
  targetLabel: string;
  /** The design with the control added, and the key of its new cell. */
  onAdded: (next: FormDesign, cellKey: string | undefined) => void;
}

/**
 * The Add subgrid, Add quick view and Add web resource buttons of the canvas toolbar, and their dialog. Without a target
 * section they are disabled and point to a visible hint that says why (A2).
 */
export function CanvasAddControls({ design, attributes, target, targetLabel, onAdded }: CanvasAddControlsProps) {
  const styles = useStyles();
  const hintId = useId('add-control-hint-');
  const [adding, setAdding] = useState<AddControlKind>();
  const kinds = ADDS.filter(([kind]) => design.type !== FORM_TYPE.quickCreate && !(design.type === FORM_TYPE.quickView && kind === 'quickview'));

  /** The added control's cell: the target section's cell that was not there before. */
  const added = (next: FormDesign) => {
    const before = new Set(target?.cells.map((c) => c.key));
    const cell = bodySections(next.tabs).find((s) => s.key === target?.key)?.cells.find((c) => !before.has(c.key));
    setAdding(undefined);
    onAdded(next, cell?.key);
  };

  return (
    <>
      {kinds.map(([kind, key]) => (
        <Button
          key={kind}
          appearance="subtle"
          icon={<NewIcon />}
          disabledFocusable={!target}
          aria-describedby={target ? undefined : hintId}
          onClick={() => setAdding(kind)}
        >
          {t(key)}
        </Button>
      ))}
      {!target && kinds.length > 0 && (
        <span id={hintId} className={styles.hint}>
          {t('forms.controls.selectSection.hint')}
        </span>
      )}
      {adding && target && (
        <AddControlDialog
          kind={adding}
          design={design}
          attributes={attributes}
          sectionKey={target.key}
          sectionLabel={targetLabel}
          onAdd={added}
          onClose={() => setAdding(undefined)}
        />
      )}
    </>
  );
}
