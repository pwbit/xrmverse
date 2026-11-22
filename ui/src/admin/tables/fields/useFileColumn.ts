import { useEffect, useRef, useState } from 'react';
import { useAuth } from '../../../shell/auth/AuthProvider';
import type { en } from '../../../shell/i18n/en';
import { t } from '../../../shell/i18n/t';
import { useNotify } from '../../../shell/notify/Notifications';
import { download, remove, saveFile, upload } from '../fileApi';
import type { AttributeMeta } from '../metadata';
import type { FieldContext } from './fieldProps';

export interface FileColumn {
  /** The column's properties (`properties`) as last read: from the form's record, then from each refresh. */
  values: Record<string, unknown>;
  /** An upload, download or delete is running. */
  busy: boolean;
  /** The running upload: its file name and how much was sent. */
  progress?: { name: string; percent: number };
  upload: (file: File) => Promise<void>;
  download: (full?: boolean) => Promise<void>;
  remove: () => Promise<void>;
}

interface Messages {
  uploaded: keyof typeof en;
  deleted: keyof typeof en;
}

/**
 * The upload, download and delete of one file or image column of the record in `context`, each sent at once (not with the
 * form's Save). After a change the column's own `properties` are read again, so the rest of the form keeps what the user
 * typed. Every failure is shown with the server's code.
 */
export function useFileColumn(attr: AttributeMeta, context: FieldContext, properties: string[], messages: Messages): FileColumn {
  const { api } = useAuth();
  const notify = useNotify();
  const record = context.record;
  const select = properties.join(',');
  const pick = (data: Record<string, unknown> | undefined) => Object.fromEntries(select.split(',').map((p) => [p, data?.[p] ?? null]));
  const [values, setValues] = useState<Record<string, unknown>>(() => pick(record?.data));
  const [busy, setBusy] = useState(false);
  const [progress, setProgress] = useState<FileColumn['progress']>();

  // An upload still running when the editor goes away (the form is closed) is aborted: no further chunk is sent.
  const mounted = useRef(true);
  const running = useRef<AbortController | undefined>(undefined);
  useEffect(() => {
    mounted.current = true;
    return () => {
      mounted.current = false;
      running.current?.abort();
    };
  }, []);

  // The form's record (loaded again after a save) replaces what was read.
  useEffect(() => {
    setValues(pick(record?.data));
    // pick derives from select only.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [record, select]);

  async function run(work: () => Promise<void>) {
    if (!api || !record) return;
    setBusy(true);
    try {
      await work();
    } catch (e) {
      // The editor's own abort on unmount is not a failure to show.
      if (mounted.current) notify.error(e);
    } finally {
      if (mounted.current) {
        setBusy(false);
        setProgress(undefined);
      }
    }
  }

  async function refresh() {
    if (!api || !record) return;
    const read = await api.get<Record<string, unknown>>(`${record.set}(${record.id})?$select=${select}`);
    setValues(pick(read));
  }

  return {
    values,
    busy,
    progress,
    upload: (file) =>
      run(async () => {
        setProgress({ name: file.name, percent: 0 });
        const controller = new AbortController();
        running.current = controller;
        try {
          await upload(
            api!,
            record!.set,
            record!.id,
            attr.logicalName,
            file,
            (sent, total) => setProgress({ name: file.name, percent: total === 0 ? 100 : Math.floor((sent * 100) / total) }),
            controller.signal,
          );
        } finally {
          if (running.current === controller) running.current = undefined;
        }
        notify.success(t(messages.uploaded, { name: file.name }));
        await refresh();
      }),
    download: (full) => run(async () => saveFile(await download(api!, record!.set, record!.id, attr.logicalName, { full }))),
    remove: () =>
      run(async () => {
        await remove(api!, record!.set, record!.id, attr.logicalName);
        notify.success(t(messages.deleted, { label: attr.displayName }));
        await refresh();
      }),
  };
}
