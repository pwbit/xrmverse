import { renderHook } from '@testing-library/react';
import { createElement } from 'react';
import { describe, expect, it, vi } from 'vitest';
import { buildTheme } from '../renderers/modern/theme';
import { ThemeScope, useActiveTheme } from '../renderers/ThemeScope';
import { attr } from '../admin/tables/testAttrs';
import { standardControl } from '../test/pcf/controls';
import { boundParameter, buildContext, literalParameter, manifestTypeOf, updateContext, type PcfContextInput, type PcfView } from './context';
import { createFormatting } from './formatting';
import { parseManifest } from './manifest';

/* eslint-disable @typescript-eslint/no-explicit-any */

const view = (over: Partial<PcfView> = {}): PcfView => ({
  parameters: { value: { raw: 1, formatted: '1' } },
  mode: {
    isControlDisabled: false,
    isVisible: true,
    label: 'Value',
    allocatedWidth: -1,
    allocatedHeight: -1,
    contextInfo: { entityId: 'id-1', entityTypeName: 'xv_project', entityRecordName: 'Apollo' },
  },
  page: { entityId: 'id-1', entityTypeName: 'xv_project', isPageReadOnly: false },
  updatedProperties: [],
  ...over,
});

const input = (over: Partial<PcfContextInput> = {}): PcfContextInput => ({
  manifest: parseManifest(standardControl.manifest),
  controlName: standardControl.name,
  strings: { Value_Name: 'Value' },
  locale: 'en-US',
  userSettings: { userId: '{U}', languageId: 1033 },
  webAPI: { retrieveRecord: vi.fn() },
  navigation: { openForm: vi.fn() },
  utils: { getEntityMetadata: vi.fn(), hasEntityPrivilege: vi.fn(() => true), lookupObjects: vi.fn() },
  requestRender: vi.fn(),
  trackContainerResize: vi.fn(),
  setControlState: vi.fn(),
  ...over,
});

