import { Button, Field, Input, MessageBar, MessageBarBody, Spinner, Switch } from '@fluentui/react-components';
import { useEffect, useState } from 'react';
import { useAuth } from '../../shell/auth/AuthProvider';
import { t } from '../../shell/i18n/t';
import { useNotify } from '../../shell/notify/Notifications';
import { loadHierarchySecurity, saveHierarchySecurity, type HierarchySecurity } from './hierarchySecurityApi';
import { PageHeader } from '../../shell/layout/PageHeader';

const messageOf = (e: unknown) => (e instanceof Error ? e.message : String(e));

/**
 * The manager hierarchy security model: switch it on or off, how many levels of reports a manager reaches, and the tables
 * left out of it. The server is the judge of every value; its refusal is shown as it is.
 */
export function HierarchySecurityPage() {
  const { api } = useAuth();
  const notify = useNotify();
  const [saved, setSaved] = useState<HierarchySecurity>();
  const [enabled, setEnabled] = useState(false);
  const [depth, setDepth] = useState('');
  const [excluded, setExcluded] = useState<string[]>([]);
  const [table, setTable] = useState('');
  const [loadError, setLoadError] = useState<string>();
  const [saveError, setSaveError] = useState<string>();
  const [busy, setBusy] = useState(false);

  function show(settings: HierarchySecurity) {
    setSaved(settings);
    setEnabled(settings.enabled);
    setDepth(String(settings.depth));
    setExcluded(settings.excludedTables);
  }

  useEffect(() => {
    if (!api) return;
    let live = true;
    loadHierarchySecurity(api)
      .then((s) => live && show(s))
      .catch((e: unknown) => live && setLoadError(messageOf(e)));
    return () => {
      live = false;
    };
  }, [api]);

  function addTable() {
    const name = table.trim();
    if (name && !excluded.some((x) => x.toLowerCase() === name.toLowerCase())) setExcluded([...excluded, name]);
    setTable('');
  }

  async function save() {
    if (!api) return;
    setBusy(true);
    setSaveError(undefined);
    try {
      // What is not a number is sent as 0, so that the server's range message answers (NaN would be sent as null: "unchanged").
      const number = depth.trim() === '' ? 0 : Number(depth);
      const result = await saveHierarchySecurity(api, { enabled, depth: Number.isFinite(number) ? number : 0, excludedTables: excluded });
      show(result);
      notify.success(t('hierarchySecurity.saved'));
    } catch (e) {
      setSaveError(messageOf(e));
    } finally {
      setBusy(false);
    }
  }

  return (
    <section>
      <PageHeader title={t('nav.hierarchySecurity')} />
      <p>{t('hierarchySecurity.intro')}</p>
      {loadError && (
        <MessageBar intent="error">
          <MessageBarBody>{t('hierarchySecurity.loadFailed', { detail: loadError })}</MessageBarBody>
        </MessageBar>
      )}
      {!saved && !loadError && <Spinner label={t('hierarchySecurity.loading')} />}
      {saved && (
        <form
          onSubmit={(e) => {
            e.preventDefault();
            void save();
          }}
          style={{ display: 'flex', flexDirection: 'column', gap: 12, maxWidth: 480 }}
        >
          <Switch checked={enabled} onChange={(_, d) => setEnabled(d.checked)} label={t('hierarchySecurity.enabled')} />
          <Field label={t('hierarchySecurity.depth')} hint={t('hierarchySecurity.depthHint')}>
            <Input value={depth} onChange={(_, d) => setDepth(d.value)} inputMode="numeric" />
          </Field>
          <Field label={t('hierarchySecurity.excluded')} hint={t('hierarchySecurity.excludedHint')}>
            <div style={{ display: 'flex', gap: 8 }}>
              <Input
                value={table}
                onChange={(_, d) => setTable(d.value)}
                onKeyDown={(e) => {
                  if (e.key === 'Enter') {
                    e.preventDefault();
                    addTable();
                  }
                }}
                aria-label={t('hierarchySecurity.table')}
                placeholder={t('hierarchySecurity.table')}
              />
              <Button type="button" onClick={addTable} disabled={!table.trim()}>
                {t('hierarchySecurity.add')}
              </Button>
            </div>
          </Field>
          {excluded.length === 0 ? (
            <span>{t('hierarchySecurity.none')}</span>
          ) : (
            <ul aria-label={t('hierarchySecurity.excluded')} style={{ margin: 0, paddingLeft: 20 }}>
              {excluded.map((name) => (
                <li key={name}>
                  {name}{' '}
                  <Button
                    type="button"
                    size="small"
                    appearance="subtle"
                    aria-label={t('hierarchySecurity.remove', { name })}
                    onClick={() => setExcluded(excluded.filter((x) => x !== name))}
                  >
                    {t('hierarchySecurity.removeButton')}
                  </Button>
                </li>
              ))}
            </ul>
          )}
          {saveError && (
            <MessageBar intent="error">
              <MessageBarBody>{saveError}</MessageBarBody>
            </MessageBar>
          )}
          <div>
            <Button type="submit" appearance="primary" disabled={busy}>
              {t('hierarchySecurity.save')}
            </Button>
          </div>
        </form>
      )}
    </section>
  );
}
