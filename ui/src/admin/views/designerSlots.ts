import type { AttributeMeta } from '../tables/metadata';
import type { ViewDesign } from './viewDesign';

/**
 * What every card of the view designer gets: the design the page holds, the table's own columns, and `onChange` with the
 * next design (the page keeps it, marks the page changed and refreshes the preview). A card never mutates `design`.
 */
export interface DesignCardProps {
  design: ViewDesign;
  attributes: AttributeMeta[];
  onChange: (next: ViewDesign) => void;
}

export { DesignerContext, useAfterSave, useDesignerHooks } from '../common/designerHooks';
export type { AfterSave, DesignerHooks } from '../common/designerHooks';