describe('buildContext', () => {
  it('gives_mode_contextInfo_and_keeps_the_object_in_step_with_the_record', () => {
    const ctx = buildContext(input(), view({ mode: { ...view().mode, contextInfo: { entityId: '', entityTypeName: 'xv_project', entityRecordName: '' } } })) as any;
    const info = ctx.mode.contextInfo;
    expect(info).toEqual({ entityId: '', entityTypeName: 'xv_project', entityRecordName: '' });
    updateContext(ctx, view());
    expect(ctx.mode.contextInfo).toBe(info);
    expect(info).toEqual({ entityId: 'id-1', entityTypeName: 'xv_project', entityRecordName: 'Apollo' });
  });

  it('gives_fluentDesignLanguage_from_the_shell_theme', () => {
    const ctx = buildContext(input(), view()) as any;
    expect(ctx.fluentDesignLanguage.isDarkTheme).toBe(false);
    expect(ctx.fluentDesignLanguage.tokenTheme.colorBrandBackground).toBeTruthy();
    expect(ctx.fluentDesignLanguage.brand[80]).toBe('#895B1F');
    expect(ctx.fluentDesignLanguage.typographyTokens.body1.fontSize).toBeTruthy();
  });

  it('gives_fluentDesignLanguage_from_the_active_theme_under_modern', () => {
    const ramp = Object.fromEntries([10, 20, 30, 40, 50, 60, 70, 80, 90, 100, 110, 120, 130, 140, 150, 160].map((k, i) => [k, `#20${(i * 16).toString(16).padStart(2, '0')}30`])) as any;
    const model = { theme: { brand: ramp, density: 'comfortable' as const }, whiteLabel: {}, views: {}, forms: {}, choices: {} };
    const light = buildContext(input({ theme: { theme: buildTheme(model, false), dark: false, brand: ramp } }), view()) as any;
    expect(light.fluentDesignLanguage.tokenTheme.colorBrandBackground).toBe(ramp[80]);
    expect(light.fluentDesignLanguage.brand).toBe(ramp);
    expect(light.fluentDesignLanguage.isDarkTheme).toBe(false);
    const dark = buildContext(input({ theme: { theme: buildTheme(model, true), dark: true, brand: ramp } }), view()) as any;
    expect(dark.fluentDesignLanguage.isDarkTheme).toBe(true);
    expect(dark.fluentDesignLanguage.tokenTheme.colorBrandForeground1).toBe(ramp[110]);
  });

  it('gives_fluentDesignLanguage_from_useActiveTheme_inside_a_theme_scope', () => {
    const ramp = Object.fromEntries([10, 20, 30, 40, 50, 60, 70, 80, 90, 100, 110, 120, 130, 140, 150, 160].map((k, i) => [k, `#30${(i * 16).toString(16).padStart(2, '0')}40`])) as any;
    const theme = buildTheme({ theme: { brand: ramp, density: 'comfortable' }, whiteLabel: {}, views: {}, forms: {}, choices: {} }, false);
    const { result } = renderHook(() => useActiveTheme(), { wrapper: ({ children }) => createElement(ThemeScope, { theme, brand: ramp, children }) });
    const ctx = buildContext(input({ theme: result.current }), view()) as any;
    expect(ctx.fluentDesignLanguage.tokenTheme.colorBrandBackground).toBe(ramp[80]);
  });

  it('exposes_the_view_and_keeps_one_object_across_updates', () => {
    const ctx = buildContext(input(), view()) as any;
    expect(ctx.parameters.value.raw).toBe(1);
    expect(ctx.mode.label).toBe('Value');
    const mode = ctx.mode;
    const same = updateContext(ctx, view({ parameters: { value: { raw: 2, formatted: '2' } }, updatedProperties: ['value'], mode: { ...view().mode, isControlDisabled: true } })) as any;
    expect(same).toBe(ctx);
    expect(ctx.mode).toBe(mode);
    expect(ctx.parameters.value.raw).toBe(2);
    expect(ctx.updatedProperties).toEqual(['value']);
    expect(ctx.mode.isControlDisabled).toBe(true);
  });

  it('resources_get_string_falls_back_to_the_key', () => {
    const ctx = buildContext(input(), view()) as any;
    expect(ctx.resources.getString('Value_Name')).toBe('Value');
    expect(ctx.resources.getString('Missing_Key')).toBe('Missing_Key');
  });

  it('get_resource_returns_an_img_file_as_base64', async () => {
    const fetchFile = vi.fn(async () => new Response(new Uint8Array([1, 2, 3])));
    const ctx = buildContext(input({ fetchFile }), view()) as any;
    const got = await new Promise((resolve, reject) => ctx.resources.getResource('img/icon.png', resolve, reject));
    expect(got).toBe('AQID');
    expect(fetchFile).toHaveBeenCalledWith('/Controls/xv_XvTest.Doubler/img/icon.png');
    const failed = vi.fn();
    await new Promise<void>((resolve) => ctx.resources.getResource('img/other.png', () => resolve(), () => (failed(), resolve())));
    expect(failed).toHaveBeenCalled();
  });

  it('host_members_and_state', () => {
    const i = input();
    const ctx = buildContext(i, view()) as any;
    ctx.factory.requestRender();
    expect(i.requestRender).toHaveBeenCalled();
    ctx.mode.trackContainerResize(true);
    expect(i.trackContainerResize).toHaveBeenCalledWith(true);
    expect(ctx.mode.setControlState({ a: 1 })).toBe(true);
    expect(i.setControlState).toHaveBeenCalledWith({ a: 1 });
    expect(ctx.webAPI).toBe(i.webAPI);
    expect(ctx.navigation).toBe(i.navigation);
    expect(ctx.utils.hasEntityPrivilege('account', 1, 0)).toBe(true);
    expect(ctx.userSettings.userId).toBe('{U}');
    ctx.client.disableScroll = true;
    expect(ctx.client.disableScroll).toBe(true);
  });

  it('unsupported_members_throw_not_available', () => {
    const ctx = buildContext(input(), view()) as any;
    for (const read of [() => ctx.device, () => ctx.events, () => ctx.copilot]) expect(read).toThrow(/is not available in this version/);
    expect(() => ctx.mode.setFullScreen(true)).toThrow(/context\.mode\.setFullScreen is not available/);
    expect(() => ctx.factory.getPopupService()).toThrow(/context\.factory\.getPopupService is not available/);
  });
});

