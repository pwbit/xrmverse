import { Toast, ToastBody, ToastTitle, Toaster, useId, useToastController } from '@fluentui/react-components';
import { createContext, useContext, useMemo, type ReactNode } from 'react';
import { XrmError } from '../api/errors';

export interface Notify {
  /** XrmError: shows its Dataverse code and server message; anything else: its message. */
  error(e: unknown): void;
  success(msg: string): void;
}

const NotifyContext = createContext<Notify | null>(null);

export function useNotify(): Notify {
  const notify = useContext(NotifyContext);
  if (!notify) throw new Error('useNotify must be used inside <NotificationsProvider>');
  return notify;
}

export function NotificationsProvider({ children }: { children: ReactNode }) {
  const toasterId = useId('toaster');
  const { dispatchToast } = useToastController(toasterId);
  const notify = useMemo<Notify>(
    () => ({
      error(e) {
        const code = e instanceof XrmError ? e.code : undefined;
        const message = e instanceof XrmError ? e.serverMessage : e instanceof Error ? e.message : String(e);
        dispatchToast(
          <Toast>
            <ToastTitle>{code ?? message}</ToastTitle>
            {code && <ToastBody>{message}</ToastBody>}
          </Toast>,
          { intent: 'error', timeout: 8000 },
        );
      },
      success(msg) {
        dispatchToast(
          <Toast>
            <ToastTitle>{msg}</ToastTitle>
          </Toast>,
          { intent: 'success' },
        );
      },
    }),
    [dispatchToast],
  );
  return (
    <NotifyContext.Provider value={notify}>
      {children}
      <Toaster toasterId={toasterId} />
    </NotifyContext.Provider>
  );
}
