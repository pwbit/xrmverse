import {
  Button,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  Field,
  Input,
  Menu,
  MenuItem,
  MenuList,
  MenuPopover,
  MenuTrigger,
  MessageBar,
  MessageBarBody,
  ToolbarButton,
} from '@fluentui/react-components';
import { useState } from 'react';
import type { ViewChoice } from '../renderers/classic/ViewSelector';
import type { XrmApi } from '../shell/api/client';
import { t } from '../shell/i18n/t';
import { SaveIcon as ViewActionsIcon } from '../shell/icons';

/** `userquery.querytype` of a personal main view. */
const MAIN_QUERY_TYPE = 0;

/** Creates a personal view (`userquery`) of `table`; returns its `userqueryid` (lowercase). */
export async function saveAsView(api: XrmApi, table: string, name: string, fetchXml: string, layoutXml: string): Promise<string> {
  const { id } = await api.post('userqueries', {
    name,
    returnedtypecode: table,
    querytype: MAIN_QUERY_TYPE,
    fetchxml: fetchXml,
    layoutxml: layoutXml,
  });
  if (!id) throw new Error('The server did not return the id of the new view.');
  return id.toLowerCase();
}

/** Saves the query and the layout of a personal view. */
export async function saveView(api: XrmApi, id: string, fetchXml: string, layoutXml: string): Promise<void> {
  await api.patch(`userqueries(${id})`, { fetchxml: fetchXml, layoutxml: layoutXml });
}

export async function renameView(api: XrmApi, id: string, name: string): Promise<void> {
  await api.patch(`userqueries(${id})`, { name });
}

export async function deleteView(api: XrmApi, id: string): Promise<void> {
  await api.delete(`userqueries(${id})`);
}

const errorText = (e: unknown) => (e instanceof Error ? e.message : String(e));

export interface ViewCommandsProps {
  api: XrmApi;
  /** The listed table's logical name. */
  table: string;
  selected: ViewChoice;
  userId: string;
  /** The query to save: the selected view with the current sort, without paging. */
  fetchXml: string;
  /** Called with the id of a view just created. */
  onCreated: (id: string) => void;
  /** Called after the selected view was saved or renamed. */
  onChanged: () => void;
  /** Called after the selected view was deleted. */
  onDeleted: () => void;
}

type Open = 'saveAs' | 'rename' | 'delete' | undefined;

/**
 * The view commands (#101), an icon at the end of the view's command bar: "Save as new view" on any view; "Save view", "Rename view" and "Delete view" only on a
 * personal view the user owns. Sharing a view is not offered.
 */
export function ViewCommands({ api, table, selected, userId, fetchXml, onCreated, onChanged, onDeleted }: ViewCommandsProps) {
  const [open, setOpen] = useState<Open>();
  const [name, setName] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string>();
  const own = selected.personal && selected.view.ownerId === userId.toLowerCase();
  const layoutXml = selected.view.layoutXml ?? '';

  const run = async (action: () => Promise<void>) => {
    setBusy(true);
    setError(undefined);
    try {
      await action();
      setOpen(undefined);
    } catch (e) {
      setError(errorText(e));
    } finally {
      setBusy(false);
    }
  };
  const show = (which: Open, initial = '') => {
    setName(initial);
    setError(undefined);
    setOpen(which);
  };
  const failure = error && (
    <MessageBar intent="error">
      <MessageBarBody>{t('apps.views.saveError', { detail: error })}</MessageBarBody>
    </MessageBar>
  );
  const nameDialog = (title: string, submit: () => Promise<void>) => (
    <Dialog open onOpenChange={(_e, data) => !data.open && setOpen(undefined)}>
      <DialogSurface>
        <form
          onSubmit={(e) => {
            e.preventDefault();
            if (name.trim()) void run(submit);
          }}
        >
          <DialogBody>
            <DialogTitle>{title}</DialogTitle>
            <DialogContent>
              <Field label={t('apps.views.viewName')} required>
                <Input value={name} onChange={(_e, data) => setName(data.value)} />
              </Field>
              {failure}
            </DialogContent>
            <DialogActions>
              <Button type="submit" appearance="primary" disabled={busy || !name.trim()}>
                {t('apps.views.saveButton')}
              </Button>
              <Button onClick={() => setOpen(undefined)}>{t('apps.views.cancel')}</Button>
            </DialogActions>
          </DialogBody>
        </form>
      </DialogSurface>
    </Dialog>
  );

  return (
    <>
      <Menu>
        <MenuTrigger disableButtonEnhancement>
          <ToolbarButton icon={<ViewActionsIcon />} aria-label={t('apps.views.commands')} title={t('apps.views.commands')} />
        </MenuTrigger>
        <MenuPopover>
          <MenuList>
            <MenuItem onClick={() => show('saveAs')}>{t('apps.views.saveAs')}</MenuItem>
            {own && (
              <MenuItem disabled={busy} onClick={() => void run(async () => {
                    await saveView(api, selected.view.id, fetchXml, layoutXml);
                    onChanged();
                  })}>
                {t('apps.views.save')}
              </MenuItem>
            )}
            {own && <MenuItem onClick={() => show('rename', selected.view.name)}>{t('apps.views.rename')}</MenuItem>}
            {own && <MenuItem onClick={() => show('delete')}>{t('apps.views.delete')}</MenuItem>}
          </MenuList>
        </MenuPopover>
      </Menu>
      {!open && failure}
      {open === 'saveAs' &&
        nameDialog(t('apps.views.saveAs'), async () => onCreated(await saveAsView(api, table, name.trim(), fetchXml, layoutXml)))}
      {open === 'rename' && nameDialog(t('apps.views.rename'), async () => {
          await renameView(api, selected.view.id, name.trim());
          onChanged();
        })}
      {open === 'delete' && (
        <Dialog open onOpenChange={(_e, data) => !data.open && setOpen(undefined)}>
          <DialogSurface role="alertdialog">
            <DialogBody>
              <DialogTitle>{t('apps.views.delete')}</DialogTitle>
              <DialogContent>
                {t('apps.views.deleteConfirm', { name: selected.view.name })}
                {failure}
              </DialogContent>
              <DialogActions>
                <Button appearance="primary" disabled={busy} onClick={() => void run(async () => {
                    await deleteView(api, selected.view.id);
                    onDeleted();
                  })}>
                  {t('apps.views.deleteButton')}
                </Button>
                <Button onClick={() => setOpen(undefined)}>{t('apps.views.cancel')}</Button>
              </DialogActions>
            </DialogBody>
          </DialogSurface>
        </Dialog>
      )}
    </>
  );
}
