import { Badge, Button, Card, makeStyles, Menu, MenuItem, MenuList, MenuPopover, MenuTrigger, mergeClasses, Text, tokens } from '@fluentui/react-components';
import { AddRegular, ArrowDownRegular, ArrowUpRegular, ClipboardPasteRegular, CutRegular, DeleteRegular, ErrorCircleFilled, MoreHorizontalRegular, WarningFilled } from '@fluentui/react-icons';
import type { KeyboardEvent, MouseEvent, ReactNode } from 'react';
import { t } from '../../shell/i18n/t';
import { addElse, addElseIf, addOnTimeout, moveStep, removeStep } from '../model/edit';
import { phraseText } from '../model/phrase';
import type { StepNode } from '../model/types';
import { BranchColumns } from './BranchColumns';
import { canMoveTo, cardKey, DRAG_TYPE, samePlace, STEP_ICONS, useCanvas, within, type CardProblems, type Place } from './canvasContext';

const useStyles = makeStyles({
  card: { minWidth: '240px', maxWidth: 'none', cursor: 'pointer' },
  selected: { outline: `${tokens.strokeWidthThick} solid ${tokens.colorBrandStroke1}` },
  cut: { opacity: 0.5, outline: `${tokens.strokeWidthThin} dashed ${tokens.colorNeutralStroke1}` },
  head: { display: 'flex', alignItems: 'center', columnGap: tokens.spacingHorizontalS },
  icon: { fontSize: tokens.fontSizeBase500, color: tokens.colorBrandForeground1, flexShrink: 0 },
  text: { display: 'flex', flexDirection: 'column', flexGrow: 1, minWidth: 0 },
  description: { color: tokens.colorNeutralForeground3 },
  footer: { display: 'flex', columnGap: tokens.spacingHorizontalS, flexWrap: 'wrap' },
});

/** The badge of a card whose lines hold problems. */
export function ProblemBadge({ problems }: { problems?: CardProblems }) {
  if (!problems) return null;
  const label = problems.count === 1 ? t('studio.canvas.problem') : t('studio.canvas.problems', { count: problems.count });
  return (
    <Badge
      role="img"
      aria-label={label}
      title={label}
      appearance="filled"
      color={problems.error ? 'danger' : 'warning'}
      icon={problems.error ? <ErrorCircleFilled /> : <WarningFilled />}
    >
      {problems.count}
    </Badge>
  );
}

/** The parts every card (the trigger's and a step's) shares: focus, selection with a click or Enter, and the badge. */
export function useCardBehaviour(at: StepNode['path'] | 'trigger') {
  const canvas = useCanvas();
  const selected = samePlace(canvas.selected, at);
  // A read-only diagram's cards are pictures: not focusable, not selectable.
  if (canvas.readOnly) return { selected: false, problems: canvas.problems.get(cardKey(at)), props: { 'data-card': '', focusMode: 'off' as const, 'aria-roledescription': t('studio.canvas.card') } };
  return {
    selected,
    problems: canvas.problems.get(cardKey(at)),
    props: {
      'data-card': '',
      tabIndex: 0,
      focusMode: 'off' as const,
      'aria-roledescription': t('studio.canvas.card'),
      'aria-current': selected ? ('true' as const) : undefined,
      // A click inside an inner card, a control or a menu (whose portal bubbles here) is not this card's.
      onClick: (e: MouseEvent<HTMLDivElement>) => {
        const target = e.target as Element;
        if (target.closest('[data-card]') !== e.currentTarget || target.closest('button, [role="menuitem"]')) return;
        canvas.select(at);
      },
      onKeyDown: (e: KeyboardEvent<HTMLDivElement>) => {
        if (e.target !== e.currentTarget || (e.key !== 'Enter' && e.key !== ' ')) return;
        e.preventDefault();
        canvas.select(at);
      },
    },
  };
}

export interface StepCardProps {
  step: StepNode;
  /** The card's position in its branch list. */
  index: number;
  count: number;
  place: Place;
}

