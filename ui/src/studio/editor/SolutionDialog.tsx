import { Button, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, Dropdown, Field, MessageBar, MessageBarBody, Option, Spinner } from '@fluentui/react-components';
import { useEffect, useState } from 'react';
import type { XrmApi } from '../../shell/api/client';
import { errorText } from '../../shell/api/errors';
import { t } from '../../shell/i18n/t';

export interface SolutionChoice {
  uniquename: string;
  friendlyname: string;
}

/** The solutions a process can be added to: the visible unmanaged ones. */
export const SOLUTIONS_QUERY = 'solutions?$select=uniquename,friendlyname&$filter=ismanaged eq false and isvisible eq true&$orderby=friendlyname';

/** Add to solution: an unmanaged solution, which the next save passes as `solution=`. */
export function SolutionDialog({ api, current, onChoose, onCancel }: { api: XrmApi; current?: string; onChoose(solution: SolutionChoice): void; onCancel(): void }) {
  const [solutions, setSolutions] = useState<SolutionChoice[]>();
  const [error, setError] = useState<string>();
  const [chosen, setChosen] = useState<string | undefined>(current);

  useEffect(() => {
    let live = true;
    api.get<{ value: SolutionChoice[] }>(SOLUTIONS_QUERY).then(
      (body) => live && setSolutions(body.value ?? []),
      (e: unknown) => live && setError(errorText(e)),
    );
    return () => {
      live = false;
    };
  }, [api]);

  const label = (s: SolutionChoice) => s.friendlyname || s.uniquename;
  const selected = solutions?.find((s) => s.uniquename === chosen);

  return (
    <Dialog open onOpenChange={(_, d) => !d.open && onCancel()}>
      <DialogSurface>
        <DialogBody>
          <DialogTitle>{t('studio.editor.solution.title')}</DialogTitle>
          <DialogContent>
            {error && (
              <MessageBar intent="error">
                <MessageBarBody>{t('studio.editor.solution.failed', { detail: error })}</MessageBarBody>
              </MessageBar>
            )}
            {!solutions && !error && <Spinner label={t('studio.editor.solution.loading')} />}
            {solutions && solutions.length === 0 && <p>{t('studio.editor.solution.none')}</p>}
            {solutions && solutions.length > 0 && (
              <Field label={t('studio.editor.solution.label')} hint={t('studio.editor.solution.hint')}>
                <Dropdown
                  value={selected ? label(selected) : ''}
                  selectedOptions={chosen ? [chosen] : []}
                  onOptionSelect={(_, d) => setChosen(d.optionValue)}
                >
                  {solutions.map((s) => (
                    <Option key={s.uniquename} value={s.uniquename}>
                      {label(s)}
                    </Option>
                  ))}
                </Dropdown>
              </Field>
            )}
          </DialogContent>
          <DialogActions>
            <Button appearance="secondary" onClick={onCancel}>
              {t('common.cancel')}
            </Button>
            <Button appearance="primary" disabled={!selected} onClick={() => selected && onChoose(selected)}>
              {t('studio.editor.solution.choose')}
            </Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}
