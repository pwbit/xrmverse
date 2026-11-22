import { Button, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, Input, makeStyles, Text, Textarea, tokens } from '@fluentui/react-components';
import { useEffect, useRef, useState } from 'react';
import { t } from '../../../../shell/i18n/t';
import { AttachmentIcon, DismissIcon } from '../../../../shell/icons';
import { fileSizeText } from './TimelineItemRow';

const useStyles = makeStyles({
  entry: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalXS },
  buttons: { display: 'flex', alignItems: 'center', gap: tokens.spacingHorizontalS },
  file: { display: 'flex', alignItems: 'center', gap: tokens.spacingHorizontalXS },
  hidden: { display: 'none' },
});

export interface NoteEntryProps {
  /** The entry shows its title, text and attachment fields; closed, it is the one-line field. */
  open: boolean;
  /** Bumped to move the focus to the entry (the + menu's Note). */
  focusRequest: number;
  onOpen: () => void;
  onClose: () => void;
  /** The attached file (chosen or dropped); the control checks its size. */
  file?: File;
  onPickFile: (file: File) => void;
  onRemoveFile: () => void;
  /** Saves the note; resolves true when it was created (the entry then closes and clears). */
  onSave: (note: { subject: string; text: string }) => Promise<boolean>;
}

/** The quick note entry (spec §5.6): a one-line field that opens into a title, a text and an attachment button. */
export function NoteEntry({ open, focusRequest, onOpen, onClose, file, onPickFile, onRemoveFile, onSave }: NoteEntryProps) {
  const styles = useStyles();
  const [subject, setSubject] = useState('');
  const [text, setText] = useState('');
  const [saving, setSaving] = useState(false);
  const oneLine = useRef<HTMLInputElement>(null);
  const subjectField = useRef<HTMLInputElement>(null);
  const picker = useRef<HTMLInputElement>(null);

  // The + menu asks for the entry: the field that is shown takes the focus.
  const handled = useRef(0);
  useEffect(() => {
    if (focusRequest === handled.current) return;
    handled.current = focusRequest;
    (open ? subjectField.current : oneLine.current)?.focus();
  }, [focusRequest, open]);

  if (!open) {
    return <Input ref={oneLine} size="small" placeholder={t('record.timeline.note.placeholder')} aria-label={t('record.timeline.note.placeholder')} onFocus={onOpen} onClick={onOpen} />;
  }

  const close = () => {
    setSubject('');
    setText('');
    onClose();
  };
  const save = async () => {
    setSaving(true);
    try {
      if (await onSave({ subject: subject.trim(), text })) close();
    } finally {
      setSaving(false);
    }
  };
  return (
    <div className={styles.entry} role="group" aria-label={t('record.timeline.note.entry')}>
      <Input ref={subjectField} size="small" autoFocus placeholder={t('record.timeline.note.subject')} aria-label={t('record.timeline.note.subject')} value={subject} onChange={(_, d) => setSubject(d.value)} />
      <Textarea size="small" resize="vertical" placeholder={t('record.timeline.note.text')} aria-label={t('record.timeline.note.text')} value={text} onChange={(_, d) => setText(d.value)} />
      {file && (
        <Text size={200} className={styles.file}>
          <AttachmentIcon aria-hidden />
          {`${file.name} (${fileSizeText(file.size)})`}
          <Button appearance="transparent" size="small" icon={<DismissIcon />} aria-label={t('record.timeline.note.removeFile')} onClick={onRemoveFile} />
        </Text>
      )}
      <div className={styles.buttons}>
        <input
          ref={picker}
          type="file"
          className={styles.hidden}
          aria-label={t('record.timeline.note.fileInput')}
          onChange={(e) => {
            const picked = e.target.files?.[0];
            e.target.value = '';
            if (picked) onPickFile(picked);
          }}
        />
        <Button size="small" appearance="subtle" icon={<AttachmentIcon />} onClick={() => picker.current?.click()}>
          {t('record.timeline.note.attach')}
        </Button>
        <Button size="small" appearance="primary" disabled={saving || (!subject.trim() && !text && !file)} onClick={() => void save()}>
          {t('record.cmd.save')}
        </Button>
        <Button size="small" disabled={saving} onClick={close}>
          {t('common.cancel')}
        </Button>
      </div>
    </div>
  );
}

export interface NoteEditDialogProps {
  note: { subject: string; text: string };
  onSave: (note: { subject: string; text: string }) => void;
  onClose: () => void;
}

/** Edit of a note's title and text (the attachment stays). */
export function NoteEditDialog({ note, onSave, onClose }: NoteEditDialogProps) {
  const [subject, setSubject] = useState(note.subject);
  const [text, setText] = useState(note.text);
  return (
    <Dialog open onOpenChange={(_, d) => !d.open && onClose()}>
      <DialogSurface>
        <DialogBody>
          <DialogTitle>{t('record.timeline.note.editTitle')}</DialogTitle>
          <DialogContent>
            <Input autoFocus placeholder={t('record.timeline.note.subject')} aria-label={t('record.timeline.note.subject')} value={subject} onChange={(_, d) => setSubject(d.value)} />
            <Textarea resize="vertical" placeholder={t('record.timeline.note.text')} aria-label={t('record.timeline.note.text')} value={text} onChange={(_, d) => setText(d.value)} />
          </DialogContent>
          <DialogActions>
            <Button appearance="primary" onClick={() => onSave({ subject: subject.trim(), text })}>
              {t('record.cmd.save')}
            </Button>
            <Button onClick={onClose}>{t('common.cancel')}</Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}
