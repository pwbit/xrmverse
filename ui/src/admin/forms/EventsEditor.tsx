import { Button, Field, makeStyles, MessageBar, MessageBarBody, Select, tokens } from '@fluentui/react-components';
import { useId, useState } from 'react';
import { t } from '../../shell/i18n/t';
import { DismissIcon, NewIcon, NextPageIcon, PreviousPageIcon } from '../../shell/icons';
import { useLoad } from '../tables/useLoad';
import { lower } from './formCells';
import { listWebResources } from './formControlsApi';
import { orphanChangeEvents } from './formDesign';
import { addLibrary, dropEvent, moveLibrary, otherEvents, removeLibrary } from './formEvents';
import { useRefocus, type FormDesignCardProps } from './formDesignerSlots';
import { HandlerList } from './HandlerList';
import { LoadFailed } from './LoadFailed';

const useStyles = makeStyles({
  card: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalM },
  heading: { margin: 0, fontSize: tokens.fontSizeBase300, fontWeight: tokens.fontWeightSemibold },
  libraries: { listStyle: 'none', margin: 0, padding: 0, display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalXS },
  library: { display: 'flex', alignItems: 'center', gap: tokens.spacingHorizontalS },
  name: { flex: '1 1 auto', overflowWrap: 'anywhere' },
  add: { display: 'flex', alignItems: 'flex-end', gap: tokens.spacingHorizontalS },
  hint: { margin: 0, color: tokens.colorNeutralForeground3, overflowWrap: 'anywhere' },
});

export interface EventsEditorProps extends FormDesignCardProps {
  readOnly: boolean;
}

/**
 * The Events card (G6): the form's libraries in order (add from the JS web resources, move, remove: blocked while a handler
 * uses one), the OnLoad and OnSave handlers, the OnChange events of columns not on the form (H2: read-only, each can be
 * removed whole), and a count of the events the designer leaves alone.
 */
