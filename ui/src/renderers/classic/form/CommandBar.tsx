import {
  makeStyles,
  Menu,
  MenuItem,
  MenuList,
  MenuPopover,
  MenuTrigger,
  mergeClasses,
  Overflow,
  OverflowItem,
  Toolbar,
  ToolbarButton,
  useIsOverflowItemVisible,
  useOverflowMenu,
} from '@fluentui/react-components';
import {
  bundleIcon,
  Add20Filled,
  Add20Regular,
  ArrowClockwise20Filled,
  ArrowClockwise20Regular,
  Checkmark20Filled,
  Checkmark20Regular,
  Delete20Filled,
  Delete20Regular,
  Dismiss20Filled,
  Dismiss20Regular,
  Edit20Filled,
  Edit20Regular,
  MoreHorizontal20Regular,
  Save20Filled,
  Save20Regular,
  Send20Filled,
  Send20Regular,
} from '@fluentui/react-icons';
import type { ComponentType, ReactElement } from 'react';
import { useMemo, useState } from 'react';
import { ConfirmDialog } from '../../../admin/common/ConfirmDialog';
import type { LookupValue } from '../../../admin/tables/formValues';
import type { RecordContext } from '../../../apps/record/loadRecord';
import { stateOfRecord, statesOf } from '../../../apps/record/recordStates';
import type { RecordForm } from '../../../apps/record/useRecordForm';
import type { RibbonButton } from '../../../apps/record/useRibbonCommands';
import { t } from '../../../shell/i18n/t';
import { useSurfaceStyles } from '../../../shell/layout/surface';
import { useAuth } from '../../../shell/auth/AuthProvider';
import { addToQueueOp } from '../../../apps/queues/queueCommands';
import { QueueTargetDialog } from '../../../apps/queues/QueueTargetDialog';
import { AssignDialog } from './AssignDialog';
import { saveAvailability } from './saveAvailability';
import { SetStateDialog } from './SetStateDialog';
import { ShareDialog } from './ShareDialog';
import { ActivateIcon, AddToQueueIcon, AssignIcon, DeactivateIcon, DeleteIcon, NewIcon, RefreshIcon, SaveCloseIcon, SaveIcon, ShareIcon } from '../../../shell/icons';


export interface CommandBarProps {
  ctx: RecordContext;
  state: Pick<RecordForm, 'dirty' | 'busy' | 'notification' | 'readOnly'>;
  onNew: () => void;
  onSave: () => void;
  onSaveAndClose: () => void;
  onDelete: () => void;
  onRefresh: () => void;
  onAssign: (owner: LookupValue) => Promise<boolean>;
  /** Activate or Deactivate (`statecode`, `statuscode`); false when it failed (the dialog stays open). */
  onSetState: (state: number, status?: number) => Promise<boolean>;
  /** The custom commands of the form's `RibbonDiffXml` whose rules hold (UI-1e), after the standard ones. */
  custom?: RibbonButton[];
  /** Closes the form (back to where it was opened from, or its side pane), shown as an X at the end of the bar. */
  onClose?: () => void;
}

/** UI-1e ruling E6: the `ModernImage` names OpenProject365 uses, as Fluent 20 bundled icons
 * (regular, filled on hover; filled in the modern renderer); any other name has no icon. */
const AcceptIcon = bundleIcon(Checkmark20Filled, Checkmark20Regular);
const DismissIcon = bundleIcon(Dismiss20Filled, Dismiss20Regular);
const AddIcon = bundleIcon(Add20Filled, Add20Regular);
const MODERN_IMAGES: Record<string, ComponentType> = {
  Accept: AcceptIcon,
  Cancel: DismissIcon,
  Send: bundleIcon(Send20Filled, Send20Regular),
  Close: DismissIcon,
  New: AddIcon,
  Add: AddIcon,
  Delete: bundleIcon(Delete20Filled, Delete20Regular),
  Edit: bundleIcon(Edit20Filled, Edit20Regular),
  Refresh: bundleIcon(ArrowClockwise20Filled, ArrowClockwise20Regular),
  Save: bundleIcon(Save20Filled, Save20Regular),
};

