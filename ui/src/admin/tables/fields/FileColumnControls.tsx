import { Button, Label, ProgressBar, Text, useId } from '@fluentui/react-components';
import { useRef, useState, type ReactNode } from 'react';
import { t } from '../../../shell/i18n/t';
import { ConfirmDialog } from '../../common/ConfirmDialog';
import type { FieldProps } from './fieldProps';
import type { FileColumn } from './useFileColumn';

interface Props extends FieldProps {
  column: FileColumn;
  /** The column holds a file or an image. */
  filled: boolean;
  /** What the column holds, or that it is empty. */
  children: ReactNode;
  /** `accept` of the file picker. */
  accept?: string;
  /** Extra actions while the column is filled (an image's full-size download). */
  actions?: ReactNode;
  /** The download of a file column; none for an image (its full image is one of `actions`). */
  downloadable: boolean;
  confirm: { title: string; body: string; label: string };
}

/**
 * What the file and image editors share: a group named by the column, an upload through a file picker, a delete behind a
 * confirmation, and the progress of an upload. On the create form (no record yet) everything is disabled, with the reason.
 */
export function FileColumnControls(p: Props) {
  const labelId = useId('file');
  const input = useRef<HTMLInputElement>(null);
  const [confirming, setConfirming] = useState(false);
  const label = p.attr.displayName;
  const creating = !p.context.record;
  const off = p.disabled || creating || p.column.busy;
  return (
    <div role="group" aria-labelledby={labelId}>
      <Label id={labelId}>{label}</Label>
      <div>{creating ? <Text>{t('field.file.afterCreate')}</Text> : p.children}</div>
      {p.attr.maxSizeInKB !== undefined && <Text size={200}>{t('field.file.maxSize', { size: p.attr.maxSizeInKB })}</Text>}
      <div>
        <input
          ref={input}
          type="file"
          hidden
          accept={p.accept}
          aria-label={t('field.file.choose', { label })}
          disabled={off}
          onChange={(e) => {
            const file = e.target.files?.[0];
            // Cleared, so choosing the same file again uploads it again.
            e.target.value = '';
            if (file) void p.column.upload(file);
          }}
        />
        <Button size="small" disabled={off} aria-label={t('field.file.uploadLabel', { label })} onClick={() => input.current?.click()}>
          {t('field.file.upload')}
        </Button>
        {!creating && p.filled && (
          <>
            {p.downloadable && (
              <Button size="small" disabled={off} aria-label={t('field.file.downloadLabel', { label })} onClick={() => void p.column.download()}>
                {t('field.file.download')}
              </Button>
            )}
            {p.actions}
            <Button size="small" disabled={off} aria-label={t('field.file.deleteLabel', { label })} onClick={() => setConfirming(true)}>
              {t('field.file.delete')}
            </Button>
          </>
        )}
      </div>
      {p.column.progress && (
        <div>
          <ProgressBar value={p.column.progress.percent / 100} />
          <Text size={200}>{t('field.file.uploading', p.column.progress)}</Text>
        </div>
      )}
      {p.error && <Text role="alert">{p.error}</Text>}
      <ConfirmDialog
        open={confirming}
        title={p.confirm.title}
        body={p.confirm.body}
        confirmLabel={p.confirm.label}
        cancelLabel={t('common.cancel')}
        onConfirm={() => {
          setConfirming(false);
          void p.column.remove();
        }}
        onCancel={() => setConfirming(false)}
      />
    </div>
  );
}