export function EventsEditor({ design, onChange, readOnly }: EventsEditorProps) {
  const styles = useStyles();
  const refocus = useRefocus();
  const [choice, setChoice] = useState('');
  /** The message of a blocked removal, with the design it was made for: any change of the design retires it. */
  const [blockedFor, setBlockedFor] = useState<{ text: string; design: typeof design }>();
  const blocked = blockedFor?.design === design ? blockedFor.text : undefined;
  /** The JavaScript web resources (type 3) the form can load: read when the library picker is first used. */
  const [wanted, setWanted] = useState(false);
  const resources = useLoad(wanted ? 'webresources-js' : undefined, (a) => listWebResources(a, [3]));
  const offered = (resources.data ?? []).filter((r) => !design.libraries.some((l) => lower(l.name) === lower(r.name)));
  const picked = offered.some((r) => r.name === choice) ? choice : '';
  const other = otherEvents(design);
  const orphans = orphanChangeEvents(design);
  const orphansId = useId();

  function remove(key: string, name: string) {
    const result = removeLibrary(design, key);
    if ('blockedBy' in result) {
      setBlockedFor({
        design,
        text: t('forms.events.library.blocked', { library: name, handlers: result.blockedBy.map((h) => h.functionName).join(', ') }),
      });
      return;
    }
    const at = design.libraries.findIndex((l) => l.key === key);
    const near = design.libraries[at + 1] ?? design.libraries[at - 1];
    refocus(near ? `${near.key}:remove` : 'lib:add');
    onChange(result);
  }

  return (
    <div className={styles.card}>
      <h4 className={styles.heading}>{t('forms.events.libraries')}</h4>
      {design.libraries.length === 0 && <p className={styles.hint}>{t('forms.events.libraries.none')}</p>}
      {design.libraries.length > 0 && (
        <ul className={styles.libraries}>
          {design.libraries.map((l, i) => (
            <li key={l.key} className={styles.library}>
              <span className={styles.name}>{l.name}</span>
              {!readOnly && (
                <>
                  <Button
                    appearance="subtle"
                    size="small"
                    icon={<PreviousPageIcon />}
                    data-refocus={`${l.key}:up`}
                    aria-label={t('forms.events.library.moveUp', { library: l.name })}
                    disabledFocusable={i === 0}
                    onClick={() => {
                      refocus(`${l.key}:up`);
                      onChange(moveLibrary(design, l.key, -1));
                    }}
                  />
                  <Button
                    appearance="subtle"
                    size="small"
                    icon={<NextPageIcon />}
                    data-refocus={`${l.key}:down`}
                    aria-label={t('forms.events.library.moveDown', { library: l.name })}
                    disabledFocusable={i === design.libraries.length - 1}
                    onClick={() => {
                      refocus(`${l.key}:down`);
                      onChange(moveLibrary(design, l.key, 1));
                    }}
                  />
                  <Button
                    appearance="subtle"
                    size="small"
                    icon={<DismissIcon />}
                    data-refocus={`${l.key}:remove`}
                    aria-label={t('forms.events.library.remove', { library: l.name })}
                    onClick={() => remove(l.key, l.name)}
                  />
                </>
              )}
            </li>
          ))}
        </ul>
      )}
      {blocked && (
        <MessageBar intent="warning">
          <MessageBarBody>{blocked}</MessageBarBody>
        </MessageBar>
      )}
      {!readOnly && (
        <>
          <LoadFailed error={resources.error} />
          <div className={styles.add}>
            <Field label={t('forms.events.library.pick')}>
              <Select value={picked} disabled={resources.error !== undefined} onFocus={() => setWanted(true)} onMouseDown={() => setWanted(true)} onChange={(_, d) => setChoice(d.value)}>
                <option value="">{t('forms.events.library.choose')}</option>
                {offered.map((r) => (
                  <option key={r.id} value={r.name}>
                    {r.name}
                  </option>
                ))}
              </Select>
            </Field>
            <Button
              icon={<NewIcon />}
              data-refocus="lib:add"
              disabledFocusable={resources.error !== undefined || picked === ''}
              onClick={() => {
                onChange(addLibrary(design, picked));
                setChoice('');
              }}
            >
              {t('forms.events.library.add')}
            </Button>
          </div>
        </>
      )}
      <h4 className={styles.heading}>{t('forms.events.onload')}</h4>
      <HandlerList design={design} onChange={onChange} readOnly={readOnly} event="onload" label={t('forms.events.onload')} />
      <h4 className={styles.heading}>{t('forms.events.onsave')}</h4>
      <HandlerList design={design} onChange={onChange} readOnly={readOnly} event="onsave" label={t('forms.events.onsave')} />
      {orphans.length > 0 && (
        <div role="group" aria-labelledby={orphansId} className={styles.card}>
          <h4 id={orphansId} className={styles.heading}>{t('forms.events.orphans')}</h4>
          <ul className={styles.libraries}>
            {orphans.map((e, i) => {
              const near = orphans[i + 1] ?? orphans[i - 1];
              return (
                <li key={e.key} className={styles.library}>
                  <span className={styles.name}>
                    {t('forms.events.orphan', {
                      column: e.attribute ?? '',
                      handlers: e.handlers.map((h) => h.functionName).join(', '),
                    })}
                  </span>
                  {!readOnly && (
                    <Button
                      appearance="subtle"
                      size="small"
                      icon={<DismissIcon />}
                      data-refocus={`${e.key}:remove`}
                      aria-label={t('forms.events.orphan.remove', { column: e.attribute ?? '' })}
                      onClick={() => {
                        refocus(near ? `${near.key}:remove` : 'lib:add');
                        onChange(dropEvent(design, e.key));
                      }}
                    />
                  )}
                </li>
              );
            })}
          </ul>
        </div>
      )}
      {other.length > 0 && <p className={styles.hint}>{t('forms.events.other', { count: other.length, names: other.join(', ') })}</p>}
    </div>
  );
}
