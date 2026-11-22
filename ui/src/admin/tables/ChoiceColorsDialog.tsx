import {
  Button,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  Field,
  makeStyles,
  MessageBar,
  MessageBarBody,
  Select,
  Spinner,
  Switch,
  tokens,
} from '@fluentui/react-components';
import { useEffect, useState } from 'react';
import { Link } from 'react-router-dom';
import { ChoiceBadge } from '../../renderers/modern/ChoiceBadge';
import { choiceKey, readableText } from '../../renderers/modern/choiceColors';
import { useAuth } from '../../shell/auth/AuthProvider';
import { t } from '../../shell/i18n/t';
import { CHOICE_DEFAULT_BACKGROUND } from '../../shell/theme';
import {
  choiceTextOf,
  listOverlays,
  loadOverlay,
  OverlayUnreadable,
  saveOverlay,
  withChoiceText,
  type OverlayChoice,
  type OverlayDoc,
} from '../views/overlayApi';
import type { AttributeMeta, OptionMeta } from './metadata';
import { loadGlobalChoices, publishOptions, updateOptionColor, type OptionTarget } from './tableApi';
import { useLoad } from './useLoad';

export interface ChoiceColorsDialogProps {
  table: string;
  /** A choice, choices or status column. */
  column: AttributeMeta;
  /** Closed with nothing written. */
  onClose: () => void;
  /**
   * The caller closes the dialog and reads the table again: everything changed is written (or nothing was), or the dialog
   * is closed after a failed save that had already written (and published) some backgrounds.
   */
  onSaved: () => void;
}

const useStyles = makeStyles({
  content: { display: 'flex', flexDirection: 'column', rowGap: tokens.spacingVerticalM },
  option: {
    display: 'grid',
    gridTemplateColumns: 'repeat(auto-fit, minmax(140px, 1fr))',
    columnGap: tokens.spacingHorizontalM,
    rowGap: tokens.spacingVerticalS,
    alignItems: 'end',
    margin: 0,
    border: 'none',
    borderTop: `1px solid ${tokens.colorNeutralStroke2}`,
    paddingTop: tokens.spacingVerticalS,
  },
  legend: { fontWeight: tokens.fontWeightSemibold, gridColumn: '1 / -1', padding: 0 },
  value: { color: tokens.colorNeutralForeground3, fontWeight: tokens.fontWeightRegular },
  swatch: { display: 'flex', alignItems: 'center', columnGap: tokens.spacingHorizontalS },
  colorInput: { width: '48px', height: '32px', padding: 0, border: `1px solid ${tokens.colorNeutralStroke1}`, borderRadius: tokens.borderRadiusMedium },
});

const message = (e: unknown) => (e instanceof Error ? e.message : String(e));
const isObject = (v: unknown): v is Record<string, unknown> => typeof v === 'object' && v !== null && !Array.isArray(v);
const sameColor = (a: string | undefined, b: string | undefined) => (a ?? '').toUpperCase() === (b ?? '').toUpperCase();

type Read =
  | { state: 'none' }
  | { state: 'loading' }
  | { state: 'ready'; doc: OverlayDoc }
  | { state: 'unreadable'; reason: string }
  | { state: 'failed'; detail: string };

/** Reads an overlay for its `choices`; one whose `choices` is not an object cannot be edited safely. */
async function readOverlay(api: Parameters<typeof loadOverlay>[0], name: string): Promise<OverlayDoc> {
  const doc = await loadOverlay(api, name);
  if (doc.json.choices !== undefined && !isObject(doc.json.choices)) throw new OverlayUnreadable(t('choiceColors.reason.choices'));
  return doc;
}

/**
 * The colours of a choice's values: the background (the option's native `Color`, written with `UpdateOptionValue` and
 * published) and the text (automatic, or an override in the presentation overlay picked here, under the choice's key).
 * Only what changed is written.
 */
