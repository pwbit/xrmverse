import { Button, makeStyles, tokens } from '@fluentui/react-components';
import { useState } from 'react';
import { t } from '../../shell/i18n/t';
import { DismissIcon, NewIcon, NextPageIcon, PreviousPageIcon } from '../../shell/icons';
import { lower } from './formCells';
import type { FormDesign } from './formDesign';
import { addHandler, moveHandler, removeHandler, setHandler, type DesignEvent, type DesignHandler, type HandlerFields } from './formEvents';
import { useRefocus } from './formDesignerSlots';
import { HandlerFieldsEditor } from './HandlerFieldsEditor';

const useStyles = makeStyles({
  list: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalS },
  handler: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXS,
    padding: tokens.spacingHorizontalS,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
  },
  actions: { display: 'flex', gap: tokens.spacingHorizontalXS },
  hint: { margin: 0, color: tokens.colorNeutralForeground3 },
  start: { alignSelf: 'flex-start' },
});

/** A new handler's fields before the maker sets them (H6): no parameters, enabled, without the execution context. */
const DEFAULTS = { parameters: '', enabled: true, passContext: false } as const;

export interface HandlerListProps {
  design: FormDesign;
  onChange: (next: FormDesign) => void;
  readOnly: boolean;
  /** The event the list edits: the form's OnLoad or OnSave, or the OnChange of `attribute`. */
  event: DesignEvent['name'];
  attribute?: string;
  /** The name of the list (its group). */
  label: string;
}

interface RowProps {
  design: FormDesign;
  handler: DesignHandler;
  at: number;
  count: number;
  readOnly: boolean;
  onChange: (next: FormDesign) => void;
  refocus: (id: string) => void;
  /** The `data-refocus` value that gets the focus when this handler is removed: a neighbour, else Add, else the list (A4). */
  afterRemove: string;
}

/** One handler: its fields are edited in place; a blank function name is shown as invalid and is not applied. */
function HandlerRow({ design, handler, at, count, readOnly, onChange, refocus, afterRemove }: RowProps) {
  const styles = useStyles();
  const [name, setName] = useState(handler.functionName);
  const patch = (p: Partial<HandlerFields>) => onChange(setHandler(design, handler.key, p));
  const onForm = design.libraries.some((l) => lower(l.name) === lower(handler.library));
  const libraries = onForm ? design.libraries : [{ key: '', name: handler.library, uniqueId: '' }, ...design.libraries];
  /** A handler whose library is not on the form cannot be edited until one is chosen (the model refuses the edit). */
  const locked = readOnly || !onForm;
  const label = handler.functionName;
  return (
    <div role="group" aria-label={t('forms.events.handler', { function: label })} className={styles.handler}>
      <HandlerFieldsEditor
        name={name}
        onName={(value) => {
          setName(value);
          if (value.trim() !== '') patch({ functionName: value });
        }}
        // What the input shows is what Save writes: a blank name is not applied, so it reverts to the model's value.
        onNameBlur={() => setName(handler.functionName)}
        nameValidation={name.trim() === '' ? t('forms.events.functionRequired') : undefined}
        fields={handler}
        libraries={libraries}
        locked={locked}
        libraryLocked={readOnly}
        libraryHint={!onForm && !readOnly && <p className={styles.hint}>{t('forms.events.libraryMissing', { library: handler.library })}</p>}
        onPatch={patch}
      />
      {!readOnly && (
        <div className={styles.actions}>
          <Button
            appearance="subtle"
            size="small"
            icon={<PreviousPageIcon />}
            data-refocus={`${handler.key}:up`}
            aria-label={t('forms.events.handler.moveUp', { function: label })}
            disabledFocusable={at === 0}
            onClick={() => {
              refocus(`${handler.key}:up`);
              onChange(moveHandler(design, handler.key, -1));
            }}
          />
          <Button
            appearance="subtle"
            size="small"
            icon={<NextPageIcon />}
            data-refocus={`${handler.key}:down`}
            aria-label={t('forms.events.handler.moveDown', { function: label })}
            disabledFocusable={at === count - 1}
            onClick={() => {
              refocus(`${handler.key}:down`);
              onChange(moveHandler(design, handler.key, 1));
            }}
          />
          <Button
            appearance="subtle"
            size="small"
            icon={<DismissIcon />}
            data-refocus={`${handler.key}:remove`}
            aria-label={t('forms.events.handler.remove', { function: label })}
            onClick={() => {
              refocus(afterRemove);
              onChange(removeHandler(design, handler.key));
            }}
          />
        </div>
      )}
    </div>
  );
}

/**
 * The handlers of one event (G6): the form's OnLoad or OnSave, or the OnChange of a column. Each handler can be edited,
 * moved and removed; a new one takes a library of the form and a required function name. Shared by the Events card and the
 * properties of a field cell.
 */
export function HandlerList({ design, onChange, readOnly, event, attribute, label }: HandlerListProps) {
  const styles = useStyles();
  const refocus = useRefocus();
  const handlers = design.events.find((e) => e.name === event && lower(e.attribute) === lower(event === 'onchange' ? attribute : undefined))?.handlers ?? [];
  const [name, setName] = useState('');
  const [draft, setDraft] = useState<Omit<HandlerFields, 'functionName'>>({ library: '', ...DEFAULTS });
  const [tried, setTried] = useState(false);
  const chosen = design.libraries.some((l) => l.name === draft.library) ? draft.library : (design.libraries[0]?.name ?? '');
  const fields: HandlerFields = { ...draft, functionName: name, library: chosen };
  const addId = `${event}:${lower(attribute)}:add`;
  const listId = `${event}:${lower(attribute)}:list`;
  /** Where the focus goes when no handler is left: the Add button, or the list itself on a form without libraries (A4). */
  const lastRemoved = design.libraries.length > 0 ? addId : listId;
  const missing = tried && name.trim() === '';

  function add() {
    if (name.trim() === '') {
      setTried(true);
      return;
    }
    onChange(addHandler(design, event, attribute, fields));
    // Back to the defaults (H6); the library chosen stays.
    setName('');
    setDraft({ library: chosen, ...DEFAULTS });
    setTried(false);
  }

  return (
    <div role="group" aria-label={label} className={styles.list} tabIndex={-1} data-refocus={listId}>
      {handlers.length === 0 && <p className={styles.hint}>{t('forms.events.handlers.none')}</p>}
      {handlers.map((h, i) => (
        <HandlerRow
          key={h.key}
          design={design}
          handler={h}
          at={i}
          count={handlers.length}
          readOnly={readOnly}
          onChange={onChange}
          refocus={refocus}
          afterRemove={handlers[i + 1] || handlers[i - 1] ? `${(handlers[i + 1] ?? handlers[i - 1]).key}:remove` : lastRemoved}
        />
      ))}
      {!readOnly &&
        (design.libraries.length === 0 ? (
          <p className={styles.hint}>{t('forms.events.noLibraries')}</p>
        ) : (
          <div role="group" aria-label={t('forms.events.handler.new')} className={styles.handler}>
            <HandlerFieldsEditor
              name={name}
              onName={setName}
              nameValidation={missing ? t('forms.events.functionRequired') : undefined}
              fields={fields}
              libraries={design.libraries}
              locked={false}
              libraryLocked={false}
              onPatch={(p) => setDraft((f) => ({ ...f, ...p }))}
            />
            <Button className={styles.start} icon={<NewIcon />} data-refocus={addId} onClick={add}>
              {t('forms.events.handler.add')}
            </Button>
          </div>
        ))}
    </div>
  );
}
