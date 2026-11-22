import { Button, MessageBar, MessageBarBody, MessageBarTitle } from '@fluentui/react-components';
import { useEffect, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { ConfirmDialog } from '../../admin/common/ConfirmDialog';
import type { XrmApi } from '../../shell/api/client';
import { errorText } from '../../shell/api/errors';
import { t } from '../../shell/i18n/t';
import { ActivateIcon, DeactivateIcon, DeleteIcon, OpenIcon } from '../../shell/icons';
import { useNotify } from '../../shell/notify/Notifications';
import { deleteWorkflow, setWorkflowState, type ProcessState, type WorkflowSummary } from '../api/studioApi';
import { EditorHeader } from './EditorHeader';
import { XamlDialog } from './XamlDialog';


/**
 * The read-only page of a process the YAML format cannot represent (spec §4.7, ruling W5): the server's message verbatim (it
 * is not parsed), View XAML, Activate / Deactivate and Delete. There is no canvas and no code view.
 */
export function NotRepresentable({ api, id, message, summary, managed }: { api: XrmApi; id: string; message: string; summary?: WorkflowSummary; managed: boolean }) {
  const notify = useNotify();
  const navigate = useNavigate();
  const [state, setState] = useState<ProcessState | undefined>(summary?.state);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string>();
  const [viewing, setViewing] = useState(false);
  const [deleting, setDeleting] = useState(false);
  const name = summary?.name ?? id;

  useEffect(() => setState(summary?.state), [summary]);

  async function changeState(activate: boolean) {
    setBusy(true);
    setError(undefined);
    try {
      const after = await setWorkflowState(api, id, activate);
      setState(after?.state ?? (activate ? 'activated' : 'draft'));
      notify.success(t(activate ? 'studio.done.activated' : 'studio.done.deactivated', { name }));
    } catch (e) {
      setError(errorText(e));
    } finally {
      setBusy(false);
    }
  }

  async function remove() {
    setDeleting(false);
    setBusy(true);
    setError(undefined);
    try {
      await deleteWorkflow(api, id);
      notify.success(t('studio.done.deleted', { name }));
      navigate('/studio/workflows');
    } catch (e) {
      setError(errorText(e));
      setBusy(false);
    }
  }

  return (
    <section>
      <EditorHeader
        name={name}
        kind={summary?.kind}
        entity={summary?.entity}
        mode={summary?.mode}
        state={state}
        managed={managed}
        commands={
          <>
            <Button icon={<OpenIcon />} disabled={busy} onClick={() => setViewing(true)}>
              {t('studio.editor.viewXaml')}
            </Button>
            <Button icon={<ActivateIcon />} disabled={busy || state === 'activated'} onClick={() => void changeState(true)}>
              {t('studio.cmd.activate')}
            </Button>
            <Button icon={<DeactivateIcon />} disabled={busy || state !== 'activated'} onClick={() => void changeState(false)}>
              {t('studio.cmd.deactivate')}
            </Button>
            <Button icon={<DeleteIcon />} disabled={busy} onClick={() => setDeleting(true)}>
              {t('studio.cmd.delete')}
            </Button>
          </>
        }
      />
      {error && (
        <MessageBar intent="error">
          <MessageBarBody>{error}</MessageBarBody>
        </MessageBar>
      )}
      <MessageBar intent="warning" layout="multiline">
        <MessageBarBody>
          <MessageBarTitle>{t('studio.editor.notRepresentable.title')}</MessageBarTitle>
          <p>{message}</p>
          <p>{t('studio.editor.notRepresentable.hint')}</p>
        </MessageBarBody>
      </MessageBar>
      {viewing && <XamlDialog api={api} id={id} name={name} onClose={() => setViewing(false)} />}
      <ConfirmDialog
        open={deleting}
        title={t('studio.delete.title')}
        body={t('studio.delete.body', { name })}
        confirmLabel={t('studio.delete.confirm')}
        cancelLabel={t('common.cancel')}
        onCancel={() => setDeleting(false)}
        onConfirm={() => void remove()}
      />
    </section>
  );
}
