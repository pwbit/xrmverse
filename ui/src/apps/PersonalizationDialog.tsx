import {
  Button,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  Field,
  MessageBar,
  MessageBarBody,
  Select,
  makeStyles,
  tokens,
} from '@fluentui/react-components';
import { useContext, useState } from 'react';
import { ConfirmDialog } from '../admin/common/ConfirmDialog';
import { XrmHostContext } from '../client-api/xrm';
import { useAuth } from '../shell/auth/AuthProvider';
import { t } from '../shell/i18n/t';
import { formatWithPattern, patternText } from './dateFormat';
import { useReloadUserSettings, useUserSettings, type DateFormats } from './userSettings';

const useStyles = makeStyles({ content: { display: 'flex', flexDirection: 'column', rowGap: tokens.spacingVerticalM } });

const LOOKS = [
  ['', 'personalization.look.default'],
  ['classic', 'personalization.look.classic'],
  ['modern', 'personalization.look.modern'],
] as const;
const SCHEMES = [
  ['0', 'personalization.scheme.system'],
  ['1', 'personalization.scheme.light'],
  ['2', 'personalization.scheme.dark'],
] as const;

/** The date patterns offered (ruling F3); `/` is the separator chosen beside them. */
const DATE_PATTERNS = ['M/d/yyyy', 'MM/dd/yyyy', 'd/M/yyyy', 'dd/MM/yyyy', 'yyyy/MM/dd'];
const DATE_SEPARATORS = ['/', '-', '.'];
const TIME_PATTERNS = ['h:mm tt', 'hh:mm tt', 'H:mm', 'HH:mm'];

/** The offered values, with the stored one kept when it is not among them (set through the API). */
const withCurrent = (values: string[], current: string) => (values.includes(current) ? values : [...values, current]);

/** An option's text: today at 14:30 in the pattern, then the pattern itself. */
function sample(pattern: string, formats: DateFormats): string {
  const now = new Date();
  const parts = { year: now.getFullYear(), month: now.getMonth() + 1, day: now.getDate(), hours: 14, minutes: 30 };
  return `${formatWithPattern(parts, pattern, formats)} (${patternText(pattern, formats)})`;
}

/** Whether the form open in the shell has unsaved changes (the reload that follows a save would discard them). */
function formIsDirty(host: { currentForm(): { data: { entity: Record<string, unknown> } } | null } | null): boolean {
  const isDirty = host?.currentForm()?.data.entity.getIsDirty;
  return typeof isDirty === 'function' && (isDirty as () => unknown)() === true;
}

/**
 * The user's look and colour scheme (`usersettings.xrmverse_renderer` / `xrmverse_colorscheme`, their own row). Saving reloads the
 * user settings, which makes the shell re-resolve the look and re-open the app; with a form that has unsaved changes the
 * same discard prompt as a navigation asks first, before anything is sent.
 */
export function PersonalizationDialog({ open, onClose }: { open: boolean; onClose: () => void }) {
  const styles = useStyles();
  const { api } = useAuth();
  const settings = useUserSettings();
  const reload = useReloadUserSettings();
  const host = useContext(XrmHostContext);
  const [renderer, setRenderer] = useState(settings.renderer ?? '');
  const [scheme, setScheme] = useState(String(settings.colorScheme));
  const [dateFormat, setDateFormat] = useState(settings.formats.dateFormat);
  const [dateSeparator, setDateSeparator] = useState(settings.formats.dateSeparator);
  const [timeFormat, setTimeFormat] = useState(settings.formats.timeFormat);
  const formats: DateFormats = { ...settings.formats, dateFormat, dateSeparator, timeFormat };
  const [error, setError] = useState<string>();
  const [busy, setBusy] = useState(false);
  const [confirming, setConfirming] = useState(false);

  const save = async () => {
    if (!api) return;
    setConfirming(false);
    setBusy(true);
    setError(undefined);
    try {
      await api.patch(`usersettingscollection(${settings.userId})`, {
        xrmverse_renderer: renderer === '' ? null : renderer,
        xrmverse_colorscheme: Number(scheme),
        dateformatstring: dateFormat,
        dateseparator: dateSeparator,
        timeformatstring: timeFormat,
      });
    } catch (e: unknown) {
      setError(e instanceof Error ? e.message : String(e));
      setBusy(false);
      return;
    }
    setBusy(false);
    onClose();
    reload();
  };

  return (
    <>
      <Dialog open={open && !confirming} onOpenChange={(_, d) => !d.open && onClose()}>
        <DialogSurface>
          <DialogBody>
            <DialogTitle>{t('personalization.title')}</DialogTitle>
            <DialogContent className={styles.content}>
              {error && (
                <MessageBar intent="error">
                  <MessageBarBody>{error}</MessageBarBody>
                </MessageBar>
              )}
              <Field label={t('personalization.look')}>
                <Select value={renderer} onChange={(_, d) => setRenderer(d.value)}>
                  {LOOKS.map(([value, label]) => (
                    <option key={value} value={value}>
                      {t(label)}
                    </option>
                  ))}
                </Select>
              </Field>
              <Field label={t('personalization.scheme')}>
                <Select value={scheme} onChange={(_, d) => setScheme(d.value)}>
                  {SCHEMES.map(([value, label]) => (
                    <option key={value} value={value}>
                      {t(label)}
                    </option>
                  ))}
                </Select>
              </Field>
              <Field label={t('personalization.dateFormat')}>
                <Select value={dateFormat} onChange={(_, d) => setDateFormat(d.value)}>
                  {withCurrent(DATE_PATTERNS, settings.formats.dateFormat).map((p) => (
                    <option key={p} value={p}>
                      {sample(p, formats)}
                    </option>
                  ))}
                </Select>
              </Field>
              <Field label={t('personalization.dateSeparator')}>
                <Select value={dateSeparator} onChange={(_, d) => setDateSeparator(d.value)}>
                  {withCurrent(DATE_SEPARATORS, settings.formats.dateSeparator).map((sep) => (
                    <option key={sep} value={sep}>
                      {sep}
                    </option>
                  ))}
                </Select>
              </Field>
              <Field label={t('personalization.timeFormat')}>
                <Select value={timeFormat} onChange={(_, d) => setTimeFormat(d.value)}>
                  {withCurrent(TIME_PATTERNS, settings.formats.timeFormat).map((p) => (
                    <option key={p} value={p}>
                      {sample(p, formats)}
                    </option>
                  ))}
                </Select>
              </Field>
            </DialogContent>
            <DialogActions>
              <Button appearance="secondary" onClick={onClose}>
                {t('common.cancel')}
              </Button>
              <Button appearance="primary" disabled={busy} onClick={() => (formIsDirty(host) ? setConfirming(true) : void save())}>
                {t('personalization.save')}
              </Button>
            </DialogActions>
          </DialogBody>
        </DialogSurface>
      </Dialog>
      <ConfirmDialog
        open={open && confirming}
        title={t('record.discard.title')}
        body={t('record.discard.body')}
        confirmLabel={t('record.discard.confirm')}
        cancelLabel={t('common.cancel')}
        onCancel={() => setConfirming(false)}
        onConfirm={() => void save()}
      />
    </>
  );
}
