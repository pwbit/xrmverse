export interface ViewColumn {
  /** The cell name as written, e.g. `name` or `pc.fullname`. */
  name: string;
  width: number;
  /** The link-entity alias of an `alias.attribute` cell. */
  alias?: string;
  attribute: string;
  /** `ishidden="1"`: the grid does not render the column. */
  isHidden: boolean;
}

export interface ViewOrder {
  attribute: string;
  descending: boolean;
  /** The aggregate alias the order names (`<order alias>`). */
  alias?: string;
  /** The alias of the `<link-entity>` the order sits in (#100); absent for a root order. */
  linkAlias?: string;
}

export interface ViewModel {
  id: string;
  name: string;
  entity: string;
  queryType: number;
  isDefault: boolean;
  fetchXml: string;
  columns: ViewColumn[];
  order: ViewOrder[];
  /** The view's LayoutXml as stored (#101: a saved view keeps the columns and widths it was opened with). */
  layoutXml?: string;
  /** The owner of a personal view (`userquery._ownerid_value`, lowercase); absent for a public view. */
  ownerId?: string;
}
