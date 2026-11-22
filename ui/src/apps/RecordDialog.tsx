import { Dialog, DialogBody, DialogContent, DialogSurface, DialogTitle, makeStyles, mergeClasses, MessageBar, MessageBarBody } from '@fluentui/react-components';
import { useContext, useEffect, useState } from 'react';
import { createMemoryRouter, RouterProvider, UNSAFE_LocationContext, UNSAFE_RouteContext, useLocation } from 'react-router-dom';
import { dialogSurface, type RecordDialog as RecordDialogOptions, type SavedRecord } from '../client-api/DialogHost';
import { XrmHostContext } from '../client-api/xrm';
import type { AppModel } from '../model/app';
import type { XrmApi } from '../shell/api/client';
import { useAuth } from '../shell/auth/AuthProvider';
import { t } from '../shell/i18n/t';
import { mainAspxUrl, parseMainAspx } from './mainAspx';
import { RecordPage } from './RecordPage';

const useStyles = makeStyles({
  surface: { display: 'flex', flexDirection: 'column', maxWidth: '100vw', maxHeight: '100vh', overflowY: 'auto' },
  side: { marginRight: 0, marginLeft: 'auto', height: '100vh' },
});

/** The path and query of `url`, for a memory router entry that carries a state. */
function splitUrl(url: string): { pathname: string; search: string } {
  const at = url.indexOf('?');
  return at < 0 ? { pathname: url, search: '' } : { pathname: url.slice(0, at), search: url.slice(at) };
}

const quote = (v: string) => v.replace(/'/g, "''");

/** The primary name of a saved record, for `savedEntityReference`; empty when it cannot be read. */
async function primaryName(api: XrmApi, etn: string, id: string): Promise<string> {
  try {
    const table = await api.get<{ EntitySetName: string; PrimaryNameAttribute?: string | null }>(
      `EntityDefinitions(LogicalName='${quote(etn)}')?$select=EntitySetName,PrimaryNameAttribute`,
    );
    if (!table.PrimaryNameAttribute) return '';
    const row = await api.get<Record<string, unknown>>(`${table.EntitySetName}(${id})?$select=${table.PrimaryNameAttribute}`);
    const name = row[table.PrimaryNameAttribute];
    return typeof name === 'string' ? name : '';
  } catch {
    return '';
  }
}

interface Session {
  /** The record saved last in the dialog, if any. */
  saved?: string;
  close: () => void;
}

/**
 * The record page of the dialog's own (in-memory) history: the form navigates inside the dialog, and leaving the record (Save
 * & Close, Close, the list) closes the dialog. The unsaved-changes prompt applies as on the page.
 */
function DialogRoute({ app, session }: { app: AppModel; session: Session }) {
  const { search } = useLocation();
  const state = parseMainAspx(search);
  const leaving = state.pageType !== 'entityrecord' || !state.etn;
  useEffect(() => {
    if (leaving) session.close();
  }, [leaving, session]);
  if (leaving) return null;
  return (
    <RecordPage
      key={state.etn}
      app={app}
      etn={state.etn!}
      id={state.id}
      formId={state.formId}
      parameters={state.extraqs}
      onSaved={(id) => void (session.saved = id)}
    />
  );
}

/**
 * A record form in a dialog (`Xrm.Navigation.navigateTo` with `target: 2`, #133). It closes with the record saved last: the one
 * a create form created, or the one it opened once saved.
 */
export function RecordDialog({ app, options, done }: { app: AppModel; options: RecordDialogOptions; done: (saved: SavedRecord | undefined) => void }) {
  const styles = useStyles();
  const { api } = useAuth();
  const xrmHost = useContext(XrmHostContext);
  // Xrm.Page is the dialog's form while it is open, and the page's form again once it closes.
  const [previousForm] = useState(() => xrmHost?.currentForm() ?? null);
  const etn = parseMainAspx(new URL(options.url, 'http://x').search).etn ?? '';
  const [session] = useState<Session>(() => {
    let closed = false;
    const s: Session = {
      close: () => {
        if (closed) return;
        closed = true;
        xrmHost?.setCurrentForm(previousForm);
        const id = s.saved;
        if (!id) return done(undefined);
        if (!api) return done({ entityType: etn, id, name: '' });
        void primaryName(api, etn, id).then((name) => done({ entityType: etn, id, name }));
      },
    };
    return s;
  });
  const [router] = useState(() =>
    createMemoryRouter([{ path: '*', element: <DialogRoute app={app} session={session} /> }], {
      initialEntries: [options.state === undefined ? options.url : { ...splitUrl(options.url), state: options.state }],
    }),
  );
  useEffect(() => () => router.dispose(), [router]);
  const { side, style } = dialogSurface(options);
  // Leaving through the dialog's router lets the form's unsaved-changes prompt run first.
  const leave = () => void router.navigate(mainAspxUrl({ appId: app.id, pageType: 'entitylist', etn }));

  if (!etn) {
    return (
      <Dialog open modalType="modal" onOpenChange={(_, d) => !d.open && session.close()}>
        <DialogSurface aria-label={options.title ?? ''}>
          <DialogBody>
            <DialogTitle>{options.title ?? ''}</DialogTitle>
            <DialogContent>
              <MessageBar intent="warning">
                <MessageBarBody>{t('record.noTable')}</MessageBarBody>
              </MessageBar>
            </DialogContent>
          </DialogBody>
        </DialogSurface>
      </Dialog>
    );
  }

  return (
    // A side pane is non-modal: the page beside it stays visible, and Fluent's modal focus trap (Tabster) could hide the pane
    // itself when its form mounts nested drawers and dialogs. Escape and the form's X close it.
    <Dialog open modalType={side ? 'non-modal' : 'modal'} onOpenChange={(_, d) => !d.open && leave()}>
      <DialogSurface className={mergeClasses(styles.surface, side && styles.side)} style={style} aria-label={options.title ?? etn}>
        {/* The form's own command bar closes it (the X at its end), as does Escape. */}
        {/* The dialog's router is its own: the app's router context does not reach in. */}
        <UNSAFE_LocationContext.Provider value={null as never}>
          <UNSAFE_RouteContext.Provider value={{ outlet: null, matches: [], isDataRoute: false }}>
            <RouterProvider router={router} future={{ v7_startTransition: true }} />
          </UNSAFE_RouteContext.Provider>
        </UNSAFE_LocationContext.Provider>
      </DialogSurface>
    </Dialog>
  );
}
