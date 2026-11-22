import { Button, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle } from '@fluentui/react-components';

export interface ConfirmDialogProps {
  open: boolean;
  title: string;
  body: string;
  confirmLabel: string;
  cancelLabel: string;
  onConfirm: () => void;
  onCancel: () => void;
}

/** The confirmation every destructive action goes through; nothing is sent before `onConfirm`. */
export function ConfirmDialog(p: ConfirmDialogProps) {
  return (
    <Dialog open={p.open} onOpenChange={(_, d) => !d.open && p.onCancel()}>
      <DialogSurface>
        <DialogBody>
          <DialogTitle>{p.title}</DialogTitle>
          <DialogContent>{p.body}</DialogContent>
          <DialogActions>
            <Button appearance="secondary" onClick={p.onCancel}>
              {p.cancelLabel}
            </Button>
            <Button appearance="primary" onClick={p.onConfirm}>
              {p.confirmLabel}
            </Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}
