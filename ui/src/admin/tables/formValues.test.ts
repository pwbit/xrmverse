import { describe, expect, it } from 'vitest';
import { changedBody, createBody, FieldError, valuesFromRecord, type LookupValue } from './formValues';
import { attr } from './testAttrs';

const fields = [
  attr('xv_name', { isPrimaryName: true }),
  attr('xv_count', { kind: 'int' }),
  attr('xv_flag', { kind: 'bool', options: [{ value: 1, label: 'Yes' }, { value: 0, label: 'No' }] }),
  attr('xv_prio', { kind: 'choice', options: [{ value: 1, label: 'High' }] }),
  attr('xv_tags', { kind: 'choices', options: [{ value: 1, label: 'A' }, { value: 2, label: 'B' }] }),
  attr('xv_due', { kind: 'datetime' }),
  attr('xv_day', { kind: 'datetime', dateOnly: true }),
  attr('xv_parent', { kind: 'lookup', targets: ['contact'] }),
];
const navFor = (a: string, target: string) => (a === 'xv_parent' && target === 'contact' ? 'xv_ParentId' : undefined);

const record = {
  xv_name: 'Alpha',
  xv_count: 3,
  xv_flag: true,
  xv_prio: 1,
  xv_tags: '1,2',
  xv_due: '2026-03-01T10:30:00Z',
  xv_day: '2026-03-02',
  _xv_parent_value: 'c1',
  '_xv_parent_value@Microsoft.Dynamics.CRM.lookuplogicalname': 'contact',
  '_xv_parent_value@OData.Community.Display.V1.FormattedValue': 'Ada',
};

describe('formValues', () => {
  it('reads a record into editor values', () => {
    const v = valuesFromRecord(fields, record, navFor);
    expect(v).toEqual({
      xv_name: 'Alpha',
      xv_count: '3',
      xv_flag: true,
      xv_prio: 1,
      xv_tags: [1, 2],
      xv_due: '2026-03-01T10:30',
      xv_day: '2026-03-02',
      xv_parent: { id: 'c1', target: 'contact', name: 'Ada', nav: 'xv_ParentId' },
    });
  });

  it('changedBody sends only what changed, with the lookup as a bind and clearing as null', () => {
    const original = valuesFromRecord(fields, record, navFor);
    expect(changedBody(fields, original, { ...original })).toEqual({});
    const picked: LookupValue = { id: 'c2', target: 'contact', set: 'contacts', name: 'Bo', nav: 'xv_ParentId' };
    const body = changedBody(fields, original, {
      ...original,
      xv_name: 'Beta',
      xv_count: '4',
      xv_tags: [2],
      xv_due: '2026-03-01T11:00',
      xv_prio: null,
      xv_parent: picked,
    });
    expect(body).toEqual({
      xv_name: 'Beta',
      xv_count: 4,
      xv_tags: '2',
      xv_due: '2026-03-01T11:00:00Z',
      xv_prio: null,
      'xv_ParentId@odata.bind': '/contacts(c2)',
    });
    expect(changedBody(fields, original, { ...original, xv_parent: null })).toEqual({ 'xv_ParentId@odata.bind': null });
  });

  it('createBody leaves out empty values and keeps false', () => {
    const body = createBody(fields, { xv_name: 'N', xv_count: '', xv_flag: false, xv_prio: null, xv_tags: [], xv_due: '', xv_day: '2026-01-05', xv_parent: null });
    expect(body).toEqual({ xv_name: 'N', xv_flag: false, xv_day: '2026-01-05' });
  });

  it('names the field when a number is not a number or a lookup has no navigation property', () => {
    expect(() => createBody(fields, { xv_count: '12x' })).toThrow(FieldError);
    expect(() => createBody(fields, { xv_parent: { id: 'c', target: 'contact', set: 'contacts' } })).toThrow(/relationship/i);
  });

  it('skips fields the mode does not allow', () => {
    const readOnlyOnUpdate = [attr('xv_once', { validForUpdate: false })];
    expect(changedBody(readOnlyOnUpdate, { xv_once: 'a' }, { xv_once: 'b' })).toEqual({});
    expect(createBody([attr('xv_calc', { validForCreate: false })], { xv_calc: 'x' })).toEqual({});
  });

  it('never sends a file or an image in the record body (they have their own upload)', () => {
    const files = [attr('xv_photo', { kind: 'image', typeName: 'Image' }), attr('xv_doc', { kind: 'file', typeName: 'File' })];
    expect(createBody(files, { xv_photo: 'AAAA', xv_doc: 'f1' })).toEqual({});
    expect(changedBody(files, { xv_photo: 'AAAA', xv_doc: 'f1' }, { xv_photo: null, xv_doc: null })).toEqual({});
  });

  describe('in the user\'s time zone (#131, ruling Z6)', () => {
    const ROME = 'Europe/Rome';
    const dates = [
      attr('xv_due', { kind: 'datetime' }),
      attr('xv_day', { kind: 'datetime', dateOnly: true }),
      attr('xv_checkin', { kind: 'datetime', behavior: 'TimeZoneIndependent' }),
      attr('xv_birthday', { kind: 'datetime', dateOnly: true, behavior: 'DateOnly' }),
    ];
    const stored = {
      xv_due: '2026-03-29T01:30:00Z',
      xv_day: '2026-07-01T22:00:00Z',
      xv_checkin: '2026-03-29T01:30:00Z',
      xv_birthday: '2026-07-01T00:00:00Z',
    };

    it('reads user local values as the zone shows them, the others as stored', () => {
      expect(valuesFromRecord(dates, stored, navFor, ROME)).toEqual({
        xv_due: '2026-03-29T03:30',
        xv_day: '2026-07-02',
        xv_checkin: '2026-03-29T01:30',
        xv_birthday: '2026-07-01',
      });
      // Without a zone (the web admin) every value is read as stored, in UTC.
      expect(valuesFromRecord(dates, stored, navFor)).toEqual({
        xv_due: '2026-03-29T01:30',
        xv_day: '2026-07-01',
        xv_checkin: '2026-03-29T01:30',
        xv_birthday: '2026-07-01',
      });
    });

    it('sends user local values as UTC instants, the others as typed', () => {
      const typed = { xv_due: '2026-03-29T03:30', xv_day: '2026-07-02', xv_checkin: '2026-03-29T01:30', xv_birthday: '2026-07-01' };
      expect(createBody(dates, typed, ROME)).toEqual({
        xv_due: '2026-03-29T01:30:00Z',
        xv_day: '2026-07-01T22:00:00Z',
        xv_checkin: '2026-03-29T01:30:00Z',
        xv_birthday: '2026-07-01',
      });
      expect(changedBody(dates, {}, { xv_due: '2026-10-25T02:30' }, new Set(), ROME)).toEqual({ xv_due: '2026-10-25T01:30:00Z' });
      // Unchanged without a zone.
      expect(createBody(dates, typed)).toEqual({
        xv_due: '2026-03-29T03:30:00Z',
        xv_day: '2026-07-02',
        xv_checkin: '2026-03-29T01:30:00Z',
        xv_birthday: '2026-07-01',
      });
    });

    it('a value the zone cannot read is kept as stored', () => {
      expect(valuesFromRecord([dates[0]!], { xv_due: 'not a date' }, navFor, ROME)).toEqual({ xv_due: 'not a date' });
    });
  });
});