/** A step's card: its own summary, the problem badge, the card menu, and the branches of an `if` or a `wait`. */
export function StepCard({ step, index, count, place }: StepCardProps) {
  const styles = useStyles();
  const canvas = useCanvas();
  const card = useCardBehaviour(step.path);
  const Icon = STEP_ICONS[step.type];
  const summary = phraseText(step.summary);
  const branch = step.path.slice(0, -1);
  const isCut = canvas.clip !== undefined && cardKey(canvas.clip.path) === cardKey(step.path);
  // Paste after: not into the cut step itself, and only where the place takes it (`canMoveTo`).
  const canPaste = canvas.clip !== undefined && !within(step.path, canvas.clip.path) && canMoveTo(canvas.model, canvas.clip.path, branch);
  const pasteRefused = canvas.clip !== undefined && !canPaste;
  const has = (label: string) => step.branches.some((b) => b.label === label);

  const move = (to: number) => {
    const path = canvas.run((doc) => moveStep(doc, step.path, branch, to));
    if (path) canvas.select(path);
  };
  const remove = () => {
    if (step.branches.some((b) => b.steps.length > 0)) {
      canvas.askDelete(step);
      return;
    }
    if (
      canvas.run((doc) => {
        removeStep(doc, step.path);
        return true;
      })
    )
      canvas.select('trigger');
  };
  const paste = () => {
    const clip = canvas.clip;
    if (!clip) return;
    const path = canvas.run((doc) => moveStep(doc, clip.path, branch, index + 1), t('studio.canvas.moveNotHere'));
    if (path) canvas.select(path);
  };

  let footer: ReactNode = null;
  if (canvas.readOnly) footer = null;
  else if (step.type === 'if') {
    footer = (
      <div className={styles.footer}>
        <Button size="small" appearance="subtle" icon={<AddRegular />} onClick={() => canvas.run((doc) => addElseIf(doc, step.path))}>
          {t('studio.canvas.addElseIf')}
        </Button>
        {!has('else') && (
          <Button size="small" appearance="subtle" icon={<AddRegular />} onClick={() => canvas.run((doc) => addElse(doc, step.path))}>
            {t('studio.canvas.addElse')}
          </Button>
        )}
      </div>
    );
  } else if (step.type === 'wait' && !has('onTimeout')) {
    footer = (
      <div className={styles.footer}>
        <Button
          size="small"
          appearance="subtle"
          icon={<AddRegular />}
          onClick={() => canvas.run((doc) => addOnTimeout(doc, step.path), t('studio.canvas.onTimeoutNeedsTimeout'))}
        >
          {t('studio.canvas.addOnTimeout')}
        </Button>
      </div>
    );
  }

  return (
    <Card
      {...card.props}
      aria-label={summary}
      data-cut={isCut ? 'true' : undefined}
      className={mergeClasses(styles.card, card.selected && styles.selected, isCut && styles.cut)}
      draggable={!canvas.readOnly}
      onDragStart={(e) => {
        if (canvas.readOnly) return;
        e.stopPropagation();
        canvas.dragging.current = step.path;
        e.dataTransfer?.setData(DRAG_TYPE, JSON.stringify(step.path));
        if (e.dataTransfer) e.dataTransfer.effectAllowed = 'move';
      }}
      onDragEnd={() => {
        canvas.dragging.current = undefined;
      }}
    >
      <div className={styles.head}>
        <Icon className={styles.icon} aria-hidden />
        <div className={styles.text}>
          <Text weight="semibold">{summary}</Text>
          {step.description && (
            <Text size={200} className={styles.description}>
              {step.description}
            </Text>
          )}
        </div>
        <ProblemBadge problems={card.problems} />
        {!canvas.readOnly && (
          <Menu>
            <MenuTrigger disableButtonEnhancement>
              <Button size="small" appearance="subtle" icon={<MoreHorizontalRegular />} aria-label={t('studio.canvas.cardMenu')} title={t('studio.canvas.cardMenu')} />
            </MenuTrigger>
            <MenuPopover>
              <MenuList>
                <MenuItem icon={<ArrowUpRegular />} disabled={index === 0} onClick={() => move(index - 1)}>
                  {t('studio.canvas.moveUp')}
                </MenuItem>
                <MenuItem icon={<ArrowDownRegular />} disabled={index === count - 1} onClick={() => move(index + 2)}>
                  {t('studio.canvas.moveDown')}
                </MenuItem>
                <MenuItem icon={<CutRegular />} onClick={() => canvas.cut(step)}>
                  {t('studio.canvas.cut')}
                </MenuItem>
                <MenuItem
                  icon={<ClipboardPasteRegular />}
                  disabled={!canPaste}
                  title={pasteRefused ? t('studio.canvas.moveNotHere') : undefined}
                  onClick={paste}
                >
                  {t('studio.canvas.paste')}
                </MenuItem>
                <MenuItem icon={<DeleteRegular />} onClick={remove}>
                  {t('studio.canvas.delete')}
                </MenuItem>
              </MenuList>
            </MenuPopover>
          </Menu>
        )}
      </div>
      {(step.type === 'if' || step.type === 'wait') && step.branches.length > 0 && <BranchColumns step={step} place={place} />}
      {footer}
    </Card>
  );
}
