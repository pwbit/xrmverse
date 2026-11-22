import { Button, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, makeStyles, MessageBar, MessageBarBody, Spinner, tokens } from '@fluentui/react-components';
import { useEffect, useState } from 'react';
import type { XrmApi } from '../../shell/api/client';
import { errorText } from '../../shell/api/errors';
import { t } from '../../shell/i18n/t';
import { exportWorkflow } from '../api/studioApi';

const useStyles = makeStyles({
  surface: { maxWidth: 'min(960px, 95vw)' },
  xaml: {
    margin: 0,
    maxHeight: '60vh',
    overflow: 'auto',
    padding: tokens.spacingHorizontalM,
    fontFamily: tokens.fontFamilyMonospace,
    fontSize: tokens.fontSizeBase200,
    backgroundColor: tokens.colorNeutralBackground2,
    borderRadius: tokens.borderRadiusMedium,
    whiteSpace: 'pre',
  },
});

/** View XAML: the stored XAML of the saved version (`export?format=xaml`), read-only. */
export function XamlDialog({ api, id, name, onClose }: { api: XrmApi; id: string; name: string; onClose(): void }) {
  const styles = useStyles();
  const [xaml, setXaml] = useState<string>();
  const [error, setError] = useState<string>();

  useEffect(() => {
    let live = true;
    exportWorkflow(api, id, 'xaml').then(
      (text) => live && setXaml(text),
      (e: unknown) => live && setError(errorText(e)),
    );
    return () => {
      live = false;
    };
  }, [api, id]);

  return (
    <Dialog open onOpenChange={(_, d) => !d.open && onClose()}>
      <DialogSurface className={styles.surface}>
        <DialogBody>
          <DialogTitle>{t('studio.editor.xaml.title', { name })}</DialogTitle>
          <DialogContent>
            {error && (
              <MessageBar intent="error">
                <MessageBarBody>{t('studio.editor.xaml.failed', { detail: error })}</MessageBarBody>
              </MessageBar>
            )}
            {xaml === undefined && !error && <Spinner label={t('studio.editor.xaml.loading')} />}
            {xaml !== undefined && (
              <pre className={styles.xaml} tabIndex={0} aria-label={t('studio.editor.xaml.title', { name })}>
                {xaml}
              </pre>
            )}
          </DialogContent>
          <DialogActions>
            <Button appearance="primary" onClick={onClose}>
              {t('studio.editor.close')}
            </Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}
