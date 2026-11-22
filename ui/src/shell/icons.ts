import {
  bundleIcon,
  type FluentIcon,
  ChevronDownFilled,
  AttachFilled,
  DismissFilled,
  OpenFilled,
  EditFilled,
  ArrowDownloadFilled,
  TableFilled,
  BuildingFilled,
  PersonFilled,
  BoardFilled,
  DocumentFilled,
  GlobeFilled,
  NoteFilled,
  CommentFilled,
  TaskListSquareLtrFilled,
  MailFilled,
  CallFilled,
  CalendarLtrFilled,
  DocumentTextFilled,
  PrintFilled,
  ClipboardTaskFilled,
  AddFilled,
  AddRegular,
  ArrowClockwiseFilled,
  ArrowClockwiseRegular,
  ArrowCollapseAllFilled,
  ArrowCollapseAllRegular,
  ArrowExpandAllFilled,
  ArrowExpandAllRegular,
  ArrowExportFilled,
  ArrowExportRegular,
  ArrowImportFilled,
  ArrowImportRegular,
  ArrowPreviousFilled,
  ArrowUpFilled,
  ArrowUpRegular,
  ArrowDownFilled,
  ArrowDownRegular,
  ArrowCircleUpFilled,
  ArrowCircleUpRegular,
  ArrowCircleDownFilled,
  ArrowCircleDownRegular,
  ArrowPreviousRegular,
  ArrowLeftFilled,
  ArrowLeftRegular,
  GridFilled,
  GridRegular,
  ArrowSortDownFilled,
  ArrowSortDownRegular,
  ArrowSortUpFilled,
  ArrowSortUpRegular,
  ArrowDownloadRegular,
  AttachRegular,
  EditRegular,
  CalendarLtrRegular,
  CallRegular,
  CheckmarkCircleFilled,
  CheckmarkCircleRegular,
  ChevronDownRegular,
  ChevronLeftFilled,
  ChevronLeftRegular,
  ChevronRightFilled,
  ChevronRightRegular,
  ClipboardTaskRegular,
  DeleteFilled,
  SearchFilled,
  TableRegular,
  BoardRegular,
  BuildingRegular,
  PersonRegular,
  GlobeRegular,
  DocumentRegular,
  SearchRegular,
  DeleteRegular,
  DismissCircleFilled,
  DismissCircleRegular,
  DismissRegular,
  DocumentTextRegular,
  FilterFilled,
  FilterRegular,
  LinkFilled,
  LinkRegular,
  MailRegular,
  MoreHorizontalFilled,
  MoreHorizontalRegular,
  NoteRegular,
  CommentRegular,
  OpenRegular,
  PersonArrowRightFilled,
  PersonArrowRightRegular,
  PrintRegular,
  SaveArrowRightFilled,
  SaveArrowRightRegular,
  SaveFilled,
  SaveRegular,
  ShareFilled,
  ShareRegular,
  TaskListSquareLtrRegular,
  NavigationFilled,
  NavigationRegular,
  FlowchartFilled,
  FlowchartRegular,
} from '@fluentui/react-icons';

/**
 * One bundled (Regular, Filled on hover; always Filled in the modern renderer, see ModernAppFrame) icon per command, so the same command has the same icon everywhere: the form
 * command bar, the list and subgrid buttons and the admin pages. Icons are decorative (`aria-hidden`); every command
 * keeps its text label.
 */