const iconOf = (name: string | undefined) => {
  const Icon = name ? MODERN_IMAGES[name] : undefined;
  return Icon ? <Icon /> : undefined;
};

/** A command of the bar; the ones that do not fit move, from the end, into the **More commands** menu. */
export interface Command {
  id: string;
  icon?: ReactElement;
  label: string;
  title?: string;
  disabled?: boolean;
  onClick: () => void;
}

const useStyles = makeStyles({
  bar: { flexWrap: 'nowrap', minWidth: 0 },
  // The commands take the bar's width and give it up from the end; the Close button stays outside them.
  items: { display: 'flex', flexGrow: 1, minWidth: 0, overflow: 'hidden' },
  button: { whiteSpace: 'nowrap', flexShrink: 0 },
});

/** The **…** button that holds the commands the bar has no room for, in their order (the view's bar has it too). */
export function MoreCommands({ commands }: { commands: Command[] }) {
  const { ref, isOverflowing } = useOverflowMenu<HTMLButtonElement>();
  if (!isOverflowing) return null;
  return (
    <Menu>
      <MenuTrigger disableButtonEnhancement>
        <ToolbarButton ref={ref} icon={<MoreHorizontal20Regular />} aria-label={t('record.cmd.more')} title={t('record.cmd.more')} />
      </MenuTrigger>
      <MenuPopover>
        <MenuList>
          {commands.map((c) => (
            <HiddenCommand key={c.id} command={c} />
          ))}
        </MenuList>
      </MenuPopover>
    </Menu>
  );
}

function HiddenCommand({ command }: { command: Command }) {
  const visible = useIsOverflowItemVisible(command.id);
  if (visible) return null;
  return (
    <MenuItem icon={command.icon} disabled={command.disabled} onClick={command.onClick}>
      {command.label}
    </MenuItem>
  );
}

type Pending = 'queueAdd' | 'delete' | 'refresh' | 'discardForAssign' | 'assign' | 'discardForSetState' | 'setState' | 'share' | undefined;

/**
 * The form's command bar, in Power Apps' order: New, Save, Save & Close, Delete, Activate or Deactivate, Refresh, Assign,
 * Share. A command the user may not use is hidden. Assign and Share exist for user- or team-owned tables only; Activate and
 * Deactivate for tables with a `statecode` of at least two states, on a record the user may write (ruling R1). The form's
 * custom commands (UI-1e) follow, in `Sequence` order.
 */
