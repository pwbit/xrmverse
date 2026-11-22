import { makeStyles, Overflow, OverflowItem, Toolbar, ToolbarButton, ToolbarDivider, tokens } from '@fluentui/react-components';
import { useState, type ReactNode } from 'react';
import { ConfirmDialog } from '../../admin/common/ConfirmDialog';
import type { LookupValue } from '../../admin/tables/formValues';
import { AssignDialog } from '../../renderers/classic/form/AssignDialog';
import { MoreCommands, type Command } from '../../renderers/classic/form/CommandBar';
import { SetStateDialog } from '../../renderers/classic/form/SetStateDialog';
import { ShareDialog } from '../../renderers/classic/form/ShareDialog';
import type { XrmApi } from '../../shell/api/client';
import { t } from '../../shell/i18n/t';
import { ActivateIcon, AddToQueueIcon, AssignIcon, DeactivateIcon, DeleteIcon, NewIcon, PickIcon, ReleaseIcon, RefreshIcon, RemoveFromQueueIcon, RouteIcon, ShareIcon } from '../../shell/icons';
import { addToQueueOp, currentUserId, pickOp, queueCommandState, releaseOp, removeOp, routeOp, type QueueTable } from '../queues/queueCommands';
import { QueueTargetDialog } from '../queues/QueueTargetDialog';
import type { RecordStateMeta } from '../record/recordStates';
import { assignOp, commandState, deleteOp, setStateOp, type Enabled, type SelectedRecord } from './viewCommands';

export interface ViewCommandBarProps {
  api?: XrmApi;
  /** The listed table's entity set. */
  set?: string;
  /** Opens the create form; absent when the user may not create (New is hidden). */
  onNew?: () => void;
  /** The selected records of the grid or the cards; absent on the board and the calendar, where the bar shows New only. */
  selected?: SelectedRecord[];
  /** The table's states (`statesOf`); fewer than two: no Activate or Deactivate. */
  states: RecordStateMeta[];
  /** The table is user- or team-owned: Assign and Share exist. */
  owned: boolean;
  /** The user holds the table's Delete privilege: Delete exists. */
  canDelete?: boolean;
  /** The listed table, for the queue commands: Add to queue where `isValidForQueue`, Pick, Release, Route and Remove on `queueitem`. */
  queue?: QueueTable;
  /** Reads the records again; absent: no Refresh. */
  onRefresh?: () => void;
  /** Runs `op` on each selected record (the bulk runner), shows the result and reloads the view. */
  onRun: (op: (id: string) => Promise<void>) => Promise<void>;
  /** The start of the first row: the back button, the table's name and the view selector. */
  leading?: ReactNode;
  /** Before the commands: the "Show as" menu. */
  modes?: ReactNode;
  /** After Share, at the end of the first row: the view actions. */
  trailing?: ReactNode;
  /** The second row: the view's search box. */
  below?: ReactNode;
}

const useStyles = makeStyles({
  // The view's header: one card (border and shadow) on two rows, as the grid under it.
  card: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXS,
    padding: `${tokens.spacingVerticalXS} ${tokens.spacingHorizontalM} ${tokens.spacingVerticalS}`,
    marginBlock: tokens.spacingVerticalS,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusLarge,
    boxShadow: tokens.shadow4,
    backgroundColor: tokens.colorNeutralBackground1,
  },
  // On a narrow page the toolbar goes under the view's name.
  row: { display: 'flex', alignItems: 'center', flexWrap: 'wrap', gap: tokens.spacingHorizontalXS, minWidth: 0 },
  leading: { display: 'flex', alignItems: 'center', gap: tokens.spacingHorizontalXS, minWidth: 0, flexShrink: 1 },
  // The commands sit at the end of the row, after the leading part.
  bar: { flexWrap: 'nowrap', flexGrow: 1, flexBasis: '280px', minWidth: 0, padding: 0, justifyContent: 'flex-end' },
  items: { display: 'flex', alignItems: 'center', flexGrow: 1, minWidth: 0, overflow: 'hidden', justifyContent: 'flex-end' },
  button: { whiteSpace: 'nowrap', flexShrink: 0 },
  fixed: { flexShrink: 0 },
  below: { display: 'flex', alignItems: 'center', gap: tokens.spacingHorizontalS, flexWrap: 'wrap' },
});

type Pending = 'activate' | 'deactivate' | 'assign' | 'share' | 'delete' | 'queueAdd' | 'queueRoute' | undefined;

/**
 * The header of a view, in one card on two rows: the back button, the table's name and the view selector, then "Show as",
 * the commands and their overflow, Share and the view actions; the search box under them. The commands are New, then
 * Delete, Activate, Deactivate, Refresh and Assign on the selected records, with the form's toolbar and overflow (ruling W8).
 * A disabled command tells why in its tooltip. Each record command runs one request per record through `onRun` (ruling
 * W9), with the form's requests; its dialog closes when the run is over.
 */
