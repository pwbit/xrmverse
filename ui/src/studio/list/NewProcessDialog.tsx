import {
  Button,
  Checkbox,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  Dropdown,
  Field,
  Input,
  makeStyles,
  Option,
  Radio,
  RadioGroup,
  tokens,
} from '@fluentui/react-components';
import { useState } from 'react';
import { stringify } from 'yaml';
import { t } from '../../shell/i18n/t';
import type { ProcessKind } from '../api/studioApi';

export interface NewProcess {
  kind: ProcessKind;
  name: string;
  /** The primary table; `none` for a global action. */
  entity: string;
  /** A workflow's mode. */
  mode?: 'realtime' | 'background';
  /** An action's message name (with the publisher prefix). */
  message?: string;
}

/** The name's limit of the format (`name`: at most 100 characters). */
const MAX_NAME = 100;

/**
 * The smallest document of each kind that the format accepts (ruling W4; docs/workflow-authoring.md "The format"): `kind`,
 * `name`, `entity`, a workflow's `mode`, an action's `message`, and no steps.
 */
export function newProcessYaml(p: NewProcess): string {
  const doc: Record<string, unknown> = { kind: p.kind, name: p.name, entity: p.entity };
  if (p.kind === 'workflow') doc.mode = p.mode ?? 'background';
  if (p.kind === 'action') doc.message = p.message ?? '';
  doc.steps = [];
  return stringify(doc);
}

const KINDS: ProcessKind[] = ['workflow', 'businessRule', 'action'];

const useStyles = makeStyles({ form: { display: 'flex', flexDirection: 'column', rowGap: tokens.spacingVerticalM } });

/** New process: the kind, the name, the table (none for a global action), a workflow's mode and an action's message. */
export function NewProcessDialog({ onCreate, onCancel }: { onCreate: (process: NewProcess) => void; onCancel: () => void }) {
  const styles = useStyles();
  const [kind, setKind] = useState<ProcessKind>('workflow');
  const [name, setName] = useState('');
  const [table, setTable] = useState('');
  const [global, setGlobal] = useState(false);
  const [mode, setMode] = useState<'realtime' | 'background'>('background');
  const [message, setMessage] = useState('');
  const isGlobal = kind === 'action' && global;
  const ready = name.trim() !== '' && (isGlobal || table.trim() !== '') && (kind !== 'action' || message.trim() !== '');

  function create() {
    if (!ready) return;
    onCreate({
      kind,
      name: name.trim(),
      entity: isGlobal ? 'none' : table.trim(),
      ...(kind === 'workflow' ? { mode } : {}),
      ...(kind === 'action' ? { message: message.trim() } : {}),
    });
  }

  return (
    <Dialog open onOpenChange={(_, d) => !d.open && onCancel()}>
      <DialogSurface>
        <form
          onSubmit={(e) => {
            e.preventDefault();
            create();
          }}
        >
          <DialogBody>
            <DialogTitle>{t('studio.new.title')}</DialogTitle>
            <DialogContent className={styles.form}>
              <Field label={t('studio.new.kind')}>
                <Dropdown
                  value={t(`studio.kind.${kind}`)}
                  selectedOptions={[kind]}
                  onOptionSelect={(_, d) => d.optionValue && setKind(d.optionValue as ProcessKind)}
                >
                  {KINDS.map((k) => (
                    <Option key={k} value={k}>
                      {t(`studio.kind.${k}`)}
                    </Option>
                  ))}
                </Dropdown>
              </Field>
              <Field label={t('studio.new.name')} required>
                <Input value={name} maxLength={MAX_NAME} onChange={(_, d) => setName(d.value)} />
              </Field>
              <Field label={t('studio.new.table')} hint={t('studio.new.tableHint')} required={!isGlobal}>
                <Input value={isGlobal ? '' : table} disabled={isGlobal} onChange={(_, d) => setTable(d.value)} />
              </Field>
              {kind === 'action' && (
                <>
                  <Checkbox label={t('studio.new.global')} checked={global} onChange={(_, d) => setGlobal(d.checked === true)} />
                  <Field label={t('studio.new.message')} hint={t('studio.new.messageHint')} required>
                    <Input value={message} onChange={(_, d) => setMessage(d.value)} />
                  </Field>
                </>
              )}
              {kind === 'workflow' && (
                <Field label={t('studio.new.mode')}>
                  <RadioGroup value={mode} onChange={(_, d) => setMode(d.value as 'realtime' | 'background')}>
                    <Radio value="background" label={t('studio.mode.background')} />
                    <Radio value="realtime" label={t('studio.mode.realtime')} />
                  </RadioGroup>
                </Field>
              )}
            </DialogContent>
            <DialogActions>
              <Button appearance="secondary" onClick={onCancel}>
                {t('studio.new.cancel')}
              </Button>
              <Button appearance="primary" type="submit" disabled={!ready}>
                {t('studio.new.create')}
              </Button>
            </DialogActions>
          </DialogBody>
        </form>
      </DialogSurface>
    </Dialog>
  );
}