export function CommandBar({ ctx, state, onNew, onSave, onSaveAndClose, onDelete, onRefresh, onAssign, onSetState, custom = [], onClose }: CommandBarProps) {
  const surface = useSurfaceStyles();
  const styles = useStyles();
  const [pending, setPending] = useState<Pending>();
  const states = useMemo(() => statesOf(ctx.entity), [ctx.entity]);
  const recordState = stateOfRecord(ctx.record);
  const stateCommand =
    !!ctx.record && states.length > 1 && recordState !== undefined && ctx.rights.includes('Write') ? (recordState === 0 ? 'deactivate' : 'activate') : undefined;
  const existing = !!ctx.record;
  const owned = ctx.entity.ownership === 'UserOwned';
  const { shown: canSave, disabled: saveDisabled } = saveAvailability(ctx, state);
  const close = () => setPending(undefined);
  const id = ctx.fieldContext.record?.id;
  const set = ctx.entity.entitySetName;

  const commands: Command[] = [];
  if (ctx.canCreate) commands.push({ id: 'new', icon: <NewIcon />, label: t('record.cmd.new'), onClick: onNew });
  if (canSave) {
    commands.push({ id: 'save', icon: <SaveIcon />, label: t('record.cmd.save'), disabled: saveDisabled, onClick: onSave });
    commands.push({ id: 'saveAndClose', icon: <SaveCloseIcon />, label: t('record.cmd.saveAndClose'), disabled: saveDisabled, onClick: onSaveAndClose });
  }
  if (existing && ctx.rights.includes('Delete'))
    commands.push({ id: 'delete', icon: <DeleteIcon />, label: t('record.cmd.delete'), disabled: state.busy, onClick: () => setPending('delete') });
  if (stateCommand)
    commands.push({
      id: 'setState',
      icon: stateCommand === 'activate' ? <ActivateIcon /> : <DeactivateIcon />,
      label: t(stateCommand === 'activate' ? 'record.cmd.activate' : 'record.cmd.deactivate'),
      disabled: state.busy,
      onClick: () => setPending(state.dirty ? 'discardForSetState' : 'setState'),
    });
  if (existing)
    commands.push({ id: 'refresh', icon: <RefreshIcon />, label: t('record.cmd.refresh'), disabled: state.busy, onClick: () => (state.dirty ? setPending('refresh') : onRefresh()) });
  if (existing && owned && ctx.rights.includes('Assign'))
    commands.push({ id: 'assign', icon: <AssignIcon />, label: t('record.cmd.assign'), disabled: state.busy, onClick: () => setPending(state.dirty ? 'discardForAssign' : 'assign') });
  if (existing && ctx.entity.isValidForQueue === true && !!id)
    commands.push({ id: 'queueAdd', icon: <AddToQueueIcon />, label: t('apps.queues.add'), disabled: state.busy, onClick: () => setPending('queueAdd') });
  if (existing && owned && ctx.rights.includes('Share'))
    commands.push({ id: 'share', icon: <ShareIcon />, label: t('record.cmd.share'), disabled: state.busy, onClick: () => setPending('share') });
  for (const { command, busy, run } of custom)
    commands.push({ id: `custom:${command.id}`, icon: iconOf(command.icon), label: command.label, title: command.tooltip, disabled: busy, onClick: run });

  return (
    <>
      <Toolbar className={mergeClasses(surface.commandBar, styles.bar)} aria-label={t('record.commands')}>
        <Overflow padding={40}>
          <div className={styles.items}>
            {commands.map((c) => (
              <OverflowItem key={c.id} id={c.id}>
                <ToolbarButton className={styles.button} icon={c.icon} title={c.title} disabled={c.disabled} onClick={c.onClick}>
                  {c.label}
                </ToolbarButton>
              </OverflowItem>
            ))}
            <MoreCommands commands={commands} />
          </div>
        </Overflow>
        {onClose && <ToolbarButton icon={<DismissIcon />} aria-label={t('record.cmd.close')} title={t('record.cmd.close')} onClick={onClose} />}
      </Toolbar>
      <ConfirmDialog
        open={pending === 'delete'}
        title={t('record.delete.title')}
        body={t('record.delete.body')}
        confirmLabel={t('record.cmd.delete')}
        cancelLabel={t('common.cancel')}
        onCancel={close}
        onConfirm={() => {
          close();
          onDelete();
        }}
      />
      <ConfirmDialog
        open={pending === 'refresh' || pending === 'discardForAssign' || pending === 'discardForSetState'}
        title={t('record.discard.title')}
        body={t('record.discard.body')}
        confirmLabel={t('record.discard.confirm')}
        cancelLabel={t('common.cancel')}
        onCancel={close}
        onConfirm={() => {
          // Assign reads the record again, which replaces the form's values: it asks like Refresh does.
          if (pending === 'discardForAssign') setPending('assign');
          else if (pending === 'discardForSetState') setPending('setState');
          else {
            close();
            onRefresh();
          }
        }}
      />
      {pending === 'assign' && <AssignDialog onAssign={onAssign} onClose={close} error={state.notification} />}
      {pending === 'setState' && stateCommand && (
        <SetStateDialog mode={stateCommand} states={states} onSetState={onSetState} onClose={close} error={state.notification} />
      )}
      {pending === 'queueAdd' && id && <AddToQueue table={ctx.entity} id={id} onClose={close} />}
      {pending === 'share' && id && set && <ShareDialog set={set} id={id} onClose={close} />}
    </>
  );
}

/** The form's Add to queue: the queue lookup, then AddToQueue for the open record. */
function AddToQueue({ table, id, onClose }: { table: RecordContext['entity']; id: string; onClose: () => void }) {
  const { api } = useAuth();
  if (!api) return null;
  const target = { logicalName: table.logicalName, primaryId: table.primaryIdAttribute };
  return <QueueTargetDialog mode="add" onConfirm={async (queue) => (await addToQueueOp(api, target, queue.id)(id), true)} onClose={onClose} />;
}
