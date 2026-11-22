import { setUiLanguage, t } from './t';

describe('t', () => {
  it('returns the catalogue text', () => {
    expect(t('app.name')).toBe('XRMverse');
  });

  it('leaves text without placeholders untouched when args are given', () => {
    expect(t('app.name', { name: 'x' })).toBe('XRMverse');
  });
});

describe('t language', () => {
  afterEach(() => setUiLanguage(undefined));

  it('uses it-IT for uilanguageid 1040 and falls back to en-US for a missing key', () => {
    setUiLanguage(1040);
    expect(t('auth.signOut')).toBe('Esci');
    expect(t('nav.users')).toBe('Users');
    setUiLanguage(1031);
    expect(t('auth.signOut')).toBe('Sign out');
  });
});

describe('it-IT catalogue of the record forms', () => {
  afterEach(() => setUiLanguage(undefined));

  const placeholders = (text: string) => [...text.matchAll(/\{(\w+)\}/g)].map((m) => m[1]).sort();

  it('has every key the record forms reuse from the field editors, with the same placeholders', async () => {
    const { en } = await import('./en');
    const { it: italian } = await import('./it');
    const reused = (Object.keys(en) as (keyof typeof en)[]).filter(
      (k) => /^(field\.|form\.err\.|form\.partyList\.|file\.err\.)/.test(k) || k === 'form.title.new' || k === 'records.noSet',
    );
    expect(reused.length).toBeGreaterThan(40);
    for (const key of reused) {
      expect(italian[key], key).toBeDefined();
      expect(placeholders(italian[key]!), key).toEqual(placeholders(en[key]));
    }
    setUiLanguage(1040);
    expect(t('form.title.new', { table: 'Progetto' })).toBe('Nuovo Progetto');
  });
});