export function ChoiceColorsDialog({ table, column, onClose, onSaved }: ChoiceColorsDialogProps) {
  const { api } = useAuth();
  const styles = useStyles();
  const globalName = column.globalOptionSet;
  const key = choiceKey(table, column.logicalName, globalName);
  // A global choice's options may be left out of the table's metadata: they are read from the global definitions then.
  const needsGlobal = !!globalName && column.options.length === 0;
  const globals = useLoad(needsGlobal ? `global:${globalName}` : undefined, (a) => loadGlobalChoices(a));
  const options: OptionMeta[] = needsGlobal ? (globals.data?.[globalName!] ?? []) : column.options;

  const [overlays, setOverlays] = useState<OverlayChoice[]>();
  const [listError, setListError] = useState<string>();
  const [selected, setSelected] = useState<string>();
  const [read, setRead] = useState<Read>({ state: 'loading' });
  /** Backgrounds as stored (updated as each one is written). */
  const [savedBackground, setSavedBackground] = useState<Record<number, string | undefined>>({});
  /** Background edits: a colour, or null for none. */
  const [backgrounds, setBackgrounds] = useState<Record<number, string | null>>({});
  /** Text edits: a colour, or null for automatic. */
  const [texts, setTexts] = useState<Record<number, string | null>>({});
  /** Options whose Automatic switch is off with no colour picked yet: the picker shows the automatic colour. */
  const [manual, setManual] = useState<ReadonlySet<number>>(new Set());
  const [busy, setBusy] = useState(false);
  /** Backgrounds are written but not yet published (a publish failed): the next Save publishes them. */
  const [needsPublish, setNeedsPublish] = useState(false);
  const [error, setError] = useState<string>();

  useEffect(() => {
    if (!api) return;
    let live = true;
    listOverlays(api)
      .then((list) => {
        if (!live) return;
        setOverlays(list);
        setSelected((s) => s ?? list[0]?.webResourceName);
        if (list.length === 0) setRead({ state: 'none' });
      })
      .catch((e: unknown) => {
        if (!live) return;
        setListError(message(e));
        setRead({ state: 'failed', detail: message(e) });
      });
    return () => {
      live = false;
    };
  }, [api]);

  useEffect(() => {
    if (!api || !selected) return;
    let live = true;
    setRead({ state: 'loading' });
    readOverlay(api, selected)
      .then((doc) => live && setRead({ state: 'ready', doc }))
      .catch((e: unknown) =>
        live && setRead(e instanceof OverlayUnreadable ? { state: 'unreadable', reason: e.reason } : { state: 'failed', detail: message(e) }),
      );
    return () => {
      live = false;
    };
  }, [api, selected]);

  const doc = read.state === 'ready' ? read.doc : undefined;
  const storedTexts = doc ? choiceTextOf(doc, key) : {};

  const storedBackground = (o: OptionMeta) => (o.value in savedBackground ? savedBackground[o.value] : o.color);
  const backgroundOf = (o: OptionMeta) => (o.value in backgrounds ? (backgrounds[o.value] ?? undefined) : storedBackground(o));
  const textOf = (o: OptionMeta) => (o.value in texts ? (texts[o.value] ?? undefined) : storedTexts[o.value]);
  /** The text colour shown: the override, else the automatic one while the switch is off. */
  const shownTextOf = (o: OptionMeta) => textOf(o) ?? (manual.has(o.value) ? readableText(backgroundOf(o) ?? CHOICE_DEFAULT_BACKGROUND) : undefined);
  /** The override to store: none when the colour picked is the automatic one for the background. */
  const overrideOf = (o: OptionMeta) => {
    const text = textOf(o);
    return text && !sameColor(text, readableText(backgroundOf(o) ?? CHOICE_DEFAULT_BACKGROUND)) ? text : undefined;
  };
  /** Some backgrounds are written: closing must read the table again, even after a failed save. */
  const wroteBackgrounds = Object.keys(savedBackground).length > 0;
  const close = () => {
    if (busy) return;
    if (wroteBackgrounds) onSaved();
    else onClose();
  };

  const choose = (name: string) => {
    // Text colours belong to an overlay: edits made for another one are dropped.
    setTexts({});
    setManual(new Set());
    setError(undefined);
    setSelected(name);
  };

  async function save() {
    if (!api) return;
    setBusy(true);
    setError(undefined);
    try {
      const target: OptionTarget = globalName ? { optionSetName: globalName } : { table, column: column.logicalName };
      let pending = needsPublish;
      try {
        for (const o of options) {
          const wanted = backgroundOf(o);
          if (sameColor(wanted, storedBackground(o))) continue;
          await updateOptionColor(api, target, o.value, wanted ?? null);
          pending = true;
          setNeedsPublish(true);
          setSavedBackground((s) => ({ ...s, [o.value]: wanted }));
        }
      } catch (e) {
        // What was written is published all the same, so the table shows it; the first error is the one reported.
        if (pending) {
          await publishOptions(api, target).then(
            () => setNeedsPublish(false),
            (publishError: unknown) => console.warn(`Choice colours ${column.logicalName}: publish failed`, publishError),
          );
        }
        throw e;
      }
      if (pending) {
        await publishOptions(api, target);
        setNeedsPublish(false);
      }
      const changedTexts = options.filter((o) => o.value in texts && !sameColor(overrideOf(o), storedTexts[o.value]));
      if (changedTexts.length > 0 && selected && doc) {
        // Read again just before writing, so nothing else in the overlay is lost.
        const fresh = await readOverlay(api, selected);
        const next = changedTexts.reduce((d, o) => withChoiceText(d, key, o.value, overrideOf(o)), fresh);
        if (next !== fresh) await saveOverlay(api, next);
        setRead({ state: 'ready', doc: next });
        setTexts({});
        setManual(new Set());
      }
      onSaved();
    } catch (e) {
      setError(t('choiceColors.saveFailed', { detail: message(e) }));
    } finally {
      setBusy(false);
    }
  }

  const textReady = read.state === 'ready';
  const overlayNote = (() => {
    if (listError) return <MessageBar intent="error"><MessageBarBody>{t('choiceColors.loadFailed', { detail: listError })}</MessageBarBody></MessageBar>;
    if (!overlays) return <Spinner size="small" label={t('common.loading')} />;
    if (overlays.length === 0)
      return (
        <p>
          <span>{t('choiceColors.noOverlay')}</span> <Link to="/manage/environment">{t('views.modes.environmentLink')}</Link>
        </p>
      );
    return (
      <>
        <Field label={t('views.modes.overlay')}>
          <Select value={selected} onChange={(_, d) => choose(d.value)} disabled={busy}>
            {overlays.map((o) => (
              <option key={o.webResourceName} value={o.webResourceName}>
                {o.label}
              </option>
            ))}
          </Select>
        </Field>
        {read.state === 'loading' && <Spinner size="small" label={t('common.loading')} />}
        {read.state === 'failed' && (
          <MessageBar intent="error">
            <MessageBarBody>{t('choiceColors.loadFailed', { detail: read.detail })}</MessageBarBody>
          </MessageBar>
        )}
        {read.state === 'unreadable' && (
          <MessageBar intent="warning">
            <MessageBarBody>{t('choiceColors.unreadable', { reason: read.reason })}</MessageBarBody>
          </MessageBar>
        )}
      </>
    );
  })();

  return (
    <Dialog open onOpenChange={(_, d) => !d.open && close()}>
      <DialogSurface>
        <DialogBody>
          <DialogTitle>{t('choiceColors.title', { column: column.displayName })}</DialogTitle>
          <DialogContent className={styles.content}>
            <p>{t('choiceColors.intro')}</p>
            {globalName && <p>{t('choiceColors.global', { name: globalName })}</p>}
            {overlayNote}
            {error && (
              <MessageBar intent="error">
                <MessageBarBody>{error}</MessageBarBody>
              </MessageBar>
            )}
            {globals.loading && <Spinner size="small" label={t('common.loading')} />}
            {globals.error && (
              <MessageBar intent="error">
                <MessageBarBody>{globals.error}</MessageBarBody>
              </MessageBar>
            )}
            {!globals.loading && options.length === 0 && <p>{t('choiceColors.noOptions')}</p>}
            {options.map((o) => {
              const background = backgroundOf(o);
              const text = shownTextOf(o);
              const shown = background ?? CHOICE_DEFAULT_BACKGROUND;
              return (
                <fieldset key={o.value} className={styles.option} aria-label={o.label}>
                  <legend className={styles.legend}>
                    {o.label} <span className={styles.value}>({o.value})</span>
                  </legend>
                  <Field label={t('choiceColors.background')}>
                    {(props) => (
                      <div className={styles.swatch}>
                        <input
                          {...props}
                          type="color"
                          className={styles.colorInput}
                          value={shown.toLowerCase()}
                          disabled={busy}
                          onChange={(e) => setBackgrounds((b) => ({ ...b, [o.value]: e.target.value.toUpperCase() }))}
                        />
                        <Button size="small" disabled={!background || busy} onClick={() => setBackgrounds((b) => ({ ...b, [o.value]: null }))}>
                          {t('choiceColors.noColour')}
                        </Button>
                      </div>
                    )}
                  </Field>
                  <Switch
                    label={t('choiceColors.automatic')}
                    checked={!text}
                    disabled={!textReady || busy}
                    onChange={(_, d) => {
                      setManual((m) => {
                        const next = new Set(m);
                        if (d.checked) next.delete(o.value);
                        else next.add(o.value);
                        return next;
                      });
                      // Switched off, nothing is written until a text colour is picked.
                      if (d.checked) setTexts((x) => ({ ...x, [o.value]: null }));
                    }}
                  />
                  {text && (
                    <Field label={t('choiceColors.text')}>
                      {(props) => (
                        <input
                          {...props}
                          type="color"
                          className={styles.colorInput}
                          value={text.toLowerCase()}
                          disabled={!textReady || busy}
                          onChange={(e) => setTexts((x) => ({ ...x, [o.value]: e.target.value.toUpperCase() }))}
                        />
                      )}
                    </Field>
                  )}
                  <div className={styles.swatch}>
                    <span className={styles.value}>{t('choiceColors.preview')}</span>
                    <ChoiceBadge label={o.label} background={shown} text={text ?? readableText(shown)} />
                  </div>
                </fieldset>
              );
            })}
          </DialogContent>
          <DialogActions>
            <Button appearance="secondary" disabled={busy} onClick={close}>
              {t('common.cancel')}
            </Button>
            <Button appearance="primary" disabled={busy || !api} onClick={() => void save()}>
              {t('choiceColors.save')}
            </Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}