describe('parameters', () => {
  const fmt = createFormatting('en-US', { currencySymbol: '$' });

  it('bound_currency_parameter', () => {
    const budget = attr('xv_budget', { kind: 'money', typeName: 'Money', displayName: 'Budget', precision: 2, requiredLevel: 'ApplicationRequired', minValue: 0, maxValue: 1000000 });
    const p = boundParameter(budget, '1234.5', { formatting: fmt, currencySymbol: '€', editable: true }) as any;
    expect(p.raw).toBe(1234.5);
    expect(p.formatted).toBe('€1,234.50');
    expect(p.type).toBe('Currency');
    expect(p.error).toBe(false);
    expect(p.errorMessage).toBe('');
    expect(p.security).toEqual({ editable: true, readable: true, secured: false });
    expect(p.attributes).toMatchObject({ DisplayName: 'Budget', LogicalName: 'xv_budget', RequiredLevel: 2, Precision: 2, MinValue: 0, MaxValue: 1000000, Type: 'Currency' });
  });

  it('bound_choice_lookup_and_empty_values', () => {
    const status = attr('xv_status', { kind: 'choice', typeName: 'Picklist', options: [{ value: 1, label: 'Open' }] });
    const choice = boundParameter(status, 1, { formatting: fmt, editable: false }) as any;
    expect(choice.raw).toBe(1);
    expect(choice.formatted).toBe('Open');
    expect(choice.attributes.Options).toEqual([{ Label: 'Open', Value: 1 }]);
    expect(choice.security.editable).toBe(false);
    const owner = attr('xv_parentid', { kind: 'lookup', typeName: 'Lookup', targets: ['account'] });
    const lookup = boundParameter(owner, { id: 'aaaaaaaa-0000-0000-0000-000000000001', target: 'account', name: 'Contoso' }, { formatting: fmt, editable: true }) as any;
    expect(lookup.raw).toEqual([{ id: '{AAAAAAAA-0000-0000-0000-000000000001}', name: 'Contoso', entityType: 'account' }]);
    expect(lookup.formatted).toBe('Contoso');
    const empty = boundParameter(attr('xv_n', { kind: 'decimal', typeName: 'Decimal' }), null, { formatting: fmt, editable: true }) as any;
    expect(empty.raw).toBeNull();
    expect(empty.formatted).toBe('');
  });

  it('literal_parameters_take_the_property_type', () => {
    expect(literalParameter('2', 'Whole.None')).toEqual({ raw: 2, formatted: '2', type: 'Whole.None' });
    expect(literalParameter('true', 'TwoOptions')).toEqual({ raw: true, formatted: 'true', type: 'TwoOptions' });
    expect(literalParameter('Hello', 'SingleLine.Text')).toEqual({ raw: 'Hello', formatted: 'Hello', type: 'SingleLine.Text' });
  });

  it('manifest_types_of_columns', () => {
    expect(manifestTypeOf(attr('a', { kind: 'decimal' }))).toBe('Decimal');
    expect(manifestTypeOf(attr('a', { kind: 'int' }))).toBe('Whole.None');
    expect(manifestTypeOf(attr('a', { kind: 'double' }))).toBe('FP');
    expect(manifestTypeOf(attr('a', { kind: 'bool' }))).toBe('TwoOptions');
    expect(manifestTypeOf(attr('a', { kind: 'datetime', dateOnly: true }))).toBe('DateAndTime.DateOnly');
    expect(manifestTypeOf(attr('a', { kind: 'string', format: 'Email' }))).toBe('SingleLine.Email');
    expect(manifestTypeOf(attr('a', { kind: 'memo' }))).toBe('Multiple');
  });
});

describe('formatting', () => {
  it('formats_numbers_and_dates', () => {
    const f = createFormatting('en-US', { currencySymbol: '$' });
    expect(f.formatCurrency(-5)).toBe('-$5.00');
    expect(f.formatDecimal(1.23456, 3)).toBe('1.235');
    expect(f.formatInteger(1234.4)).toBe('1,234');
    expect(f.getWeekOfYear(new Date(2026, 0, 1))).toBe(1);
    expect(f.formatDateShort(new Date(2026, 10, 16))).toBe('11/16/26');
  });
});
