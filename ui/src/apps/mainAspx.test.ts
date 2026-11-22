import { describe, expect, it } from 'vitest';
import { mainAspxUrl, parseMainAspx } from './mainAspx';

describe('parseMainAspx', () => {
  it('reads an entity list URL (parameter names in any case, braces dropped)', () => {
    expect(
      parseMainAspx('?appid=%7BAAAA-1%7D&pagetype=entitylist&etn=account&viewid=%7b61ad%7d&viewType=1039'),
    ).toEqual({ appId: 'aaaa-1', pageType: 'entitylist', etn: 'account', viewId: '61ad', viewType: 1039 });
    expect(parseMainAspx('AppId=x&PageType=EntityRecord&etn=account&id=1')).toEqual({
      appId: 'x',
      pageType: 'entityrecord',
      etn: 'account',
      id: '1',
    });
  });

  it('reads the formid of a record page and writes it back', () => {
    const state = { appId: 'a1', pageType: 'entityrecord', etn: 'account', id: 'r1', formId: 'f1' };
    expect(parseMainAspx('?appid=a1&pagetype=entityrecord&etn=account&id=r1&FormId=%7BF1%7D')).toEqual(state);
    expect(mainAspxUrl(state)).toBe('/main.aspx?appid=a1&pagetype=entityrecord&etn=account&id=r1&formid=f1');
  });

  it('has an empty page type for the app home', () => {
    expect(parseMainAspx('?appid=x')).toEqual({ appId: 'x', pageType: '' });
    expect(parseMainAspx('')).toEqual({ pageType: '' });
  });

  it('reads the web resource and custom page names', () => {
    expect(parseMainAspx('?pagetype=webresource&webresourceName=x_page.html').name).toBe('x_page.html');
    expect(parseMainAspx('?pagetype=custom&name=x_page').name).toBe('x_page');
  });
});

describe('mainAspxUrl', () => {
  it('writes the parameters in the Dataverse order and round-trips', () => {
    const state = { appId: 'a1', pageType: 'entitylist', etn: 'account', viewId: 'v1', viewType: 4230 };
    const url = mainAspxUrl(state);
    expect(url).toBe('/main.aspx?appid=a1&pagetype=entitylist&etn=account&viewid=v1&viewType=4230');
    expect(parseMainAspx(url.slice(url.indexOf('?')))).toEqual(state);
  });

  it('writes the app home and a web resource', () => {
    expect(mainAspxUrl({ appId: 'a1', pageType: '' })).toBe('/main.aspx?appid=a1');
    expect(mainAspxUrl({ appId: 'a1', pageType: 'webresource', name: 'x.html' })).toBe(
      '/main.aspx?appid=a1&pagetype=webresource&webresourceName=x.html',
    );
  });
});
