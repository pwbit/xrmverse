import { Button, Combobox, Field, makeStyles, MessageBar, MessageBarBody, Option, Select, Spinner, Title3, tokens } from '@fluentui/react-components';
import { useEffect, useState } from 'react';
import { PRESENTATION, RENDERER } from '../../renderers/lookSettings';
import { useAuth } from '../../shell/auth/AuthProvider';
import { t } from '../../shell/i18n/t';
import { refreshEnvironmentLook } from '../../shell/layout/useEnvironmentOverlay';
import { useNotify } from '../../shell/notify/Notifications';

/** `webresourcetype`: HTML, CSS, script, XML and SVG (11): the types a presentation JSON can be kept in (XSL 9 and RESX 12 are not offered). */
const TEXT_TYPES = [1, 2, 3, 4, 11];

interface Definition {
  settingdefinitionid: string;
  uniquename: string;
  defaultvalue?: string | null;
}
interface Row {
  organizationsettingid: string;
  _settingdefinitionid_value: string;
  value: string | null;
}

const useStyles = makeStyles({
  form: { display: 'flex', flexDirection: 'column', rowGap: tokens.spacingVerticalM, maxWidth: '480px', marginBottom: tokens.spacingVerticalL },
});

const same = (a: string, b: string) => a.toLowerCase() === b.toLowerCase();

/**
 * "Model-driven apps look" (UI-2a): the environment's default look and presentation web resource, kept as `organizationsetting`
 * rows (at most one per definition): created when absent, updated when present, deleted when cleared. Only System
 * Administrators and System Customizers may write them; the API's error is shown as it is.
 */
export function LookSettings() {
  const { api } = useAuth();
  const notify = useNotify();
  const styles = useStyles();
  const [definitions, setDefinitions] = useState<Definition[]>();
  const [rows, setRows] = useState<Row[]>([]);
  const [resources, setResources] = useState<string[]>([]);
  const [renderer, setRenderer] = useState('classic');
  const [presentation, setPresentation] = useState('');
  const [error, setError] = useState<string>();
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    if (!api) return;
    let live = true;
    (async () => {
      const defs = (
        await api.get<{ value: Definition[] }>(
          `settingdefinitions?$select=settingdefinitionid,uniquename,defaultvalue&$filter=uniquename eq '${RENDERER}' or uniquename eq '${PRESENTATION}'`,
        )
      ).value;
      const ids = defs.map((d) => `_settingdefinitionid_value eq ${d.settingdefinitionid}`).join(' or ');
      const rowList = ids
        ? (await api.get<{ value: Row[] }>(`organizationsettings?$select=organizationsettingid,_settingdefinitionid_value,value&$filter=${ids}`)).value
        : [];
      if (!live) return;
      const def = (n: string) => defs.find((d) => same(d.uniquename, n));
      const valueOf = (n: string) => {
        const d = def(n);
        return d ? rowList.find((r) => same(r._settingdefinitionid_value, d.settingdefinitionid))?.value ?? undefined : undefined;
      };
      setDefinitions(defs);
      setRows(rowList);
      setRenderer(valueOf(RENDERER) || def(RENDERER)?.defaultvalue || 'classic');
      setPresentation(valueOf(PRESENTATION) ?? '');
    })().catch((e: unknown) => live && setError(t('environment.look.loadFailed', { detail: e instanceof Error ? e.message : String(e) })));
    // The web resource list is separate: if it fails, only the combobox stays empty (the error is shown) and the look still loads and saves.
    (async () => {
      const types = TEXT_TYPES.map((n) => `webresourcetype eq ${n}`).join(' or ');
      const files = (await api.get<{ value: { name: string }[] }>(`webresourceset?$select=name&$filter=${types}&$orderby=name`)).value;
      if (live) setResources(files.map((f) => f.name));
    })().catch((e: unknown) => live && setError(t('environment.look.loadFailed', { detail: e instanceof Error ? e.message : String(e) })));
    return () => {
      live = false;
    };
  }, [api]);

  const save = async () => {
    if (!api || !definitions) return;
    setBusy(true);
    let current = rows;
    try {
      for (const [name, value] of [[RENDERER, renderer], [PRESENTATION, presentation]] as const) {
        const def = definitions.find((d) => same(d.uniquename, name));
        if (!def) continue;
        const row = current.find((r) => same(r._settingdefinitionid_value, def.settingdefinitionid));
        if (value === '') {
          if (!row) continue;
          await api.delete(`organizationsettings(${row.organizationsettingid})`);
          current = current.filter((r) => r !== row);
        } else if (!row) {
          // No row means the definition's default: choosing the default needs no row.
          if (value === (def.defaultvalue ?? '')) continue;
          const created = await api.post('organizationsettings', {
            displayname: name,
            uniquename: name,
            value,
            'settingdefinitionid@odata.bind': `/settingdefinitions(${def.settingdefinitionid})`,
          });
          current = [...current, { organizationsettingid: created.id ?? '', _settingdefinitionid_value: def.settingdefinitionid, value }];
        } else if (row.value !== value) {
          await api.patch(`organizationsettings(${row.organizationsettingid})`, { value });
          current = current.map((r) => (r === row ? { ...r, value } : r));
        }
      }
      notify.success(t('environment.look.saved'));
    } catch (e: unknown) {
      notify.error(e);
    } finally {
      // The web admin re-reads the environment's overlay (R6), also after a partial save: it shows what is stored.
      refreshEnvironmentLook();
      setRows(current);
      setBusy(false);
    }
  };

  const known: [string, string][] = [
    ['classic', t('personalization.look.classic')],
    ['modern', t('personalization.look.modern')],
  ];
  const looks = known.some(([v]) => v === renderer) ? known : [...known, [renderer, renderer] as [string, string]];
  const names = presentation && !resources.includes(presentation) ? [presentation, ...resources] : resources;
  return (
    <section aria-labelledby="look-title">
      <Title3 as="h3" block id="look-title">{t('environment.look.title')}</Title3>
      <p>{t('environment.look.intro')}</p>
      {error && (
        <MessageBar intent="error">
          <MessageBarBody>{error}</MessageBarBody>
        </MessageBar>
      )}
      {!definitions && !error && <Spinner />}
      {definitions && (
        <div className={styles.form}>
          <Field label={t('environment.look.renderer')}>
            <Select value={renderer} onChange={(_, d) => setRenderer(d.value)}>
              {looks.map(([value, label]) => (
                <option key={value} value={value}>
                  {label}
                </option>
              ))}
            </Select>
          </Field>
          <Field label={t('environment.look.presentation')}>
            <Combobox
              value={presentation}
              selectedOptions={[presentation]}
              onOptionSelect={(_, d) => setPresentation(d.optionValue ?? '')}
              onChange={(e) => e.target.value === '' && setPresentation('')}
            >
              <Option value="">{t('environment.look.presentationNone')}</Option>
              {names.map((n) => (
                <Option key={n} value={n}>
                  {n}
                </Option>
              ))}
            </Combobox>
          </Field>
          <div>
            <Button appearance="primary" disabled={busy} onClick={() => void save()}>
              {t('environment.look.save')}
            </Button>
          </div>
        </div>
      )}
    </section>
  );
}
