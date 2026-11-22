import { useMediaQuery } from '../../shell/layout/useMediaQuery';
import { ViewGrid, type ViewGridProps } from '../classic/ViewGrid';
import { CardList } from './CardList';
import { useChoiceCells } from './ChoiceBadge';

/** The narrow-screen breakpoint below which a list shows as cards (UI-2b). */
export const NARROW_QUERY = '(max-width: 640px)';

/** The modern ViewGrid slot: cards when asked for (`layout="cards"`) or on a narrow screen, else the classic grid; choice values as badges. */
export function ModernViewGrid(props: ViewGridProps) {
  const narrow = useMediaQuery(NARROW_QUERY);
  // Coloured choice values as badges (the table's option colours, the overlay's text colours).
  const renderCellContent = useChoiceCells(props.table, props.columns) ?? props.renderCellContent;
  const shown = { ...props, renderCellContent };
  return props.layout === 'cards' || narrow ? <CardList {...shown} /> : <ViewGrid {...shown} />;
}