export function ViewCommandBar({ api, set, onNew, selected, states, owned, canDelete, queue, onRefresh, onRun, leading, modes, trailing, below }: ViewCommandBarProps) {
  const styles = useStyles();
  const [pending, setPending] = useState<Pending>();
  const close = () => setPending(undefined);
  const hasState = states.length > 1;
  const enablement = selected ? commandState(selected, { hasState }) : undefined;

  const commands: Command[] = [];
  if (onNew) commands.push({ id: 'new', icon: <NewIcon />, label: t('apps.views.new'), onClick: onNew });
  const record = (id: Exclude<Pending, undefined>, icon: Command['icon'], label: string, state: Enabled | undefined) => {
    // An empty reason: the command does not apply to the table.
    if (!state || (!state.enabled && !state.reason)) return;
    commands.push({ id, icon, label, disabled: !state.enabled, title: state.enabled ? undefined : state.reason, onClick: () => setPending(id) });
  };
  if (canDelete) record('delete', <DeleteIcon />, t('record.cmd.delete'), enablement?.delete);
  record('activate', <ActivateIcon />, t('record.cmd.activate'), enablement?.activate);
  record('deactivate', <DeactivateIcon />, t('record.cmd.deactivate'), enablement?.deactivate);
  if (onRefresh) commands.push({ id: 'refresh', icon: <RefreshIcon />, label: t('record.cmd.refresh'), onClick: onRefresh });
  if (owned) record('assign', <AssignIcon />, t('record.cmd.assign'), enablement?.assign);
  const queueState = queue && selected ? queueCommandState(selected.length, queue) : undefined;
  const queueCommand = (id: string, icon: Command['icon'], label: string, state: Enabled | undefined, run: () => void) => {
    if (!state || (!state.enabled && !state.reason)) return;
    commands.push({ id, icon, label, disabled: !state.enabled, title: state.enabled ? undefined : state.reason, onClick: run });
  };
  queueCommand('queueAdd', <AddToQueueIcon />, t('apps.queues.add'), queueState?.add, () => setPending('queueAdd'));
  if (api) {
    queueCommand('queuePick', <PickIcon />, t('apps.queues.pick'), queueState?.pick, () => {
        // The worker is read inside the op, so a failed WhoAmI is reported like any other failure of the run.
        let worker: Promise<string> | undefined;
        void onRun(async (id) => pickOp(api, await (worker ??= currentUserId(api)))(id));
      });
    queueCommand('queueRelease', <ReleaseIcon />, t('apps.queues.release'), queueState?.release, () => void onRun(releaseOp(api)));
    queueCommand('queueRoute', <RouteIcon />, t('apps.queues.route'), queueState?.route, () => setPending('queueRoute'));
    queueCommand('queueRemove', <RemoveFromQueueIcon />, t('apps.queues.remove'), queueState?.remove, () => void onRun(removeOp(api)));
  }
  const share = owned ? enablement?.share : undefined;

  /** Runs `op` on the selection; the dialog closes afterwards (the result shows above the view). */
  const runAll = async (op: (id: string) => Promise<void>): Promise<boolean> => {
    await onRun(op);
    return true;
  };
  const ids = (selected ?? []).map((r) => r.id);

  return (
    <>
      <div className={styles.card} role="region" aria-label={t('apps.views.header')}>
        <div className={styles.row}>
          {leading && <div className={styles.leading}>{leading}</div>}
          <Toolbar className={styles.bar} aria-label={t('apps.views.toolbar')}>
            {/* The room "Show as" takes is kept out of the commands' measure, so it is never clipped. */}
            <Overflow padding={modes ? 240 : 40}>
              <div className={styles.items}>
                {/* "Show as" comes first and never moves into the overflow menu. */}
                {modes && (
                  <>
                    <span className={styles.fixed}>{modes}</span>
                    {commands.length > 0 && <ToolbarDivider />}
                  </>
                )}
                {commands.map((c) => (
                  <OverflowItem key={c.id} id={c.id}>
                    {/* disabledFocusable: a disabled command still shows its reason on hover and focus. */}
                    <ToolbarButton className={styles.button} icon={c.icon} title={c.title} disabledFocusable={c.disabled} onClick={c.onClick}>
                      {c.label}
                    </ToolbarButton>
                  </OverflowItem>
                ))}
                <MoreCommands commands={commands} />
              </div>
            </Overflow>
            {(share || trailing) && <ToolbarDivider />}
            {share && (
              <ToolbarButton
                className={styles.fixed}
                icon={<ShareIcon />}
                aria-label={t('record.cmd.share')}
                title={share.enabled ? t('record.cmd.share') : share.reason}
                disabledFocusable={!share.enabled}
                onClick={() => setPending('share')}
              />
            )}
            {trailing && <span className={styles.fixed}>{trailing}</span>}
          </Toolbar>
        </div>
        {below && <div className={styles.below}>{below}</div>}
      </div>
      {(pending === 'activate' || pending === 'deactivate') && api && set && (
        <SetStateDialog
          mode={pending}
          states={states}
          count={ids.length}
          onSetState={(state, status) => runAll(setStateOp(api, set, state, status))}
          onClose={close}
        />
      )}
      <ConfirmDialog
        open={pending === 'delete' && !!api && !!set}
        title={t('apps.views.records.deleteTitle')}
        body={t('apps.views.records.deleteBody', { count: ids.length })}
        confirmLabel={t('record.cmd.delete')}
        cancelLabel={t('common.cancel')}
        onCancel={close}
        onConfirm={() => {
          close();
          if (api && set) void onRun(deleteOp(api, set));
        }}
      />
      {pending === 'assign' && api && set && (
        <AssignDialog count={ids.length} onAssign={(owner: LookupValue) => runAll(assignOp(api, set, owner))} onClose={close} />
      )}
      {pending === 'queueAdd' && api && queue && (
        <QueueTargetDialog mode="add" count={ids.length} onConfirm={(target) => runAll(addToQueueOp(api, queue, target.id))} onClose={close} />
      )}
      {pending === 'queueRoute' && api && (
        <QueueTargetDialog mode="route" count={ids.length} onConfirm={(target) => runAll(routeOp(api, target))} onClose={close} />
      )}
      {pending === 'share' && set && ids[0] && (
        ids.length > 1 ? (
          <ShareDialog set={set} id={ids[0]} ids={ids} onBulk={runAll} onClose={close} />
        ) : (
          <ShareDialog set={set} id={ids[0]} onClose={close} />
        )
      )}
    </>
  );
}