/** The modern renderer's navigation rail toggle. */
export const NavMenuIcon = bundleIcon(NavigationFilled, NavigationRegular);
export const NewIcon = bundleIcon(AddFilled, AddRegular);
export const SaveIcon = bundleIcon(SaveFilled, SaveRegular);
export const SaveCloseIcon = bundleIcon(SaveArrowRightFilled, SaveArrowRightRegular);
export const DeleteIcon = bundleIcon(DeleteFilled, DeleteRegular);
export const ActivateIcon = bundleIcon(CheckmarkCircleFilled, CheckmarkCircleRegular);
export const DeactivateIcon = bundleIcon(DismissCircleFilled, DismissCircleRegular);
export const RefreshIcon = bundleIcon(ArrowClockwiseFilled, ArrowClockwiseRegular);
export const AssignIcon = bundleIcon(PersonArrowRightFilled, PersonArrowRightRegular);
export const ShareIcon = bundleIcon(ShareFilled, ShareRegular);
export const AddExistingIcon = bundleIcon(LinkFilled, LinkRegular);
export const ImportIcon = bundleIcon(ArrowImportFilled, ArrowImportRegular);
export const ExportIcon = bundleIcon(ArrowExportFilled, ArrowExportRegular);
export const FirstPageIcon = bundleIcon(ArrowPreviousFilled, ArrowPreviousRegular);
export const PreviousPageIcon = bundleIcon(ChevronLeftFilled, ChevronLeftRegular);
export const NextPageIcon = bundleIcon(ChevronRightFilled, ChevronRightRegular);
/** Moves an item up or down in a list (the form designer's sections and fields). */
export const MoveUpIcon = bundleIcon(ArrowUpFilled, ArrowUpRegular);
export const MoveDownIcon = bundleIcon(ArrowDownFilled, ArrowDownRegular);
/** Moves an item to the previous or next container (the form designer's fields between sections). */
export const MovePreviousIcon = bundleIcon(ArrowCircleUpFilled, ArrowCircleUpRegular);
export const MoveNextIcon = bundleIcon(ArrowCircleDownFilled, ArrowCircleDownRegular);
export const ExpandAllIcon = bundleIcon(ArrowExpandAllFilled, ArrowExpandAllRegular);
export const CollapseAllIcon = bundleIcon(ArrowCollapseAllFilled, ArrowCollapseAllRegular);
export const MoreIcon = bundleIcon(MoreHorizontalFilled, MoreHorizontalRegular);
export const SortNewestIcon = bundleIcon(ArrowSortDownFilled, ArrowSortDownRegular);
export const SortOldestIcon = bundleIcon(ArrowSortUpFilled, ArrowSortUpRegular);
/** A filter button: outlined while no filter is applied, filled while one is. */
export const FilterIcon = FilterRegular;
export const FilterAppliedIcon = FilterFilled;
export const ExpandItemIcon = bundleIcon(ChevronRightFilled, ChevronRightRegular);
export const CollapseItemIcon = bundleIcon(ChevronDownFilled, ChevronDownRegular);
export const AttachmentIcon = bundleIcon(AttachFilled, AttachRegular);
export const DismissIcon = bundleIcon(DismissFilled, DismissRegular);
export const OpenIcon = bundleIcon(OpenFilled, OpenRegular);
export const EditIcon = bundleIcon(EditFilled, EditRegular);
export const BackIcon = bundleIcon(ArrowLeftFilled, ArrowLeftRegular);
export const ShowAsIcon = bundleIcon(GridFilled, GridRegular);
export const DownloadIcon = bundleIcon(ArrowDownloadFilled, ArrowDownloadRegular);
/** Site map entries: a table (with a few well-known ones), a dashboard, a page or web resource, an external address. */
const PersonIcon = bundleIcon(PersonFilled, PersonRegular);
export const SITEMAP_TABLE_ICONS: Record<string, FluentIcon> = {
  account: bundleIcon(BuildingFilled, BuildingRegular),
  contact: PersonIcon,
  systemuser: PersonIcon,
};
export const SiteMapTableIcon = bundleIcon(TableFilled, TableRegular);
export const SiteMapDashboardIcon = bundleIcon(BoardFilled, BoardRegular);
export const SiteMapPageIcon = bundleIcon(DocumentFilled, DocumentRegular);
export const SiteMapUrlIcon = bundleIcon(GlobeFilled, GlobeRegular);
/** Look up a record (the lookup field's search). */
export const LookupSearchIcon = bundleIcon(SearchFilled, SearchRegular);
/** Complete an activity: the same check as Activate. */
export const CompleteIcon = ActivateIcon;

/** The icon of a timeline record by its table (a note is `annotation`); any other activity table gets the generic one. */
export const RECORD_TYPE_ICONS: Record<string, FluentIcon> = {
  annotation: bundleIcon(NoteFilled, NoteRegular),
  post: bundleIcon(CommentFilled, CommentRegular),
  task: bundleIcon(TaskListSquareLtrFilled, TaskListSquareLtrRegular),
  email: bundleIcon(MailFilled, MailRegular),
  phonecall: bundleIcon(CallFilled, CallRegular),
  appointment: bundleIcon(CalendarLtrFilled, CalendarLtrRegular),
  letter: bundleIcon(DocumentTextFilled, DocumentTextRegular),
  fax: bundleIcon(PrintFilled, PrintRegular),
};
export const ActivityIcon = bundleIcon(ClipboardTaskFilled, ClipboardTaskRegular);
/** The workflow studio (its tile on /apps). */
export const WorkflowStudioIcon = bundleIcon(FlowchartFilled, FlowchartRegular);
