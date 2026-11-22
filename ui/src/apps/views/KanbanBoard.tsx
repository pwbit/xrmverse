import { Badge, Button, Card, Link, makeStyles, MessageBar, MessageBarBody, Menu, MenuItem, MenuList, MenuPopover, MenuTrigger, Text, tokens } from '@fluentui/react-components';
import { useEffect, useRef, useState } from 'react';
import type { ViewColumn } from '../../model/view';
import { ChoiceBadge, ChoiceBadges } from '../../renderers/modern/ChoiceBadge';
import { badgeColors, choiceKey } from '../../renderers/modern/choiceColors';
import { usePresentationModel } from '../../renderers/modern/PresentationContext';
import { useChoicePalette } from '../../renderers/modern/useChoicePalette';
import { t } from '../../shell/i18n/t';
import { MoreIcon } from '../../shell/icons';
import { cellText } from '../runView';
import type { ChoiceOption, ColumnKind } from './columnKinds';
import { moveBody } from './moveCard';

export interface KanbanBoardProps {
  label: string;
  /** The table of the rows: its coloured choice values show as badges on the cards. */
  table?: string;
  columns: ViewColumn[];
  headers: Record<string, string>;
  groupBy: string;
  column: Extract<ColumnKind, { kind: 'choice' }>;
  rows: Record<string, unknown>[];
  primaryId: string;
  /** Rejects with the server's error. */
  onMove(id: string, body: Record<string, unknown>): Promise<void>;
  onOpen(id: string): void;
}

const useStyles = makeStyles({
  board: { display: 'flex', alignItems: 'flex-start', gap: tokens.spacingHorizontalM, overflowX: 'auto', paddingBottom: tokens.spacingVerticalM },
  column: {
    flex: '0 0 260px',
    width: '260px',
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
    backgroundColor: tokens.colorNeutralBackground3,
    borderRadius: tokens.borderRadiusMedium,
    padding: tokens.spacingHorizontalS,
  },
  over: { backgroundColor: tokens.colorNeutralBackground3Hover },
  head: { display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: tokens.spacingHorizontalS },
  list: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalS, minHeight: '48px' },
  card: { cursor: 'grab' },
  cardHead: { display: 'flex', alignItems: 'flex-start', justifyContent: 'space-between', gap: tokens.spacingHorizontalXS },
  link: { textAlign: 'start', overflowWrap: 'anywhere' },
});

/** Where a card sits: an option's value, none (null) or other (a value that is no option). */
type Place = { option: number | boolean } | 'none' | 'other';
const NONE = 'none';
const OTHER = 'other';
const DATA = 'text/plain';

interface Card {
  key: string;
  id?: string;
  row: Record<string, unknown>;
}

