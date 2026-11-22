import { describe, expect, it } from 'vitest';
import { localized } from '../model/sitemap';
import { parseSiteMap } from './siteMapXml';

const wrap = (subArea: string) =>
  `<SiteMap><Area Id="a"><Group Id="g">${subArea}</Group></Area></SiteMap>`;
const kindOf = (subArea: string) => parseSiteMap(wrap(subArea)).areas[0]!.groups[0]!.subAreas[0]!;

describe('parseSiteMap', () => {
  it('a Url="/main.aspx?pagetype=dashboard…" subarea is kind dashboard', () => {
    const sub = kindOf(
      '<SubArea Id="s" Url="/main.aspx?pagetype=dashboard&amp;id=%7b1%7d&amp;type=system"><Titles><Title LCID="1033" Title="D"/></Titles></SubArea>',
    );
    expect(sub.kind).toBe('dashboard');
    expect(sub.url).toBe('/main.aspx?pagetype=dashboard&id=%7b1%7d&type=system');
    expect(sub.titles).toEqual({ 1033: 'D' });
  });

  it('classifies entity, webresource, custom and other urls', () => {
    expect(kindOf('<SubArea Id="s" Entity="account"/>')).toMatchObject({ kind: 'entity', entity: 'account' });
    expect(kindOf('<SubArea Id="s" Url="$webresource:x_page.html"/>').kind).toBe('webresource');
    expect(kindOf('<SubArea Id="s" Url="/WebResources/x_page.html"/>').kind).toBe('webresource');
    expect(kindOf('<SubArea Id="s" Url="/main.aspx?pagetype=custom&amp;name=p"/>').kind).toBe('custom');
    expect(kindOf('<SubArea Id="s" Type="PageType"/>').kind).toBe('custom');
    expect(kindOf('<SubArea Id="s" Url="https://example.com"/>').kind).toBe('url');
  });

  it('keeps an entity subarea that also has an entitylist Url as entity', () => {
    const sub = kindOf('<SubArea Id="s" Entity="account" Url="/main.aspx?pagetype=entitylist&amp;etn=account"/>');
    expect(sub.kind).toBe('entity');
    expect(sub.url).toContain('pagetype=entitylist');
  });

  it('reads Privilege children', () => {
    const sub = kindOf('<SubArea Id="s" Entity="account"><Privilege Entity="role" Privilege="Read,Write"/></SubArea>');
    expect(sub.privileges).toEqual([{ entity: 'role', privilege: 'Read,Write' }]);
  });

  it('skips a Title whose LCID is missing, empty or not numeric', () => {
    const sub = kindOf(
      '<SubArea Id="s"><Titles><Title Title="a"/><Title LCID="" Title="b"/><Title LCID="x" Title="c"/><Title LCID="1033" Title="ok"/></Titles></SubArea>',
    );
    expect(sub.titles).toEqual({ 1033: 'ok' });
  });

  it('keeps document order for the first-title fallback', () => {
    const sub = kindOf(
      '<SubArea Id="s"><Titles><Title LCID="1040" Title="it"/><Title LCID="1031" Title="de"/></Titles></SubArea>',
    );
    expect(sub.firstLcid).toBe(1040);
    expect(localized(sub.titles, 1036, sub.id, sub.firstLcid)).toBe('it');
  });

  it('throws on malformed xml', () => {
    expect(() => parseSiteMap('<SiteMap><Area></SiteMap>')).toThrow();
  });
});

describe('localized', () => {
  it('falls back to 1033, then the first title, then the id', () => {
    expect(localized({ 1031: 'de', 1033: 'en' }, 1031, 'id')).toBe('de');
    expect(localized({ 1031: 'de', 1033: 'en' }, 1036, 'id')).toBe('en');
    expect(localized({ 1031: 'de', 1040: 'it' }, 1036, 'id')).toBe('de');
    expect(localized({}, 1033, 'id')).toBe('id');
  });
});