/** A board of fixed-width columns, one per option of the grouping column; cards move by drag and drop or by a menu. */
export function KanbanBoard({ label, table, columns, headers, groupBy, column, rows, primaryId, onMove, onOpen }: KanbanBoardProps) {
  const styles = useStyles();
  const palette = useChoicePalette(table, columns);
  const choices = usePresentationModel()?.choices ?? {};
  // A lane of a coloured option shows the option as its badge (the overlay's text colour needs the table).
  const laneColors = (o: ChoiceOption) =>
    badgeColors({ value: Number(o.value), color: o.color }, table ? choiceKey(table, groupBy, column.globalName) : '', table ? choices : {});
  // Moves made and not yet confirmed by the rows: card id -> the option (null = none) it was moved to.
  const [moved, setMoved] = useState<Record<string, number | boolean | null>>({});
  const [failure, setFailure] = useState<string>();
  const [over, setOver] = useState<string>();
  const dragging = useRef<string>();

  // New rows (after a reload) replace the local moves.
  useEffect(() => setMoved({}), [rows]);

  const cards: Card[] = rows.map((row, index) => {
    const value = row[primaryId];
    const id = typeof value === 'string' && value ? value : undefined;
    return { key: id ?? `row-${index}`, id, row };
  });
  const options = column.options;
  const placeOf = (card: Card): Place => {
    const value = card.id !== undefined && card.id in moved ? moved[card.id] : (card.row[groupBy] ?? null);
    if (value === null) return NONE;
    return options.some((o) => o.value === value) ? { option: value as number | boolean } : OTHER;
  };
  // The (none) column is shown while a card sits in it; a card elsewhere may then be moved there from its menu.
  const showNone = cards.some((c) => placeOf(c) === NONE);
  const places = cards.map((card) => ({ card, place: placeOf(card) }));
  const inColumn = (match: (p: Place) => boolean) => places.filter((p) => match(p.place)).map((p) => p.card);

  const move = (card: Card, target: number | boolean | null) => {
    const id = card.id;
    if (id === undefined) return;
    setFailure(undefined);
    setMoved((m) => ({ ...m, [id]: target }));
    onMove(id, moveBody(groupBy, column, card.row, target)).catch((e: unknown) => {
      setMoved((m) => {
        const { [id]: _gone, ...rest } = m;
        return rest;
      });
      setFailure(e instanceof Error ? e.message : String(e));
    });
  };

  const drop = (target: number | boolean | null | undefined, dataTransfer: DataTransfer | null) => {
    setOver(undefined);
    const id = dataTransfer?.getData(DATA) || dragging.current;
    dragging.current = undefined;
    const card = cards.find((c) => c.id === id);
    if (!card || target === undefined) return;
    const place = placeOf(card);
    const same = target === null ? place === NONE : typeof place === 'object' && place.option === target;
    if (!same) move(card, target);
  };

  const cardView = (card: Card) => {
    const [first, ...rest] = columns;
    const extra = rest
      .map((c) => ({ c, text: cellText(card.row, c), badges: c.alias ? undefined : palette(c, card.row[c.attribute]) }))
      .filter((x) => x.text !== '')
      .slice(0, 3);
    const title = first ? cellText(card.row, first) : '';
    return (
      <Card
        key={card.key}
        role="listitem"
        size="small"
        className={styles.card}
        draggable={card.id !== undefined}
        onDragStart={(e: React.DragEvent) => {
          dragging.current = card.id;
          e.dataTransfer?.setData(DATA, card.id ?? '');
          if (e.dataTransfer) e.dataTransfer.effectAllowed = 'move';
        }}
        onDragEnd={() => {
          dragging.current = undefined;
          setOver(undefined);
        }}
      >
        <div className={styles.cardHead}>
          <Link as="button" className={styles.link} onClick={() => card.id && onOpen(card.id)}>
            {title}
          </Link>
          {card.id !== undefined && (
            <Menu>
              <MenuTrigger disableButtonEnhancement>
                <Button size="small" appearance="subtle" icon={<MoreIcon />} aria-label={t('apps.views.kanban.moveTo')} />
              </MenuTrigger>
              <MenuPopover>
                <MenuList>
                  {options
                    .filter((o) => {
                      const place = placeOf(card);
                      return typeof place !== 'object' || place.option !== o.value;
                    })
                    .map((o) => (
                      <MenuItem key={String(o.value)} onClick={() => move(card, o.value)}>
                        {o.label}
                      </MenuItem>
                    ))}
                  {showNone && placeOf(card) !== NONE && <MenuItem onClick={() => move(card, null)}>{t('apps.views.kanban.none')}</MenuItem>}
                </MenuList>
              </MenuPopover>
            </Menu>
          )}
        </div>
        {extra.map(({ c, text, badges }) => (
          <Text key={c.name} size={200} title={headers[c.name]}>
            {badges ? <ChoiceBadges badges={badges} /> : text}
          </Text>
        ))}
      </Card>
    );
  };

  const lane = (key: string, name: string, items: Card[], target: number | boolean | null | undefined, colors?: { background: string; text: string }) => (
    <div key={key} data-column={key} role="region" aria-label={name} className={styles.column + (over === key ? ` ${styles.over}` : '')}>
      <div className={styles.head}>
        {colors ? <ChoiceBadge label={name} {...colors} /> : <Text weight="semibold">{name}</Text>}
        <Badge appearance="tint" color="informative" aria-hidden>
          {items.length}
        </Badge>
      </div>
      <div
        role="list"
        aria-label={name}
        className={styles.list}
        onDragOver={(e) => {
          if (target === undefined) return;
          e.preventDefault();
          setOver(key);
        }}
        onDragLeave={() => setOver((o) => (o === key ? undefined : o))}
        onDrop={(e) => {
          if (target === undefined) return;
          e.preventDefault();
          drop(target, e.dataTransfer);
        }}
      >
        {items.map(cardView)}
      </div>
    </div>
  );

  const none = inColumn((p) => p === NONE);
  const other = inColumn((p) => p === OTHER);
  return (
    <div aria-label={label} data-mode="kanban">
      {failure !== undefined && (
        <MessageBar intent="error">
          <MessageBarBody>{t('apps.views.kanban.moveFailed', { detail: failure })}</MessageBarBody>
        </MessageBar>
      )}
      <div className={styles.board}>
        {options.map((o) => lane(`option-${String(o.value)}`, o.label, inColumn((p) => typeof p === 'object' && p.option === o.value), o.value, laneColors(o)))}
        {none.length > 0 && lane('none', t('apps.views.kanban.none'), none, null)}
        {other.length > 0 && lane('other', t('apps.views.kanban.other'), other, undefined)}
      </div>
    </div>
  );
}
